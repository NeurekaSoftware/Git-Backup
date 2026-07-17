using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GitBackup.Services.Paths;

namespace GitBackup.Services.Providers;

/// <summary>
/// Shared helpers for downloading issue/merge-request attachments and for turning arbitrary upload
/// names into safe storage-key leaves. Attachments are streamed straight through to storage rather
/// than buffered in memory (the returned stream owns the HTTP client/response), and a size cap guards
/// against pathological uploads.
/// </summary>
internal static class AttachmentDownloader
{
    public const long MaxAttachmentBytes = 100L * 1024 * 1024;
    private const int MaxRedirects = 5;

    /// <summary>
    /// The Content-Length declared for a stream returned by <see cref="OpenStreamAsync"/>, or null when
    /// the server did not declare one (a chunked response) or the stream came from elsewhere. Lets the
    /// storage layer pick a single-request upload for a small attachment instead of a multipart one.
    /// </summary>
    public static long? TryGetKnownLength(Stream stream)
    {
        return (stream as CappedAttachmentStream)?.KnownLength;
    }

    /// <summary>
    /// Opens an attachment as a streaming, size-capped read that the caller uploads straight to storage
    /// without ever buffering the whole file in memory. The returned stream owns <paramref name="client"/>
    /// and the HTTP response and disposes both when it is disposed, so the caller must dispose the stream
    /// once the upload completes. Redirects are followed manually (see
    /// <see cref="SendFollowingRedirectsAsync"/>) so every hop is SSRF-checked.
    /// </summary>
    public static async Task<Stream> OpenStreamAsync(
        HttpClient client,
        string downloadUrl,
        string? trustedHost,
        CancellationToken cancellationToken)
    {
        var response = await SendFollowingRedirectsAsync(client, downloadUrl, trustedHost, cancellationToken);
        try
        {
            response.EnsureSuccessStatusCode();

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength > MaxAttachmentBytes)
            {
                throw new InvalidOperationException(
                    $"Attachment '{RedactUrl(downloadUrl)}' is {declaredLength} bytes, over the {MaxAttachmentBytes} byte limit.");
            }

            var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new CappedAttachmentStream(source, response, client, MaxAttachmentBytes, declaredLength);
        }
        catch
        {
            // The response is owned here on the failure path; the client is disposed by the caller.
            response.Dispose();
            throw;
        }
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
        string? trustedHost,
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
            await EnsureSafeDownloadHostAsync(currentUri, trustedHost, cancellationToken);

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
    /// Builds a collision-resistant storage-key leaf as <c>{shortHash(hashSeed)}-{sanitized(rawName)}</c>
    /// — the shared naming convention for downloaded attachments and release assets, so the scheme lives
    /// in one place instead of being reassembled inline at each call site.
    /// </summary>
    public static string BuildStorageFileName(string hashSeed, string rawName)
    {
        return $"{ShortHash(hashSeed)}-{SanitizeFileName(rawName)}";
    }

    /// <summary>
    /// Rejects an attachment URL that is not http(s) or that resolves to a private, loopback, or
    /// link-local address, so a crafted provider response (or a redirect hop) cannot make the
    /// authenticated client reach an internal endpoint (e.g. a cloud metadata service).
    /// <paramref name="trustedHost"/> is the forge this repository came from, which is exempt: a
    /// self-hosted instance is routinely on private address space and is already trusted, since the API
    /// calls that discovered the attachment went to that same host with the same credential. Every other
    /// target still fails closed. DNS names are resolved and every returned address is checked, not just
    /// literal-IP hosts. A residual DNS-rebinding TOCTOU remains (the name could resolve differently when
    /// the socket actually connects); fully closing it would require pinning the connection to the
    /// validated address.
    /// </summary>
    private static async Task EnsureSafeDownloadHostAsync(Uri uri, string? trustedHost, CancellationToken cancellationToken)
    {
        if (!GitRepositoryUrl.IsHttpOrHttps(uri))
        {
            throw new InvalidOperationException($"Attachment URL '{RedactUrl(uri.ToString())}' is not an http or https URL.");
        }

        if (!string.IsNullOrEmpty(trustedHost)
            && string.Equals(uri.Host, trustedHost, StringComparison.OrdinalIgnoreCase))
        {
            return;
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

    // A read-only pass-through stream that enforces the attachment size cap as bytes are read and, when
    // disposed, tears down the HTTP response and the owning client. This lets an attachment be uploaded
    // to storage directly from the network without buffering it in memory.
    private sealed class CappedAttachmentStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;
        private readonly HttpClient _ownedClient;
        private readonly long _maxBytes;
        private long _totalRead;

        public CappedAttachmentStream(
            Stream inner,
            HttpResponseMessage response,
            HttpClient ownedClient,
            long maxBytes,
            long? knownLength)
        {
            _inner = inner;
            _response = response;
            _ownedClient = ownedClient;
            _maxBytes = maxBytes;
            KnownLength = knownLength;
        }

        // The Content-Length the server declared, when it declared one. Distinct from Length, which stays
        // unsupported because the stream is not seekable: this is a hint for choosing an upload strategy,
        // not a guarantee about the bytes that will actually arrive.
        public long? KnownLength { get; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Track(_inner.Read(buffer, offset, count));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return Track(await _inner.ReadAsync(buffer, cancellationToken));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        private int Track(int read)
        {
            if (read > 0)
            {
                _totalRead += read;
                if (_totalRead > _maxBytes)
                {
                    throw new InvalidOperationException($"Attachment exceeds the {_maxBytes} byte limit.");
                }
            }

            return read;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
                _ownedClient.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            _response.Dispose();
            _ownedClient.Dispose();
            await base.DisposeAsync();
        }
    }
}
