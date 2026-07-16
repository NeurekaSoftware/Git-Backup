using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using GitBackup.Configuration.Models;
using GitBackup.Runtime;
using GitBackup.Services.Paths;
using Genbox.SimpleS3.Core.Abstracts.Clients;
using Genbox.SimpleS3.Core.Abstracts.Enums;
using Genbox.SimpleS3.Core.Abstracts.Response;
using Genbox.SimpleS3.Core.Common.Authentication;
using Genbox.SimpleS3.Core.Common.Exceptions;
using Genbox.SimpleS3.Core.Extensions;
using Genbox.SimpleS3.Extensions.GenericS3;
using Genbox.SimpleS3.GenericS3;
using Genbox.SimpleS3.ProviderBase;

namespace GitBackup.Services.Storage;

public sealed class SimpleS3ObjectStorageService : IObjectStorageService
{
    private readonly IObjectClient _objectClient;
    private readonly string _bucket;

    public SimpleS3ObjectStorageService(StorageConfig storage)
    {
        _bucket = storage.Bucket!;

        var requestedPathStyle = storage.ForcePathStyle == true;
        var payloadSignatureMode = ResolvePayloadSignatureMode(storage.PayloadSignatureMode);
        var alwaysCalculateContentMd5 = storage.AlwaysCalculateContentMd5 == true;
        var resolvedEndpoint = ResolveEndpoint(storage.Endpoint!, _bucket, requestedPathStyle);

        var config = new GenericS3Config
        {
            Credentials = new StringAccessKey(storage.AccessKeyId!, storage.SecretAccessKey!),
            Endpoint = resolvedEndpoint,
            RegionCode = storage.Region!,
            NamingMode = requestedPathStyle ? NamingMode.PathStyle : NamingMode.VirtualHost,
            PayloadSignatureMode = payloadSignatureMode,
            AlwaysCalculateContentMd5 = alwaysCalculateContentMd5,
            ThrowExceptionOnError = true
        };

        var client = new GenericS3Client(config, new NetworkConfig());

        _objectClient = client;
        AppLogger.Info("Object storage client initialized. provider=GenericS3");
        AppLogger.Debug(
            "Object storage settings: endpoint={Endpoint}, resolvedEndpoint={ResolvedEndpoint}, region={Region}, bucket={Bucket}, forcePathStyle={ForcePathStyle}, payloadSignatureMode={PayloadSignatureMode}, alwaysCalculateContentMd5={AlwaysCalculateContentMd5}.",
            storage.Endpoint,
            resolvedEndpoint,
            storage.Region,
            storage.Bucket,
            storage.ForcePathStyle,
            payloadSignatureMode,
            alwaysCalculateContentMd5);
    }

    public async Task<ArchiveUploadResult> UploadDirectoryAsTarGzAsync(
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

            await using var stream = File.OpenRead(temporaryArchivePath);
            var response = await _objectClient.PutObjectAsync(
                _bucket,
                normalizedObjectKey,
                stream,
                _ => { },
                cancellationToken);
            EnsureSuccess(response, $"upload archive object '{normalizedObjectKey}'");
            AppLogger.Info("Archive uploaded. objectKey={ObjectKey}.", normalizedObjectKey);

            return new ArchiveUploadResult
            {
                ObjectKey = normalizedObjectKey
            };
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
        AppLogger.Debug("Uploading text object. objectKey={ObjectKey}, bytes={ByteCount}.", normalizedObjectKey, Encoding.UTF8.GetByteCount(content));
        var bytes = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(bytes, writable: false);
        var response = await _objectClient.PutObjectAsync(_bucket, normalizedObjectKey, stream, _ => { }, cancellationToken);
        EnsureSuccess(response, $"upload text object '{normalizedObjectKey}'");
    }

    public async Task<string?> GetTextIfExistsAsync(string objectKey, CancellationToken cancellationToken)
    {
        var normalizedObjectKey = objectKey.Trim('/');
        if (string.IsNullOrWhiteSpace(normalizedObjectKey))
        {
            throw new ArgumentException("Object key is required.", nameof(objectKey));
        }

        try
        {
            using var response = await _objectClient.GetObjectAsync(_bucket, normalizedObjectKey, _ => { }, cancellationToken);
            EnsureSuccess(response, $"download text object '{normalizedObjectKey}'");

            using var reader = new StreamReader(response.Content, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (S3RequestException exception) when (IsNotFound(exception.Response))
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListObjectKeysAsync(string prefix, CancellationToken cancellationToken)
    {
        var normalizedPrefix = StorageKeyBuilder.EnsurePrefix(prefix);
        AppLogger.Debug("Listing object keys. prefix={Prefix}", normalizedPrefix);
        var keys = new List<string>();

        await foreach (var item in ObjectClientExtensions
                           .ListAllObjectsAsync(_objectClient, _bucket, normalizedPrefix, false, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            var key = ExtractObjectKey(item);
            if (!string.IsNullOrWhiteSpace(key))
            {
                keys.Add(key);
            }
        }

        AppLogger.Info("Object key listing completed. prefix={Prefix}, keyCount={KeyCount}.", normalizedPrefix, keys.Count);
        return keys;
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        AppLogger.Info("Deleting objects under prefix={Prefix}.", prefix);
        var objectKeys = await ListObjectKeysAsync(prefix, cancellationToken);
        await DeleteObjectsAsync(objectKeys, cancellationToken);
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
                await ObjectClientExtensions.DeleteObjectsAsync(_objectClient, _bucket, batch, _ => { }, cancellationToken);
            }
            catch (S3RequestException exception) when (IsBulkDeleteSchemaError(exception))
            {
                AppLogger.Warn(
                    "Bulk delete request was rejected by the storage provider. Switching to single-object deletes. batchSize={BatchSize}, error={ErrorMessage}.",
                    batch.Length,
                    exception.Message);
                useSingleObjectDeletes = true;
                await DeleteObjectsIndividuallyAsync(batch, cancellationToken);
            }
        }
    }

    private async Task DeleteObjectsIndividuallyAsync(IEnumerable<string> objectKeys, CancellationToken cancellationToken)
    {
        var deletedCount = 0;
        foreach (var objectKey in objectKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await _objectClient.DeleteObjectAsync(_bucket, objectKey, _ => { }, cancellationToken);
            EnsureSuccess(response, $"delete object '{objectKey}'");
            deletedCount++;
        }

        AppLogger.Debug("Single-object delete batch completed. batchSize={BatchSize}.", deletedCount);
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

    private static bool IsNotFound(IResponse? response)
    {
        return response?.StatusCode == 404;
    }

    private static string? ExtractObjectKey(object item)
    {
        return ReadStringProperty(item, "ObjectKey")
               ?? ReadStringProperty(item, "Key")
               ?? ReadStringProperty(item, "Name");
    }

    private static string? ReadStringProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property?.PropertyType != typeof(string))
        {
            return null;
        }

        return (string?)property.GetValue(instance);
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

    private static void EnsureSuccess(IResponse response, string operation)
    {
        if (response.IsSuccess)
        {
            return;
        }

        var detail = response.Error is null
            ? $"statusCode={response.StatusCode}"
            : $"{response.Error.Code}: {response.Error.Message} ({response.Error.GetErrorDetails()})";

        throw new InvalidOperationException($"storage: failed to {operation}. {detail}");
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
                "Failed to remove temporary archive. path={Path}, error={ErrorMessage}",
                path,
                exception.Message);
        }
    }
}
