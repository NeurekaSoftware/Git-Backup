namespace GitBackup.Services.Storage;

public interface IObjectStorageService : IDisposable
{
    Task UploadDirectoryAsTarGzAsync(
        string localDirectory,
        string objectKey,
        CancellationToken cancellationToken);

    Task UploadTextAsync(string objectKey, string content, CancellationToken cancellationToken);

    Task UploadStreamAsync(
        string objectKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListObjectKeysAsync(string prefix, CancellationToken cancellationToken);

    Task DeleteObjectsAsync(IEnumerable<string> objectKeys, CancellationToken cancellationToken);
}
