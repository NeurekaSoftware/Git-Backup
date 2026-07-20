using System.Formats.Tar;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;
using GitBackup.Configuration.Models;
using GitBackup.Runtime;
using GitBackup.Services.Paths;
using Genbox.HttpBuilders.Enums;
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
    private const string JsonContentType = "application/json";
    private const int MultipartPartSizeBytes = 16 * 1024 * 1024;

    // Number of parts uploaded concurrently. Peak upload RAM is partSize x (parallelParts + 1), so 2
    // holds ~48 MiB in flight instead of ~80 MiB at 4 — without changing the part size, which would
    // split each archive into more upload requests. Multiplied by concurrency.repositories.
    private const int MultipartParallelParts = 2;

    // Largest payload sent as a single PutObject. Below this the whole body is held in memory briefly,
    // which is cheaper than a multipart round trip; above it, streaming multipart keeps peak memory flat.
    private const int SinglePutMaxBytes = 5 * 1024 * 1024;

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

        // The library retries timeouts and 5xx with exponential backoff + jitter, but only over a
        // sub-second window. The handler below owns the durable, wider transient retry — 429, 5xx, and
        // the other non-2xx codes the library misreports as success — since it buffers the request body
        // and so can safely re-send an upload part after a provider blip that outlasts this inner retry.
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

        AppLogger.Debug("Object storage client initialized. provider={Provider}.", "GenericS3");
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

        var normalizedObjectKey = NormalizeObjectKey(objectKey, nameof(objectKey));

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
                    // A bare git mirror is almost entirely packfile data that git has already
                    // zlib-compressed, so max-effort deflate spends far more CPU for negligible extra
                    // reduction. Because this stream throttles the S3 upload to the compression rate,
                    // Fastest keeps snapshot wall-clock down at near-identical object size.
                    await using var gzipStream = new GZipStream(writerStream, CompressionLevel.Fastest);
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

            // Deliberately no Content-Type on the multipart path. The initiate request (POST ?uploads)
            // has no body, and .NET can only attach Content-Type to a request body — so the header is
            // dropped on the wire while SimpleS3 still lists content-type in the SigV4 SignedHeaders. A
            // strict S3 provider (e.g. Backblaze B2) then rejects the request with "header 'content-type'
            // is listed in signed headers, but is not present". The stored object gets the default type.
            await ExecuteAsync(
                "upload archive object",
                normalizedObjectKey,
                () => _client.MultipartUploadAsync(
                    _bucket,
                    normalizedObjectKey,
                    readerStream,
                    MultipartPartSizeBytes,
                    MultipartParallelParts,
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
        // A metadata document is small and already in memory, so a single PutObject is cheaper than a
        // multipart round trip (initiate + part + complete).
        var normalizedObjectKey = NormalizeObjectKey(objectKey, nameof(objectKey));

        // Store the JSON gzip-compressed with Content-Encoding: gzip. Issue/MR documents and manifests
        // are repetitive text that gzips several times over, cutting upload egress and stored size; a
        // client that honors Content-Encoding still reads plain JSON, and the object key stays *.json.
        AppLogger.Debug(
            "Uploading object. objectKey={ObjectKey}, contentType={ContentType}, contentEncoding=gzip.",
            normalizedObjectKey,
            JsonContentType);

        using var stream = GzipUtf8(content);
        await ExecuteAsync(
            "upload object",
            normalizedObjectKey,
            () => _client.PutObjectAsync(
                _bucket,
                normalizedObjectKey,
                stream,
                request =>
                {
                    request.ContentType.Set(JsonContentType);
                    request.ContentEncoding.Add(ContentEncodingType.Gzip);
                },
                cancellationToken));
    }

    // Returns the compressed bytes as a rewound stream ready to upload. Handing the buffer over directly
    // avoids copying the whole document out with ToArray only to wrap it in a second MemoryStream —
    // which for a busy issue thread or a large collection manifest is a copy worth not making.
    private static MemoryStream GzipUtf8(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }

        output.Position = 0;
        return output;
    }

    public async Task UploadStreamAsync(
        string objectKey,
        Stream content,
        string contentType,
        long? knownLength,
        CancellationToken cancellationToken)
    {
        var normalizedObjectKey = NormalizeObjectKey(objectKey, nameof(objectKey));

        var resolvedContentType = string.IsNullOrWhiteSpace(contentType) ? MimeTypeResolver.DefaultContentType : contentType;

        // Most attachments are small — a screenshot pasted into an issue. Multipart costs three round
        // trips (initiate + part + complete) and reads through a buffer sized to the full part size, so
        // for those it is far more expensive than a single PutObject over a right-sized buffer. Take the
        // multipart path only when the payload is genuinely large, or when the length is unknown and it
        // therefore might be.
        if (knownLength is { } length && length <= SinglePutMaxBytes)
        {
            AppLogger.Debug(
                "Uploading object. objectKey={ObjectKey}, contentType={ContentType}, bytes={Bytes}.",
                normalizedObjectKey,
                resolvedContentType,
                length);

            using var buffer = new MemoryStream((int)length);
            await content.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;

            await ExecuteAsync(
                "upload object",
                normalizedObjectKey,
                () => _client.PutObjectAsync(
                    _bucket,
                    normalizedObjectKey,
                    buffer,
                    request => request.ContentType.Set(resolvedContentType),
                    cancellationToken));
            return;
        }

        AppLogger.Debug(
            "Streaming object upload. objectKey={ObjectKey}.",
            normalizedObjectKey);

        // Upload straight from the (non-seekable) source stream via multipart — the same path the
        // archive uses — so an attachment of unknown or large size is never buffered fully in memory.
        // Content-Type is omitted for the same reason as the archive path: the bodyless multipart
        // initiate can't carry it, and signing it breaks strict S3 providers. Smaller attachments take
        // the single-PutObject branch above, which does record their Content-Type.
        await ExecuteAsync(
            "upload object",
            normalizedObjectKey,
            () => _client.MultipartUploadAsync(
                _bucket,
                normalizedObjectKey,
                content,
                MultipartPartSizeBytes,
                MultipartParallelParts,
                token: cancellationToken));
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

        AppLogger.Debug("Object key listing completed. prefix={Prefix}, keyCount={KeyCount}.", normalizedPrefix, keys.Count);
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

        AppLogger.Debug("Deleting objects. count={ObjectCount}.", keys.Length);
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
        // The constructor already rejected a blank endpoint, so this only has to normalize a real one.
        var endpoint = configuredEndpoint.Trim();

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
        return PayloadSignatureModes.Normalize(configuredMode) switch
        {
            PayloadSignatureModes.Streaming => SignatureMode.StreamingSignature,
            PayloadSignatureModes.Unsigned => SignatureMode.Unsigned,
            _ => SignatureMode.FullSignature
        };
    }

    private static string Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // No paramName: `name` is a settings path, not an argument, and passing it as one both
            // breaks the ArgumentException contract and repeats the path in the rendered message.
            throw new ArgumentException($"Storage configuration '{name}' is required.");
        }

        return value;
    }

    /// <summary>
    /// Trims the surrounding slashes an object key must not carry and rejects an empty one. Shared by
    /// every upload path so the bucket's only writers cannot disagree about what a valid key is.
    /// </summary>
    private static string NormalizeObjectKey(string objectKey, string paramName)
    {
        var normalized = objectKey.Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Object key is required.", paramName);
        }

        return normalized;
    }
}
