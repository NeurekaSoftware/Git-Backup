using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GitBackup.Services.Providers;

/// <summary>
/// Shared helpers for downloading issue/merge-request attachments into memory with a size cap and
/// for turning arbitrary upload names into safe storage-key leaves. Buffering fully into memory
/// keeps attachment lifetime simple (the authenticated client can be disposed before the caller
/// uploads) and attachments are small in practice; the cap guards against pathological uploads.
/// </summary>
internal static class AttachmentDownloader
{
    public const long MaxAttachmentBytes = 100L * 1024 * 1024;

    private static readonly Regex InvalidFileNameCharacters = new("[^A-Za-z0-9._-]+", RegexOptions.Compiled);

    public static async Task<Stream> DownloadToMemoryAsync(
        HttpClient client,
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > MaxAttachmentBytes)
        {
            throw new InvalidOperationException(
                $"Attachment '{RedactUrl(downloadUrl)}' is {declaredLength} bytes, over the {MaxAttachmentBytes} byte limit.");
        }

        // Pre-size the buffer when the server declares a length, avoiding the doubling reallocations
        // (and repeated large-object-heap copies) a default-capacity MemoryStream would incur.
        var capacity = declaredLength is > 0 and <= MaxAttachmentBytes ? (int)declaredLength.Value : 0;
        var buffer = capacity > 0 ? new MemoryStream(capacity) : new MemoryStream();
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            await CopyWithLimitAsync(source, buffer, MaxAttachmentBytes, cancellationToken);
        }

        buffer.Position = 0;
        return buffer;
    }

    /// <summary>
    /// Produces a safe storage-key leaf from an upload's raw file name: strips any path, decodes URL
    /// escapes, and replaces characters outside <c>[A-Za-z0-9._-]</c> so the name matches the same
    /// normalization discipline used for repository path segments.
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        var candidate = fileName.Trim();

        var lastSeparator = candidate.LastIndexOfAny(['/', '\\']);
        if (lastSeparator >= 0)
        {
            candidate = candidate[(lastSeparator + 1)..];
        }

        candidate = Uri.UnescapeDataString(candidate);
        var sanitized = InvalidFileNameCharacters.Replace(candidate, "-").Trim('-', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "file" : sanitized;
    }

    /// <summary>
    /// Strips the query string from a URL so short-lived signed tokens (e.g. GitHub's
    /// <c>private-user-images…?jwt=</c>) are never written to a log or persisted as a stored
    /// attachment reference. Using the query-free form also keeps the derived storage key stable across
    /// runs, since the signing token changes on every API read.
    /// </summary>
    public static string RedactUrl(string url)
    {
        var queryIndex = url.IndexOf('?');
        return queryIndex >= 0 ? url[..queryIndex] : url;
    }

    /// <summary>
    /// A short, stable hex prefix derived from a value (e.g. an attachment URL), used to keep storage
    /// keys unique and idempotent across runs when the provider gives no natural content hash.
    /// </summary>
    public static string ShortHash(string value)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static async Task CopyWithLimitAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException($"Attachment exceeds the {maxBytes} byte limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
