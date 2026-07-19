//! Attachment download + naming helpers ← `AttachmentDownloader`.
//!
//! Attachments stream straight to storage (never buffered), capped at 100 MiB. Redirects are followed
//! manually so every hop is SSRF-checked, and the auth header is sent only while the request stays on
//! the original host — a redirect elsewhere drops it, so the token never reaches a redirect target.

use super::http_base::Auth;
use crate::paths::git_url;
use anyhow::{anyhow, bail, Context, Result};
use sha1::{Digest, Sha1};
use std::net::IpAddr;
use std::pin::Pin;
use std::str::FromStr;
use std::task::{Context as TaskContext, Poll};
use tokio::io::{AsyncRead, ReadBuf};
use tokio_util::sync::CancellationToken;
use url::Url;

pub const MAX_ATTACHMENT_BYTES: u64 = 100 * 1024 * 1024;
const MAX_REDIRECTS: usize = 5;

/// A streaming attachment read plus the server-declared length (for choosing a single-PUT vs multipart
/// upload). The reader enforces the size cap as bytes flow through.
pub struct AttachmentStream {
    pub reader: Box<dyn AsyncRead + Send + Unpin>,
    pub known_length: Option<u64>,
}

/// Produces a safe storage-key leaf from an upload's raw file name: strips any path, decodes URL
/// escapes, and normalizes to the safe segment charset ← `SanitizeFileName`.
pub fn sanitize_file_name(file_name: &str) -> String {
    let mut candidate = file_name.trim();
    if let Some(index) = candidate.rfind(['/', '\\']) {
        candidate = &candidate[index + 1..];
    }
    let decoded = percent_encoding::percent_decode_str(candidate).decode_utf8_lossy();
    git_url::normalize_storage_segment(&decoded, "file", false)
}

/// Strips the query string so short-lived signed tokens are never logged or stored, and the derived key
/// stays stable across runs ← `RedactUrl`.
pub fn redact_url(url: &str) -> &str {
    match url.find('?') {
        Some(index) => &url[..index],
        None => url,
    }
}

/// A short, stable hex prefix (SHA-1, first 4 bytes) ← `ShortHash`.
pub fn short_hash(value: &str) -> String {
    let digest = Sha1::digest(value.as_bytes());
    let mut hex = String::with_capacity(8);
    for byte in &digest[..4] {
        hex.push_str(&format!("{byte:02x}"));
    }
    hex
}

/// `{shortHash(seed)}-{sanitized(name)}` ← `BuildStorageFileName`.
pub fn build_storage_filename(hash_seed: &str, raw_name: &str) -> String {
    format!("{}-{}", short_hash(hash_seed), sanitize_file_name(raw_name))
}

/// Whether a URL's host is on the same instance ← `IsInstanceHost`.
pub fn is_instance_host(url: &str, instance_host: &str) -> bool {
    !instance_host.is_empty()
        && Url::parse(url)
            .ok()
            .and_then(|u| u.host_str().map(|h| h.eq_ignore_ascii_case(instance_host)))
            .unwrap_or(false)
}

/// Opens an attachment as a size-capped stream, following redirects manually with a per-hop SSRF check
/// and same-host-only auth ← `OpenStreamAsync` + `SendFollowingRedirectsAsync`.
pub async fn open_stream(
    client: &reqwest::Client,
    download_url: &str,
    auth: Option<&Auth<'_>>,
    trusted_host: Option<&str>,
    cancel: &CancellationToken,
) -> Result<AttachmentStream> {
    let mut current = Url::parse(download_url).map_err(|_| {
        anyhow!(
            "Attachment URL '{}' is not a valid absolute URL.",
            redact_url(download_url)
        )
    })?;
    let original_host = current.host_str().map(str::to_string);

    let mut hop = 0usize;
    let response = loop {
        ensure_safe_download_host(&current, trusted_host).await?;

        let mut request = client.get(current.clone());
        // Send the credential only while on the original host.
        if let (Some(auth), Some(original)) = (auth, original_host.as_deref()) {
            if current
                .host_str()
                .is_some_and(|h| h.eq_ignore_ascii_case(original))
            {
                request = request.header(reqwest::header::AUTHORIZATION, auth.header_value());
            }
        }

        let response = tokio::select! {
            _ = cancel.cancelled() => bail!("attachment: download of '{}' cancelled", redact_url(download_url)),
            response = request.send() => response.with_context(|| format!("attachment: request to '{}' failed", redact_url(download_url)))?,
        };

        let status = response.status();
        let is_redirect = status.is_redirection();
        let location = response
            .headers()
            .get(reqwest::header::LOCATION)
            .and_then(|value| value.to_str().ok())
            .map(str::to_string);

        if !is_redirect || location.is_none() || hop >= MAX_REDIRECTS {
            break response;
        }

        let location = location.unwrap();
        current = current
            .join(&location)
            .map_err(|_| anyhow!("attachment: invalid redirect location '{location}'"))?;
        hop += 1;
    };

    if !response.status().is_success() {
        bail!(
            "attachment: '{}' returned HTTP {}",
            redact_url(download_url),
            response.status().as_u16()
        );
    }

    let declared_length = response.content_length();
    if let Some(length) = declared_length {
        if length > MAX_ATTACHMENT_BYTES {
            bail!(
                "Attachment '{}' is {length} bytes, over the {MAX_ATTACHMENT_BYTES} byte limit.",
                redact_url(download_url)
            );
        }
    }

    // reqwest's byte stream -> AsyncRead, wrapped so the cap is enforced as bytes arrive.
    let byte_stream = response.bytes_stream().map_err(std::io::Error::other);
    let reader = tokio_util::io::StreamReader::new(byte_stream);
    let capped = CappedReader::new(reader, MAX_ATTACHMENT_BYTES);

    Ok(AttachmentStream {
        reader: Box::new(capped),
        known_length: declared_length,
    })
}

async fn ensure_safe_download_host(uri: &Url, trusted_host: Option<&str>) -> Result<()> {
    if !git_url::is_http_or_https(uri) {
        bail!(
            "Attachment URL '{}' is not an http or https URL.",
            redact_url(uri.as_str())
        );
    }

    let host = uri
        .host_str()
        .ok_or_else(|| anyhow!("Attachment URL '{}' has no host.", redact_url(uri.as_str())))?;

    // The forge this repository came from is exempt: a self-hosted instance is routinely on private
    // address space and is already trusted (discovery used the same host + credential).
    if trusted_host.is_some_and(|trusted| !trusted.is_empty() && host.eq_ignore_ascii_case(trusted))
    {
        return Ok(());
    }

    let addresses: Vec<IpAddr> = if let Ok(literal) = IpAddr::from_str(host) {
        vec![literal]
    } else {
        let resolved: Vec<IpAddr> = tokio::net::lookup_host((host, 0u16))
            .await
            .with_context(|| format!("Attachment host '{host}' did not resolve to any address."))?
            .map(|addr| addr.ip())
            .collect();
        if resolved.is_empty() {
            bail!("Attachment host '{host}' did not resolve to any address.");
        }
        resolved
    };

    if addresses
        .iter()
        .any(|address| is_private_or_local(*address))
    {
        bail!(
            "Attachment URL '{}' resolves to a private, loopback, or link-local address.",
            redact_url(uri.as_str())
        );
    }
    Ok(())
}

fn is_private_or_local(address: IpAddr) -> bool {
    // Treat an IPv4-mapped IPv6 address as its IPv4 form.
    let address = match address {
        IpAddr::V6(v6) => v6.to_ipv4_mapped().map_or(IpAddr::V6(v6), IpAddr::V4),
        other => other,
    };

    match address {
        IpAddr::V4(v4) => {
            if v4.is_loopback() {
                return true;
            }
            let o = v4.octets();
            o[0] == 0
                || o[0] == 10
                || (o[0] == 172 && (16..=31).contains(&o[1]))
                || (o[0] == 192 && o[1] == 168)
                || (o[0] == 169 && o[1] == 254)
        }
        IpAddr::V6(v6) => {
            if v6.is_loopback() {
                return true;
            }
            let o = v6.octets();
            // link-local fe80::/10, site-local fec0::/10, unique-local fc00::/7
            (o[0] == 0xfe && (o[1] & 0xc0) == 0x80)
                || (o[0] == 0xfe && (o[1] & 0xc0) == 0xc0)
                || (o[0] & 0xfe) == 0xfc
        }
    }
}

use futures::stream::TryStreamExt as _;

/// A pass-through `AsyncRead` that fails once more than `max_bytes` have been read.
struct CappedReader<R> {
    inner: R,
    max_bytes: u64,
    total_read: u64,
}

impl<R> CappedReader<R> {
    fn new(inner: R, max_bytes: u64) -> Self {
        Self {
            inner,
            max_bytes,
            total_read: 0,
        }
    }
}

impl<R: AsyncRead + Unpin> AsyncRead for CappedReader<R> {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut TaskContext<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<std::io::Result<()>> {
        let before = buf.filled().len();
        let inner = Pin::new(&mut self.inner);
        match inner.poll_read(cx, buf) {
            Poll::Ready(Ok(())) => {
                let read = (buf.filled().len() - before) as u64;
                self.total_read += read;
                if self.total_read > self.max_bytes {
                    return Poll::Ready(Err(std::io::Error::other(format!(
                        "Attachment exceeds the {} byte limit.",
                        self.max_bytes
                    ))));
                }
                Poll::Ready(Ok(()))
            }
            other => other,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::{Ipv4Addr, Ipv6Addr};

    #[test]
    fn sanitize_strips_path_and_normalizes() {
        assert_eq!(sanitize_file_name("../../etc/passwd"), "passwd");
        assert_eq!(sanitize_file_name("my file (1).png"), "my-file-1-.png");
        assert_eq!(sanitize_file_name("a%20b.txt"), "a-b.txt");
        assert_eq!(sanitize_file_name("   "), "file");
    }

    #[test]
    fn redact_strips_query() {
        assert_eq!(redact_url("https://h/a.png?jwt=secret"), "https://h/a.png");
        assert_eq!(redact_url("https://h/a.png"), "https://h/a.png");
    }

    #[test]
    fn build_storage_filename_is_stable_and_prefixed() {
        let name = build_storage_filename("https://h/asset.zip", "asset.zip");
        assert!(name.ends_with("-asset.zip"));
        assert_eq!(name.len(), 8 + 1 + "asset.zip".len());
        // Deterministic.
        assert_eq!(
            name,
            build_storage_filename("https://h/asset.zip", "asset.zip")
        );
    }

    #[test]
    fn ssrf_classifies_private_and_public_addresses() {
        for private in [
            IpAddr::V4(Ipv4Addr::new(127, 0, 0, 1)),
            IpAddr::V4(Ipv4Addr::new(10, 0, 0, 5)),
            IpAddr::V4(Ipv4Addr::new(172, 16, 0, 1)),
            IpAddr::V4(Ipv4Addr::new(192, 168, 1, 1)),
            IpAddr::V4(Ipv4Addr::new(169, 254, 1, 1)),
            IpAddr::V4(Ipv4Addr::new(0, 0, 0, 0)),
            IpAddr::V6(Ipv6Addr::LOCALHOST),
            IpAddr::V6("fe80::1".parse().unwrap()),
            IpAddr::V6("fc00::1".parse().unwrap()),
            // IPv4-mapped loopback.
            IpAddr::V6("::ffff:127.0.0.1".parse().unwrap()),
        ] {
            assert!(is_private_or_local(private), "{private} should be private");
        }

        for public in [
            IpAddr::V4(Ipv4Addr::new(1, 1, 1, 1)),
            IpAddr::V4(Ipv4Addr::new(140, 82, 121, 3)),
            IpAddr::V6("2606:4700::1".parse().unwrap()),
        ] {
            assert!(!is_private_or_local(public), "{public} should be public");
        }
    }
}
