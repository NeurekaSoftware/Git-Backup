namespace GitBackup.Services.Storage;

public interface IObjectStorageService : IDisposable
{
    Task UploadDirectoryAsTarGzAsync(
        string localDirectory,
        string objectKey,
        CancellationToken cancellationToken);

    Task UploadTextAsync(string objectKey, string content, CancellationToken cancellationToken);

    /// <param name="knownLength">
    /// The content length when the source declared one, so a small payload can be sent as a single
    /// request; null when the length is unknown, which forces the streaming multipart path.
    /// </param>
    Task UploadStreamAsync(
        string objectKey,
        Stream content,
        string contentType,
        long? knownLength,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListObjectKeysAsync(string prefix, CancellationToken cancellationToken);

    Task DeleteObjectsAsync(IEnumerable<string> objectKeys, CancellationToken cancellationToken);
}
