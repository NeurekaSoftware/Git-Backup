//! Git repository URL helpers ← `GitRepositoryUrl`.
//!
//! The single choke point for "is this a usable web URL" checks (settings validation, the git
//! transport allowlist, the storage-key parser, the attachment SSRF guard) and for normalizing an
//! untrusted value into one safe storage-key segment. `split_unescaped_segments` lands in P2.

use regex::Regex;
use std::sync::LazyLock;
use url::{Host, Url};

/// Characters not allowed in a storage-key path segment or file name; everything else becomes '-'.
static INVALID_STORAGE_SEGMENT: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"[^A-Za-z0-9._-]+").unwrap());

/// Normalizes an untrusted value into a single safe storage-key segment: replaces anything outside the
/// safe charset with '-', then trims leading/trailing '-' and '.' so "." or ".." collapses to
/// `fallback` rather than surviving into a key as a path-traversal token.
pub fn normalize_storage_segment(value: &str, fallback: &str, lowercase: bool) -> String {
    let mut candidate = value.trim().to_string();
    if lowercase {
        candidate = candidate.to_lowercase();
    }

    let sanitized = INVALID_STORAGE_SEGMENT.replace_all(&candidate, "-");
    let trimmed = sanitized.trim_matches(|c| c == '-' || c == '.');
    if trimmed.is_empty() {
        fallback.to_string()
    } else {
        trimmed.to_string()
    }
}

/// Removes a trailing `.git` suffix (case-insensitive), else returns the value unchanged.
pub fn trim_git_suffix(value: &str) -> &str {
    if value.len() >= 4 && value[value.len() - 4..].eq_ignore_ascii_case(".git") {
        &value[..value.len() - 4]
    } else {
        value
    }
}

/// True when the URL's scheme is http or https.
pub fn is_http_or_https(url: &Url) -> bool {
    matches!(url.scheme(), "http" | "https")
}

/// Parses `value` as an absolute http/https URL, else returns `None`.
pub fn try_create_http_url(value: Option<&str>) -> Option<Url> {
    let value = value?;
    let parsed = Url::parse(value).ok()?;
    is_http_or_https(&parsed).then_some(parsed)
}

/// Whether the URL targets a loopback host ← `Uri.IsLoopback`: "localhost", 127.0.0.0/8, or ::1.
pub fn is_loopback(url: &Url) -> bool {
    match url.host() {
        Some(Host::Domain(domain)) => domain.eq_ignore_ascii_case("localhost"),
        Some(Host::Ipv4(ip)) => ip.is_loopback(),
        Some(Host::Ipv6(ip)) => ip.is_loopback(),
        None => false,
    }
}
