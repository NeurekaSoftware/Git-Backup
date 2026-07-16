using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using GitBackup.Configuration.Models;
using GitBackup.Runtime;
using GitBackup.Services.Paths;
using Genbox.SimpleS3.Core.Abstracts.Enums;
using Genbox.SimpleS3.Core.Abstracts.Response;
using Genbox.SimpleS3.Core.Common.Authentication;
using Genbox.SimpleS3.Core.Common.Exceptions;
using Genbox.SimpleS3.Core.Extensions;
using Genbox.SimpleS3.Core.Network.Responses.S3Types;
using Genbox.SimpleS3.Extensions.GenericS3;
using Genbox.SimpleS3.Extensions.HttpClientFactory.Polly;
using Genbox.SimpleS3.GenericS3;
using Genbox.SimpleS3.ProviderBase;

namespace GitBackup.Services.Storage;

public sealed class SimpleS3ObjectStorageService : IObjectStorageService
{
    private const string ArchiveContentType = "application/gzip";
    private const string JsonContentType = "application/json";
    private const int RateLimitStatusCode = 429;
    private const int RateLimitRetryLimit = 5;
    private const double RateLimitMaxBackoffSeconds = 30;

    private readonly GenericS3Client _client;
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
        var alwaysCalculateContentMd5 = storage.AlwaysCalculateContentMd5 == true;
        var resolvedEndpoint = ResolveEndpoint(endpoint, _bucket, requestedPathStyle);

        var config = new GenericS3Config
        {
            Credentials = new StringAccessKey(accessKeyId, secretAccessKey),
            Endpoint = resolvedEndpoint,
            RegionCode = region,
            NamingMode = requestedPathStyle ? NamingMode.PathStyle : NamingMode.VirtualHost,
            PayloadSignatureMode = payloadSignatureMode,
            AlwaysCalculateContentMd5 = alwaysCalculateContentMd5,
            ThrowExceptionOnError = true
        };

        // The library retries timeouts and 5xx (including 503 SlowDown) with exponential backoff
        // and jitter via this config. It does NOT cover a literal 429 (which it reports as a
        // successful response), so that case is handled explicitly in SendAsync below.
        var networkConfig = new NetworkConfig
        {
            Retries = 5,
            RetryMode = RetryMode.ExponentialBackoffJitter,
            MaxRandomDelay = TimeSpan.FromSeconds(1),
            Timeout = TimeSpan.FromMinutes(15)
        };

        _client = new GenericS3Client(config, networkConfig);

        AppLogger.Info("Object storage client initialized. provider={Provider}.", "GenericS3");
        AppLogger.Debug(
            "Object storage settings: endpoint={Endpoint}, resolvedEndpoint={ResolvedEndpoint}, region={Region}, bucket={Bucket}, forcePathStyle={ForcePathStyle}, payloadSignatureMode={PayloadSignatureMode}, alwaysCalculateContentMd5={AlwaysCalculateContentMd5}, retries={Retries}.",
            endpoint,
            resolvedEndpoint,
            region,
            _bucket,
            requestedPathStyle,
            payloadSignatureMode,
            alwaysCalculateContentMd5,
            networkConfig.Retries);
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

        AppLogger.Debug(
            "Preparing archive upload. localDirectory={LocalDirectory}, objectKey={ObjectKey}.",
            localDirectory,
            normalizedObjectKey);
        string? temporaryArchivePath = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            temporaryArchivePath = Path.Combine(Path.GetTempPath(), $"git-backup-{Guid.NewGuid():N}.tar.gz");
            AppLogger.Debug(
                "Creating temporary archive before upload. localDirectory={LocalDirectory}, temporaryArchivePath={TemporaryArchivePath}.",
                localDirectory,
                temporaryArchivePath);
            await using (var archiveFileStream = File.Create(temporaryArchivePath))
            await using (var gzipStream = new GZipStream(archiveFileStream, CompressionLevel.SmallestSize))
            {
                TarFile.CreateFromDirectory(localDirectory, gzipStream, includeBaseDirectory: false);
            }

            // Multipart upload lifts the single-PUT size ceiling for large repositories. A fresh
            // seekable stream is opened per attempt so a rate-limit retry re-sends the full body.
            var archivePath = temporaryArchivePath;
            await SendAsync(
                "upload archive object",
                normalizedObjectKey,
                cancellationToken,
                async () =>
                {
                    await using var uploadStream = File.OpenRead(archivePath);
                    return await _client.MultipartUploadAsync(
                        _bucket,
                        normalizedObjectKey,
                        uploadStream,
                        config: request => request.ContentType.Set(ArchiveContentType),
                        token: cancellationToken);
                });

            AppLogger.Info("Archive uploaded. objectKey={ObjectKey}.", normalizedObjectKey);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryArchivePath))
            {
                TryDeleteTemporaryFile(temporaryArchivePath);
            }
        }
    }

    public async Task UploadTextAsync(string objectKey, string content, CancellationToken cancellationToken)
    {
        var normalizedObjectKey = objectKey.Trim('/');
        var bytes = Encoding.UTF8.GetBytes(content);
        AppLogger.Debug("Uploading text object. objectKey={ObjectKey}, bytes={ByteCount}.", normalizedObjectKey, bytes.Length);

        await SendAsync(
            "upload text object",
            normalizedObjectKey,
            cancellationToken,
            async () =>
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return await _client.PutObjectAsync(
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
                await SendAsync(
                    "delete objects",
                    $"{batch.Length} keys",
                    cancellationToken,
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
        // Releases the internal HttpClient/handler owned by the client's service provider.
        _client.Dispose();
    }

    private async Task DeleteObjectsIndividuallyAsync(IEnumerable<string> objectKeys, CancellationToken cancellationToken)
    {
        var deletedCount = 0;
        foreach (var objectKey in objectKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendAsync(
                "delete object",
                objectKey,
                cancellationToken,
                () => _client.DeleteObjectAsync(_bucket, objectKey, _ => { }, cancellationToken));
            deletedCount++;
        }

        AppLogger.Debug("Single-object delete batch completed. batchSize={BatchSize}.", deletedCount);
    }

    /// <summary>
    /// Runs an S3 operation and enforces a real success outcome. Two things the SimpleS3 client
    /// does are handled here: it throws <see cref="S3RequestException"/> on genuine errors (403,
    /// 5xx after its own retries), which are translated into a domain error logged with context;
    /// and it reports a literal <c>429</c> as a <i>successful</i> response instead of throwing, so
    /// a rate-limited request is detected from the status code and backed off/retried here. Any
    /// other non-2xx status that slips through as "success" is treated as a failure rather than
    /// silently accepted (which would report a write that never happened as done).
    /// </summary>
    private static async Task<TResponse> SendAsync<TResponse>(
        string operation,
        string objectKey,
        CancellationToken cancellationToken,
        Func<Task<TResponse>> action)
        where TResponse : IResponse
    {
        for (var attempt = 0; ; attempt++)
        {
            TResponse response;
            try
            {
                response = await action();
            }
            catch (S3RequestException exception) when (IsBulkDeleteSchemaError(exception))
            {
                // Let the caller handle the bulk-delete compatibility fallback.
                throw;
            }
            catch (S3RequestException exception)
            {
                throw ReportFailure(operation, objectKey, exception);
            }

            if (response.StatusCode == RateLimitStatusCode && attempt < RateLimitRetryLimit)
            {
                await DelayForRateLimitAsync(operation, objectKey, attempt, cancellationToken);
                continue;
            }

            if (response.StatusCode is < 200 or >= 300)
            {
                throw ReportUnexpectedStatus(operation, objectKey, response);
            }

            return response;
        }
    }

    private static async Task DelayForRateLimitAsync(string operation, string objectKey, int attempt, CancellationToken cancellationToken)
    {
        var backoffSeconds = Math.Min(Math.Pow(2, attempt), RateLimitMaxBackoffSeconds);
        var delay = TimeSpan.FromSeconds(backoffSeconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
        AppLogger.Warn(
            "Storage request was rate limited (429). Backing off before retry. operation={Operation}, objectKey={ObjectKey}, attempt={Attempt}, delaySeconds={DelaySeconds}.",
            operation,
            objectKey,
            attempt + 1,
            Math.Round(delay.TotalSeconds, 1));
        await Task.Delay(delay, cancellationToken);
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

    private static InvalidOperationException ReportUnexpectedStatus(string operation, string objectKey, IResponse response)
    {
        var detail = DescribeError(response);
        AppLogger.Error(
            "Object storage operation failed. operation={Operation}, objectKey={ObjectKey}, detail={ErrorDetail}.",
            operation,
            objectKey,
            detail);
        return new InvalidOperationException($"storage: failed to {operation} '{objectKey}'. {detail}");
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

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warn(
                "Failed to remove temporary archive. path={Path}, error={ErrorMessage}.",
                path,
                exception.Message);
        }
    }
}
