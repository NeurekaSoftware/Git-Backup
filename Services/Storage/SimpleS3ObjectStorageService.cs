using System.Formats.Tar;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;
using GitBackup.Configuration.Models;
using GitBackup.Runtime;
using GitBackup.Services.Paths;
using Genbox.SimpleS3.Core.Abstracts;
using Genbox.SimpleS3.Core.Abstracts.Enums;
using Genbox.SimpleS3.Core.Abstracts.Response;
using Genbox.SimpleS3.Core.Common.Authentication;
using Genbox.SimpleS3.Core.Common.Exceptions;
using Genbox.SimpleS3.Core.Extensions;
using Genbox.SimpleS3.Core.Network.Responses.S3Types;
using Genbox.SimpleS3.Extensions.HttpClientFactory.Polly;
using Genbox.SimpleS3.Extensions.HttpClientFactory.Polly.Extensions;
using Genbox.SimpleS3.GenericS3.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace GitBackup.Services.Storage;

public sealed class SimpleS3ObjectStorageService : IObjectStorageService
{
    private const string ArchiveContentType = "application/gzip";
    private const string JsonContentType = "application/json";
    private const int MultipartPartSizeBytes = 16 * 1024 * 1024;
    private const int MultipartParallelParts = 4;

    private readonly ServiceProvider _serviceProvider;
    private readonly ISimpleClient _client;
    private readonly string _bucket;

    public SimpleS3ObjectStorageService(StorageConfig storage)
    {
        _bucket = Require(storage.Bucket, "storage.bucket");
        var endpoint = Require(storage.Endpoint, "storage.endpoint");
        var region = Require(storage.Region, "storage.region");
        var accessKeyId = Require(storage.AccessKeyId, "storage.accessKeyId");
        var secretAccessKey = Require(storage.SecretAccessKey, "storage.secretAccessKey");

        var requestedPathStyle = storage.ForcePathStyle == true;
        var payloadSignatureMode = ResolvePayloadSignatureMode(storage.PayloadSignatureMode);
        var resolvedEndpoint = ResolveEndpoint(endpoint, _bucket, requestedPathStyle);

        var services = new ServiceCollection();
        var clientBuilder = services.AddGenericS3(config =>
        {
            config.Credentials = new StringAccessKey(accessKeyId, secretAccessKey);
            config.Endpoint = resolvedEndpoint;
            config.RegionCode = region;
            config.NamingMode = requestedPathStyle ? NamingMode.PathStyle : NamingMode.VirtualHost;
            config.PayloadSignatureMode = payloadSignatureMode;
            config.ThrowExceptionOnError = true;
        });

        // The library retries timeouts and 5xx with exponential backoff + jitter. The handler below
        // additionally covers 429 and the other non-2xx codes the library misreports as success.
        clientBuilder.HttpBuilder.UseRetryAndTimeout(polly =>
        {
            polly.Retries = 5;
            polly.RetryMode = RetryMode.ExponentialBackoffJitter;
            polly.MaxRandomDelay = TimeSpan.FromSeconds(1);
            polly.Timeout = TimeSpan.FromMinutes(15);
        });

        // Install the guard outermost so it observes every response — single PUT, each multipart
        // part, each list page — after the library's own inner retries.
        clientBuilder.HttpBuilder.Services.Configure<HttpClientFactoryOptions>(
            clientBuilder.HttpBuilder.Name,
            options => options.HttpMessageHandlerBuilderActions.Add(
                builder => builder.AdditionalHandlers.Insert(0, new NoSilentSuccessHandler())));

        _serviceProvider = services.BuildServiceProvider();
        _client = _serviceProvider.GetRequiredService<ISimpleClient>();

        AppLogger.Info("Object storage client initialized. provider={Provider}.", "GenericS3");
        AppLogger.Debug(
            "Object storage settings: endpoint={Endpoint}, resolvedEndpoint={ResolvedEndpoint}, region={Region}, bucket={Bucket}, forcePathStyle={ForcePathStyle}, payloadSignatureMode={PayloadSignatureMode}.",
            endpoint,
            resolvedEndpoint,
            region,
            _bucket,
            requestedPathStyle,
            payloadSignatureMode);
    }

    public async Task UploadDirectoryAsTarGzAsync(
        string localDirectory,
        string objectKey,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(localDirectory))
        {
            throw new DirectoryNotFoundException($"Directory '{localDirectory}' does not exist.");
        }

        var normalizedObjectKey = objectKey.Trim('/');
        if (string.IsNullOrWhiteSpace(normalizedObjectKey))
        {
            throw new ArgumentException("Object key is required.", nameof(objectKey));
        }

        cancellationToken.ThrowIfCancellationRequested();
        AppLogger.Debug(
            "Streaming archive upload. localDirectory={LocalDirectory}, objectKey={ObjectKey}.",
            localDirectory,
            normalizedObjectKey);

        // Stream tar -> gzip -> multipart through a bounded pipe: no temp .tar.gz on disk, and peak
        // RAM is just the in-flight part buffers (partSize x (parallelism + 1)). Only the bare git
        // clone stays on disk.
        var pipe = new Pipe();
        var produceTask = Task.Run(
            async () =>
            {
                Exception? failure = null;
                try
                {
                    await using var writerStream = pipe.Writer.AsStream(leaveOpen: true);
                    await using var gzipStream = new GZipStream(writerStream, CompressionLevel.SmallestSize);
                    TarFile.CreateFromDirectory(localDirectory, gzipStream, includeBaseDirectory: false);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                await pipe.Writer.CompleteAsync(failure);
            },
            cancellationToken);

        try
        {
            await using var readerStream = pipe.Reader.AsStream();
            await ExecuteAsync(
                "upload archive object",
                normalizedObjectKey,
                () => _client.MultipartUploadAsync(
                    _bucket,
                    normalizedObjectKey,
                    readerStream,
                    MultipartPartSizeBytes,
                    MultipartParallelParts,
                    config: request => request.ContentType.Set(ArchiveContentType),
                    token: cancellationToken));
        }
        catch
        {
            await pipe.Reader.CompleteAsync();
            try
            {
                await produceTask;
            }
            catch
            {
                // The upload failure is the primary error; a producer failure is secondary.
            }

            throw;
        }

        await produceTask;
        AppLogger.Info("Archive uploaded. objectKey={ObjectKey}.", normalizedObjectKey);
    }

    public async Task UploadTextAsync(string objectKey, string content, CancellationToken cancellationToken)
    {
        var normalizedObjectKey = objectKey.Trim('/');
        var bytes = Encoding.UTF8.GetBytes(content);
        AppLogger.Debug("Uploading text object. objectKey={ObjectKey}, bytes={ByteCount}.", normalizedObjectKey, bytes.Length);

        await ExecuteAsync(
            "upload text object",
            normalizedObjectKey,
            async () =>
            {
                using var stream = new MemoryStream(bytes, writable: false);
                await _client.PutObjectAsync(
                    _bucket,
                    normalizedObjectKey,
                    stream,
                    request => request.ContentType.Set(JsonContentType),
                    cancellationToken);
            });
    }

    public async Task<IReadOnlyList<string>> ListObjectKeysAsync(string prefix, CancellationToken cancellationToken)
    {
        var normalizedPrefix = StorageKeyBuilder.EnsurePrefix(prefix);
        AppLogger.Debug("Listing object keys. prefix={Prefix}.", normalizedPrefix);

        var keys = new List<string>();
        try
        {
            await foreach (S3Object item in _client.ListAllObjectsAsync(_bucket, normalizedPrefix, false, cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(item.ObjectKey))
                {
                    keys.Add(item.ObjectKey);
                }
            }
        }
        catch (S3RequestException exception)
        {
            throw ReportFailure("list objects", normalizedPrefix, exception);
        }

        AppLogger.Info("Object key listing completed. prefix={Prefix}, keyCount={KeyCount}.", normalizedPrefix, keys.Count);
        return keys;
    }

    public async Task DeleteObjectsAsync(IEnumerable<string> objectKeys, CancellationToken cancellationToken)
    {
        var keys = objectKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim('/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (keys.Length == 0)
        {
            AppLogger.Debug("No object deletions needed.");
            return;
        }

        AppLogger.Info("Deleting objects. count={ObjectCount}.", keys.Length);
        var useSingleObjectDeletes = false;

        foreach (var batch in keys.Chunk(1000))
        {
            if (useSingleObjectDeletes)
            {
                await DeleteObjectsIndividuallyAsync(batch, cancellationToken);
                continue;
            }

            AppLogger.Debug("Deleting object batch. batchSize={BatchSize}.", batch.Length);
            try
            {
                await ExecuteAsync(
                    "delete objects",
                    $"{batch.Length} keys",
                    () => _client.DeleteObjectsAsync(_bucket, batch, _ => { }, cancellationToken));
            }
            catch (S3RequestException exception) when (IsBulkDeleteSchemaError(exception))
            {
                AppLogger.Warn(
                    "Bulk delete was rejected by the storage provider. Switching to single-object deletes. batchSize={BatchSize}, error={ErrorMessage}.",
                    batch.Length,
                    exception.Message);
                useSingleObjectDeletes = true;
                await DeleteObjectsIndividuallyAsync(batch, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        // Disposes the DI container, which owns the HttpClientFactory infrastructure and handlers.
        _serviceProvider.Dispose();
    }

    private async Task DeleteObjectsIndividuallyAsync(IEnumerable<string> objectKeys, CancellationToken cancellationToken)
    {
        var deletedCount = 0;
        foreach (var objectKey in objectKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteAsync(
                "delete object",
                objectKey,
                () => _client.DeleteObjectAsync(_bucket, objectKey, _ => { }, cancellationToken));
            deletedCount++;
        }

        AppLogger.Debug("Single-object delete batch completed. batchSize={BatchSize}.", deletedCount);
    }

    /// <summary>
    /// Runs an S3 operation under a single, uniform failure model. Every genuine failure reaches here
    /// as an <see cref="S3RequestException"/> — the client throws on its own error codes, and
    /// <see cref="NoSilentSuccessHandler"/> converts the codes it would otherwise misreport as
    /// success — so this just translates it into a domain error logged with context. Bulk-delete
    /// schema errors are rethrown untouched for the caller's per-object fallback.
    /// </summary>
    private static async Task ExecuteAsync(string operation, string objectKey, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (S3RequestException exception) when (IsBulkDeleteSchemaError(exception))
        {
            throw;
        }
        catch (S3RequestException exception)
        {
            throw ReportFailure(operation, objectKey, exception);
        }
    }

    private static InvalidOperationException ReportFailure(string operation, string objectKey, S3RequestException exception)
    {
        var detail = DescribeError(exception.Response);
        AppLogger.Error(
            exception,
            "Object storage operation failed. operation={Operation}, objectKey={ObjectKey}, detail={ErrorDetail}.",
            operation,
            objectKey,
            detail);
        return new InvalidOperationException($"storage: failed to {operation} '{objectKey}'. {detail}", exception);
    }

    private static string DescribeError(IResponse? response)
    {
        var error = response?.Error;
        if (error is null)
        {
            return $"statusCode={response?.StatusCode}";
        }

        var details = error.GetErrorDetails();
        return string.IsNullOrWhiteSpace(details)
            ? $"{error.Code}: {error.Message}"
            : $"{error.Code}: {error.Message} ({details})";
    }

    private static bool IsBulkDeleteSchemaError(S3RequestException exception)
    {
        var message = exception.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("MalformedXML", StringComparison.OrdinalIgnoreCase)
               || message.Contains("XML you provided was not well formed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("did not validate against our published schema", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveEndpoint(string configuredEndpoint, string bucket, bool forcePathStyle)
    {
        var endpoint = configuredEndpoint.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return endpoint;
        }

        if (endpoint.Contains('{'))
        {
            return endpoint.TrimEnd('/');
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return endpoint.TrimEnd('/');
        }

        if (forcePathStyle)
        {
            return endpoint.TrimEnd('/');
        }

        var host = uri.Host;
        var bucketPrefix = $"{bucket}.";
        if (host.StartsWith(bucketPrefix, StringComparison.OrdinalIgnoreCase))
        {
            host = host[bucketPrefix.Length..];
        }

        var authority = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        var path = uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme}://{{Bucket}}.{authority}{path}";
    }

    private static SignatureMode ResolvePayloadSignatureMode(string? configuredMode)
    {
        return configuredMode?.Trim().ToLowerInvariant() switch
        {
            "streaming" => SignatureMode.StreamingSignature,
            "unsigned" => SignatureMode.Unsigned,
            _ => SignatureMode.FullSignature
        };
    }

    private static string Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Storage configuration '{name}' is required.", name);
        }

        return value;
    }
}
