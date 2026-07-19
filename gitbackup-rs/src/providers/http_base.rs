//! Shared forge HTTP plumbing ← `ProviderHttpClientBase`.
//!
//! One pooled reqwest client shared across providers; per-request auth is the only per-provider seam.
//! Carries the retry/`Retry-After` policy, the paginated JSON collector, the two pagination strategies,
//! base-URL composition, and the owned-first clone-URL dedup.

use crate::config::CredentialConfig;
use crate::providers::discovered::DiscoveredRepository;
use crate::retry;
use anyhow::{anyhow, bail, Context, Result};
use reqwest::header::{HeaderValue, ACCEPT, AUTHORIZATION};
use reqwest::{Client, Response, StatusCode};
use serde_json::Value;
use std::collections::HashSet;
use std::time::Duration;
use tokio_util::sync::CancellationToken;

const MAX_ATTEMPTS: u32 = 5;
const MAX_RETRY_DELAY: Duration = Duration::from_secs(60);

/// The usable API key from a credential (trimmed, non-blank) ← `HasApiKey`. `None` means an
/// authenticated request is never attempted and discovery returns empty.
pub fn api_key(credential: &CredentialConfig) -> Option<String> {
    credential
        .api_key
        .as_deref()
        .map(str::trim)
        .filter(|key| !key.is_empty())
        .map(str::to_string)
}

/// The provider's authentication scheme — the only per-provider difference in request setup.
#[derive(Debug, Clone, Copy)]
pub enum AuthScheme {
    /// `Authorization: Bearer {token}` (GitHub, GitLab).
    Bearer,
    /// `Authorization: token {token}` (Forgejo/Gitea).
    Token,
}

pub struct Auth<'a> {
    pub scheme: AuthScheme,
    pub token: &'a str,
}

impl Auth<'_> {
    pub(crate) fn header_value(&self) -> String {
        match self.scheme {
            AuthScheme::Bearer => format!("Bearer {}", self.token),
            AuthScheme::Token => format!("token {}", self.token),
        }
    }
}

/// How a paginated endpoint signals that another page follows.
#[derive(Debug, Clone, Copy)]
pub enum PageStrategy {
    /// Offset pagination: a full page of `page_size` items means "more" (GitHub, Forgejo).
    FullPage(usize),
    /// GitLab returns the next page number in the `X-Next-Page` response header.
    GitLabNextPage,
}

/// A pooled forge HTTP client. Built once and shared; connection reuse happens inside reqwest. A
/// second, non-redirecting client serves attachment downloads so every redirect hop can be
/// SSRF-checked before it is followed.
pub struct ProviderHttp {
    client: Client,
    attachment_client: Client,
}

impl ProviderHttp {
    pub fn new() -> Result<Self> {
        let user_agent = HeaderValue::from_str(&format!(
            "GitBackup/{}",
            crate::runtime::build_metadata::version()
        ))
        .unwrap_or_else(|_| HeaderValue::from_static("GitBackup"));

        let client = Client::builder()
            .user_agent(user_agent.clone())
            .build()
            .context("provider: failed to build HTTP client")?;
        let attachment_client = Client::builder()
            .user_agent(user_agent)
            .redirect(reqwest::redirect::Policy::none())
            .build()
            .context("provider: failed to build attachment HTTP client")?;
        Ok(Self {
            client,
            attachment_client,
        })
    }

    /// Opens an attachment as a size-capped, SSRF-guarded stream (see [`super::attachments`]).
    pub async fn download_attachment(
        &self,
        download_url: &str,
        auth: &Auth<'_>,
        trusted_host: Option<&str>,
        cancel: &CancellationToken,
    ) -> Result<super::attachments::AttachmentStream> {
        super::attachments::open_stream(
            &self.attachment_client,
            download_url,
            Some(auth),
            trusted_host,
            cancel,
        )
        .await
    }

    /// Issues a GET, retrying 429/5xx (honoring a capped `Retry-After`), and returns the final response
    /// (which the caller validates). Honors cancellation between attempts.
    async fn get_with_retry(
        &self,
        url: &str,
        auth: &Auth<'_>,
        cancel: &CancellationToken,
    ) -> Result<Response> {
        for attempt in 1..=MAX_ATTEMPTS {
            let response = tokio::select! {
                _ = cancel.cancelled() => bail!("provider: request to '{url}' cancelled"),
                response = self
                    .client
                    .get(url)
                    .header(AUTHORIZATION, auth.header_value())
                    .header(ACCEPT, "application/json")
                    .send() => response.with_context(|| format!("provider: request to '{url}' failed"))?,
            };

            let status = response.status();
            let retryable = status == StatusCode::TOO_MANY_REQUESTS || status.is_server_error();
            if !retryable || attempt >= MAX_ATTEMPTS {
                return Ok(response);
            }

            let retry_after = parse_retry_after(&response);
            let delay = retry::resolve(retry_after, attempt, MAX_RETRY_DELAY, true, false);
            tracing::debug!(
                "Retrying provider request. status={}, attempt={attempt}, delaySeconds={:.1}.",
                status.as_u16(),
                delay.as_secs_f64()
            );
            tokio::select! {
                _ = cancel.cancelled() => bail!("provider: request to '{url}' cancelled"),
                _ = tokio::time::sleep(delay) => {}
            }
        }
        unreachable!("loop returns on the final attempt")
    }

    /// Reads and maps every page of a JSON-array endpoint, stopping per the pagination strategy.
    pub async fn collect_pages<T, F>(
        &self,
        auth: &Auth<'_>,
        build_uri: impl Fn(u32) -> String,
        map_item: F,
        strategy: PageStrategy,
        cancel: &CancellationToken,
    ) -> Result<Vec<T>>
    where
        F: Fn(&Value) -> Option<T>,
    {
        let mut items = Vec::new();
        let mut page = 1u32;
        loop {
            let url = build_uri(page);
            let response = self.get_with_retry(&url, auth, cancel).await?;
            if !response.status().is_success() {
                let status = response.status();
                bail!(
                    "provider: request to '{url}' returned HTTP {}",
                    status.as_u16()
                );
            }

            let next_page_header = read_next_page(&response);
            let body: Value = response
                .json()
                .await
                .with_context(|| format!("provider: failed to parse JSON from '{url}'"))?;

            let mut item_count = 0usize;
            if let Value::Array(array) = &body {
                for element in array {
                    item_count += 1;
                    if let Some(mapped) = map_item(element) {
                        items.push(mapped);
                    }
                }
            }

            if !has_next_page(strategy, next_page_header, page, item_count) {
                break;
            }
            page += 1;
        }
        Ok(items)
    }
}

/// First-wins dedup by clone URL (ordinal), preserving order. Callers append the owned walk first, so an
/// owned+starred repository keeps its owned entry — which decides whether its metadata is backed up.
pub fn distinct_by_clone_url(
    repositories: impl IntoIterator<Item = DiscoveredRepository>,
) -> Vec<DiscoveredRepository> {
    let mut seen = HashSet::new();
    let mut result = Vec::new();
    for repository in repositories {
        if seen.insert(repository.clone_url.clone()) {
            result.push(repository);
        }
    }
    result
}

/// Resolves a provider API base URL from an optional configured value, a default, and the API suffix.
pub fn compose_api_base_url(configured: Option<&str>, default: &str, api_suffix: &str) -> String {
    ensure_api_suffix(&resolve_base_url(configured, default), api_suffix)
}

pub fn resolve_base_url(configured: Option<&str>, default: &str) -> String {
    match configured.map(str::trim).filter(|v| !v.is_empty()) {
        Some(value) => value.trim_end_matches('/').to_string(),
        None => default.trim_end_matches('/').to_string(),
    }
}

pub fn ensure_api_suffix(base_url: &str, api_suffix: &str) -> String {
    if base_url
        .to_lowercase()
        .ends_with(&api_suffix.to_lowercase())
    {
        base_url.to_string()
    } else {
        format!("{base_url}{api_suffix}")
    }
}

fn has_next_page(
    strategy: PageStrategy,
    next_page_header: Option<u32>,
    page: u32,
    item_count: usize,
) -> bool {
    match strategy {
        PageStrategy::FullPage(page_size) => item_count >= page_size,
        PageStrategy::GitLabNextPage => next_page_header.is_some_and(|next| next > page),
    }
}

fn read_next_page(response: &Response) -> Option<u32> {
    response
        .headers()
        .get("x-next-page")
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.trim().parse::<u32>().ok())
}

fn parse_retry_after(response: &Response) -> Option<Duration> {
    let seconds = response
        .headers()
        .get(reqwest::header::RETRY_AFTER)?
        .to_str()
        .ok()?
        .trim()
        .parse::<u64>()
        .ok()?;
    Some(Duration::from_secs(seconds))
}

/// Splits a clone URL into `(owner, repository)` for providers addressing `/repos/{owner}/{repo}`, with
/// any `.git` suffix removed. Errors carry the original messages.
pub fn resolve_owner_and_repository(clone_url: &str) -> Result<(String, String)> {
    let uri =
        url::Url::parse(clone_url).map_err(|_| anyhow!("Invalid repository URL '{clone_url}'."))?;
    let segments = crate::paths::git_url::split_unescaped_segments(&uri);
    if segments.len() < 2 {
        bail!("Repository URL '{clone_url}' does not contain owner and repository segments.");
    }
    let repository =
        crate::paths::git_url::trim_git_suffix(&segments[segments.len() - 1]).to_string();
    Ok((segments[0].clone(), repository))
}

/// Builds the URL-escaped `{owner}/{repo}` path for providers addressing `/repos/{owner}/{repo}`.
pub fn build_owner_repo_path(clone_url: &str) -> Result<String> {
    let (owner, repository) = resolve_owner_and_repository(clone_url)?;
    Ok(format!(
        "{}/{}",
        escape_data_string(&owner),
        escape_data_string(&repository)
    ))
}

/// Percent-encodes like .NET's `Uri.EscapeDataString`: escape everything except the RFC 3986 unreserved
/// set (alphanumeric plus `-._~`). Note `/` is escaped, matching GitLab's URL-encoded project path.
pub(crate) fn escape_data_string(value: &str) -> String {
    const UNRESERVED: percent_encoding::AsciiSet = percent_encoding::NON_ALPHANUMERIC
        .remove(b'-')
        .remove(b'.')
        .remove(b'_')
        .remove(b'~');
    percent_encoding::utf8_percent_encode(value, &UNRESERVED).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn distinct_keeps_first_owned_entry() {
        // The load-bearing invariant: an owned+starred repository keeps its owned (non-starred) entry.
        let owned = DiscoveredRepository::repository("https://h/a.git".into(), None);
        let mut starred = DiscoveredRepository::repository("https://h/a.git".into(), None);
        starred.is_starred = true;
        let owned_b = DiscoveredRepository::repository("https://h/b.git".into(), None);

        let result = distinct_by_clone_url(vec![owned, starred, owned_b]);
        assert_eq!(result.len(), 2);
        assert_eq!(result[0].clone_url, "https://h/a.git");
        assert!(!result[0].is_starred, "owned entry must win over starred");
        assert_eq!(result[1].clone_url, "https://h/b.git");
    }

    #[test]
    fn compose_api_base_url_adds_suffix_idempotently() {
        assert_eq!(
            compose_api_base_url(None, "https://gitlab.com", "/api/v4"),
            "https://gitlab.com/api/v4"
        );
        assert_eq!(
            compose_api_base_url(
                Some("https://gl.example.com/"),
                "https://gitlab.com",
                "/api/v4"
            ),
            "https://gl.example.com/api/v4"
        );
        // Already suffixed -> left as-is.
        assert_eq!(
            compose_api_base_url(
                Some("https://gl.example.com/api/v4"),
                "https://gitlab.com",
                "/api/v4"
            ),
            "https://gl.example.com/api/v4"
        );
    }

    #[test]
    fn build_owner_repo_path_encodes_like_escape_data_string() {
        assert_eq!(
            build_owner_repo_path("https://github.com/octo-cat/my.repo.git").unwrap(),
            "octo-cat/my.repo"
        );
        // A reserved char round-trips through decode then re-encode; unreserved -._ stay literal.
        assert_eq!(
            build_owner_repo_path("https://h/owner/a%20b").unwrap(),
            "owner/a%20b"
        );
        assert!(build_owner_repo_path("https://github.com/onlyone").is_err());
    }

    #[test]
    fn pagination_strategies_decide_next_page() {
        assert!(has_next_page(PageStrategy::FullPage(100), None, 1, 100));
        assert!(!has_next_page(PageStrategy::FullPage(100), None, 1, 99));
        assert!(has_next_page(PageStrategy::GitLabNextPage, Some(2), 1, 100));
        assert!(!has_next_page(
            PageStrategy::GitLabNextPage,
            Some(1),
            1,
            100
        ));
        assert!(!has_next_page(PageStrategy::GitLabNextPage, None, 1, 100));
    }
}
