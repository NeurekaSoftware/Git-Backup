using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GitBackup.Services.Paths;

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
    private const int MaxRedirects = 5;

    public static async Task<Stream> DownloadToMemoryAsync(
        HttpClient client,
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        using var response = await SendFollowingRedirectsAsync(client, downloadUrl, cancellationToken);
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
    /// Issues the download GET, following redirects manually so every hop is re-checked by
    /// <see cref="EnsureSafeDownloadHostAsync"/> (a plain <c>AllowAutoRedirect</c> would validate only
    /// the first URL). The auth header is captured from the client and sent only while the request
    /// stays on the original host — a redirect to any other host drops it, mirroring the framework's
    /// own cross-host strip so the token never reaches a redirect target.
    /// </summary>
    private static async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        HttpClient client,
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var currentUri))
        {
            throw new InvalidOperationException($"Attachment URL '{RedactUrl(downloadUrl)}' is not a valid absolute URL.");
        }

        // The client is built on a non-redirecting handler; capture and remove its default auth so it
        // is applied per-request only, never automatically to a redirected host.
        var auth = client.DefaultRequestHeaders.Authorization;
        client.DefaultRequestHeaders.Authorization = null;
        var originalHost = currentUri.Host;

        for (var hop = 0; ; hop++)
        {
            await EnsureSafeDownloadHostAsync(currentUri, cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            if (auth is not null && string.Equals(currentUri.Host, originalHost, StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = auth;
            }

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null || hop >= MaxRedirects)
            {
                return response;
            }

            var location = response.Headers.Location;
            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            response.Dispose();
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
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
        var sanitized = GitRepositoryUrl.InvalidStorageSegmentCharacters.Replace(candidate, "-").Trim('-', '.');
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

    /// <summary>
    /// Rejects an attachment URL that is not http(s) or that resolves to a private, loopback, or
    /// link-local address, so a crafted provider response (or a redirect hop) cannot make the
    /// authenticated client reach an internal endpoint (e.g. a cloud metadata service). DNS names are
    /// resolved and every returned address is checked, not just literal-IP hosts. A residual
    /// DNS-rebinding TOCTOU remains (the name could resolve differently when the socket actually
    /// connects); fully closing it would require pinning the connection to the validated address.
    /// </summary>
    private static async Task EnsureSafeDownloadHostAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!GitRepositoryUrl.IsHttpOrHttps(uri))
        {
            throw new InvalidOperationException($"Attachment URL '{RedactUrl(uri.ToString())}' is not an http or https URL.");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
            if (addresses.Length == 0)
            {
                throw new InvalidOperationException($"Attachment host '{uri.Host}' did not resolve to any address.");
            }
        }

        foreach (var address in addresses)
        {
            if (IsPrivateOrLocal(address))
            {
                throw new InvalidOperationException(
                    $"Attachment URL '{RedactUrl(uri.ToString())}' resolves to a private, loopback, or link-local address.");
            }
        }
    }

    private static bool IsPrivateOrLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] is 0 or 10
                   || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal
                   || address.IsIPv6SiteLocal
                   || (bytes[0] & 0xFE) == 0xFC; // fc00::/7 unique local addresses
        }

        return false;
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
