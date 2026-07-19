//! GitHub provider ← `GitHubRepositoryProviderClient` (discovery).
//!
//! Owned repos, optionally starred repos and gists (plus starred gists), offset-paginated. Metadata
//! lands in P7.

use super::attachments::{build_storage_filename, redact_url, AttachmentStream};
use super::client::{ProjectMetadataProviderClient, RepositoryProviderClient};
use super::discovered::{DiscoveredRepository, DiscoveredRepositoryKind};
use super::gitea::{
    map_gitea_comment, map_gitea_issue, map_gitea_pull_request, map_gitea_release,
    map_gitea_repository,
};
use super::http_base::{self, api_key, Auth, AuthScheme, PageStrategy, ProviderHttp};
use super::json;
use super::mappers::scan_body_and_comments;
use super::models::{
    BackedUpAttachment, BackedUpComment, BackedUpIssue, BackedUpMergeRequest, BackedUpRelease,
    ProjectMetadataContext,
};
use crate::config::{CredentialConfig, RepositoryJobConfig};
use anyhow::{anyhow, Result};
use async_trait::async_trait;
use regex::Regex;
use serde_json::Value;
use std::collections::HashMap;
use std::sync::{Arc, LazyLock};
use tokio_util::sync::CancellationToken;

const DEFAULT_API_BASE_URL: &str = "https://api.github.com";
const PAGE_SIZE: usize = 100;

// GitHub stores issue/PR attachments as absolute URLs on its attachment hosts, embedded in markdown.
static ATTACHMENT_REFERENCE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r#"(?i)https?://(?:user-images\.githubusercontent\.com|private-user-images\.githubusercontent\.com|github\.com/user-attachments/(?:assets|files)|github\.com/[^/\s)]+/[^/\s)]+/(?:assets|files))/[^\s)\]"'<>]+"#,
    )
    .unwrap()
});

pub struct GitHubClient {
    http: Arc<ProviderHttp>,
}

impl GitHubClient {
    pub fn new(http: Arc<ProviderHttp>) -> Self {
        Self { http }
    }
}

#[async_trait]
impl RepositoryProviderClient for GitHubClient {
    fn provider(&self) -> &'static str {
        "github"
    }

    fn supports_snippets(&self) -> bool {
        true
    }

    async fn list_repositories(
        &self,
        job: &RepositoryJobConfig,
        credential: &CredentialConfig,
        cancel: &CancellationToken,
    ) -> Result<Vec<DiscoveredRepository>> {
        let Some(token) = http_base::api_key(credential) else {
            return Ok(Vec::new());
        };
        let auth = Auth {
            scheme: AuthScheme::Bearer,
            token: &token,
        };
        let base = resolve_github_api_base_url(job.base_url.as_deref());
        let full_page = PageStrategy::FullPage(PAGE_SIZE);

        let mut all = self
            .http
            .collect_pages(
                &auth,
                |page| format!("{base}/user/repos?affiliation=owner&visibility=all&per_page={PAGE_SIZE}&page={page}"),
                |item| map_gitea_repository(item, false),
                full_page,
                cancel,
            )
            .await?;

        if job.include_starred == Some(true) {
            tracing::debug!("Including starred repositories. provider=github.");
            all.extend(
                self.http
                    .collect_pages(
                        &auth,
                        |page| format!("{base}/user/starred?per_page={PAGE_SIZE}&page={page}"),
                        |item| map_gitea_repository(item, true),
                        full_page,
                        cancel,
                    )
                    .await?,
            );
        }

        if job.include_snippets == Some(true) {
            tracing::debug!("Including gists. provider=github.");
            all.extend(
                self.http
                    .collect_pages(
                        &auth,
                        |page| format!("{base}/gists?per_page={PAGE_SIZE}&page={page}"),
                        map_gist,
                        full_page,
                        cancel,
                    )
                    .await?,
            );

            if job.include_starred == Some(true) {
                tracing::debug!("Including starred gists. provider=github.");
                all.extend(
                    self.http
                        .collect_pages(
                            &auth,
                            |page| format!("{base}/gists/starred?per_page={PAGE_SIZE}&page={page}"),
                            map_gist,
                            full_page,
                            cancel,
                        )
                        .await?,
                );
            }
        }

        Ok(http_base::distinct_by_clone_url(all))
    }
}

fn map_gist(item: &Value) -> Option<DiscoveredRepository> {
    let clone_url = json::get_str(item, "git_pull_url").filter(|v| !v.trim().is_empty())?;
    let id = json::get_str(item, "id").filter(|v| !v.trim().is_empty())?;
    Some(DiscoveredRepository {
        clone_url: clone_url.to_string(),
        web_url: json::get_str(item, "html_url").map(str::to_string),
        kind: DiscoveredRepositoryKind::Gist,
        identifier: Some(id.to_string()),
        parent_url: None,
        provider_project_id: None,
        is_starred: false,
    })
}

/// GitHub base-URL resolution: default api.github.com; a configured base already carrying `/api/` is
/// kept; a bare `https://github.com` maps to api.github.com; anything else is a GHES host, suffixed
/// with `/api/v3`.
fn resolve_github_api_base_url(configured: Option<&str>) -> String {
    let Some(configured) = configured.map(str::trim).filter(|v| !v.is_empty()) else {
        return DEFAULT_API_BASE_URL.to_string();
    };

    let trimmed = http_base::resolve_base_url(Some(configured), DEFAULT_API_BASE_URL);
    if trimmed.to_lowercase().contains("/api/") {
        return trimmed;
    }
    if trimmed.eq_ignore_ascii_case("https://github.com") {
        return DEFAULT_API_BASE_URL.to_string();
    }
    format!("{trimmed}/api/v3")
}

#[async_trait]
impl ProjectMetadataProviderClient for GitHubClient {
    fn supports_issues(&self) -> bool {
        true
    }
    fn supports_merge_requests(&self) -> bool {
        true
    }
    fn supports_releases(&self) -> bool {
        true
    }
    fn supports_artifacts(&self) -> bool {
        true
    }

    async fn list_issues(
        &self,
        context: &ProjectMetadataContext,
        credential: &CredentialConfig,
        cancel: &CancellationToken,
    ) -> Result<Vec<BackedUpIssue>> {
        let Some(token) = api_key(credential) else {
            return Ok(Vec::new());
        };
        let auth = Auth {
            scheme: AuthScheme::Bearer,
            token: &token,
        };
        let base = resolve_github_api_base_url(context.base_url.as_deref());
        let repo_path = http_base::build_owner_repo_path(&context.clone_url)?;

        let comments = self
            .fetch_issue_comments_by_number(&auth, &base, &repo_path, cancel)
            .await?;
        let mut issues = self
            .http
            .collect_pages(
                &auth,
                |page| format!("{base}/repos/{repo_path}/issues?state=all&per_page={PAGE_SIZE}&page={page}"),
                map_issue,
                PageStrategy::FullPage(PAGE_SIZE),
                cancel,
            )
            .await?;
        for issue in &mut issues {
            issue.comments = comments.get(&issue.number).cloned().unwrap_or_default();
            issue.attachments = extract_attachments(issue.body.as_deref(), &issue.comments);
        }
        Ok(issues)
    }

    async fn list_merge_requests(
        &self,
        context: &ProjectMetadataContext,
        credential: &CredentialConfig,
        cancel: &CancellationToken,
    ) -> Result<Vec<BackedUpMergeRequest>> {
        let Some(token) = api_key(credential) else {
            return Ok(Vec::new());
        };
        let auth = Auth {
            scheme: AuthScheme::Bearer,
            token: &token,
        };
        let base = resolve_github_api_base_url(context.base_url.as_deref());
        let repo_path = http_base::build_owner_repo_path(&context.clone_url)?;

        // Pull requests are issues on GitHub, so their discussion comments live on the same repo-wide
        // endpoint the issue pass walks.
        let comments = self
            .fetch_issue_comments_by_number(&auth, &base, &repo_path, cancel)
            .await?;
        let mut pulls = self
            .http
            .collect_pages(
                &auth,
                |page| {
                    format!(
                        "{base}/repos/{repo_path}/pulls?state=all&per_page={PAGE_SIZE}&page={page}"
                    )
                },
                map_gitea_pull_request,
                PageStrategy::FullPage(PAGE_SIZE),
                cancel,
            )
            .await?;
        for pull in &mut pulls {
            pull.comments = comments.get(&pull.number).cloned().unwrap_or_default();
            pull.attachments = extract_attachments(pull.body.as_deref(), &pull.comments);
        }
        Ok(pulls)
    }

    async fn list_releases(
        &self,
        context: &ProjectMetadataContext,
        credential: &CredentialConfig,
        cancel: &CancellationToken,
    ) -> Result<Vec<BackedUpRelease>> {
        let Some(token) = api_key(credential) else {
            return Ok(Vec::new());
        };
        let auth = Auth {
            scheme: AuthScheme::Bearer,
            token: &token,
        };
        let base = resolve_github_api_base_url(context.base_url.as_deref());
        let repo_path = http_base::build_owner_repo_path(&context.clone_url)?;

        self.http
            .collect_pages(
                &auth,
                |page| {
                    format!("{base}/repos/{repo_path}/releases?per_page={PAGE_SIZE}&page={page}")
                },
                map_gitea_release,
                PageStrategy::FullPage(PAGE_SIZE),
                cancel,
            )
            .await
    }

    async fn open_attachment(
        &self,
        context: &ProjectMetadataContext,
        credential: &CredentialConfig,
        download_url: &str,
        cancel: &CancellationToken,
    ) -> Result<AttachmentStream> {
        let Some(token) = api_key(credential) else {
            return Err(anyhow!(
                "github: missing credential for attachment download"
            ));
        };
        let auth = Auth {
            scheme: AuthScheme::Bearer,
            token: &token,
        };
        let trusted_host = context.instance_host();
        self.http
            .download_attachment(download_url, &auth, trusted_host.as_deref(), cancel)
            .await
    }
}

impl GitHubClient {
    /// Fetches every issue and PR comment for the repo in one walk, grouped by the issue/PR number
    /// parsed from each comment's `issue_url`. Requested oldest-first so each thread stays ordered.
    async fn fetch_issue_comments_by_number(
        &self,
        auth: &Auth<'_>,
        base: &str,
        repo_path: &str,
        cancel: &CancellationToken,
    ) -> Result<HashMap<i64, Vec<BackedUpComment>>> {
        let flat = self
            .http
            .collect_pages(
                auth,
                |page| {
                    format!(
                        "{base}/repos/{repo_path}/issues/comments?sort=created&direction=asc&per_page={PAGE_SIZE}&page={page}"
                    )
                },
                |item| {
                    let comment = map_gitea_comment(item)?;
                    let number = parse_issue_number(json::get_str(item, "issue_url")?)?;
                    Some((number, comment))
                },
                PageStrategy::FullPage(PAGE_SIZE),
                cancel,
            )
            .await?;

        let mut grouped: HashMap<i64, Vec<BackedUpComment>> = HashMap::new();
        for (number, comment) in flat {
            grouped.entry(number).or_default().push(comment);
        }
        Ok(grouped)
    }
}

// The GitHub issues endpoint also returns pull requests; those carry a pull_request object and are
// skipped here (backed up via the pulls endpoint instead).
fn map_issue(item: &Value) -> Option<BackedUpIssue> {
    if item.get("pull_request").is_some() {
        return None;
    }
    map_gitea_issue(item)
}

fn parse_issue_number(issue_url: &str) -> Option<i64> {
    let segment = issue_url.rsplit('/').next()?;
    segment.parse().ok()
}

fn extract_attachments(
    body: Option<&str>,
    comments: &[BackedUpComment],
) -> Vec<BackedUpAttachment> {
    scan_body_and_comments(body, comments, &ATTACHMENT_REFERENCE, |captures| {
        let url = &captures[0];
        // Reject dot-segments that Uri normalization would resolve into a different path on the same
        // allowlisted host before the request goes out with the token.
        let path = url.split('?').next().unwrap_or(url);
        if path.split('/').any(|segment| segment == "..") {
            return None;
        }
        // Persist and key off the query-free URL so the short-lived signing token is never stored.
        let reference = redact_url(url);
        Some(BackedUpAttachment::new(
            build_storage_filename(reference, last_path_segment(reference)),
            reference.to_string(),
            url.to_string(),
        ))
    })
}

fn last_path_segment(url: &str) -> &str {
    let without_query = url.split('?').next().unwrap_or(url);
    without_query.rsplit('/').next().unwrap_or(without_query)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn maps_gist() {
        let item = json!({
            "git_pull_url": "https://gist.github.com/abc.git",
            "id": "abc",
            "html_url": "https://gist.github.com/abc"
        });
        let g = map_gist(&item).unwrap();
        assert_eq!(g.clone_url, "https://gist.github.com/abc.git");
        assert_eq!(g.identifier.as_deref(), Some("abc"));
        assert_eq!(g.kind, DiscoveredRepositoryKind::Gist);
        assert!(map_gist(&json!({ "id": "abc" })).is_none());
    }

    #[test]
    fn github_base_url_cases() {
        assert_eq!(resolve_github_api_base_url(None), "https://api.github.com");
        assert_eq!(
            resolve_github_api_base_url(Some("https://github.com")),
            "https://api.github.com"
        );
        assert_eq!(
            resolve_github_api_base_url(Some("https://ghe.example.com")),
            "https://ghe.example.com/api/v3"
        );
        assert_eq!(
            resolve_github_api_base_url(Some("https://ghe.example.com/api/v3")),
            "https://ghe.example.com/api/v3"
        );
    }

    #[test]
    fn extract_attachments_redacts_query_and_rejects_dot_segments() {
        let body = "look https://github.com/o/r/assets/123/pic.png?jwt=tok and \
                    https://github.com/o/r/assets/../secret";
        let attachments = extract_attachments(Some(body), &[]);

        // The dot-segment URL is rejected; the good one is stored query-free but downloaded with token.
        assert_eq!(attachments.len(), 1);
        assert_eq!(
            attachments[0].original_path,
            "https://github.com/o/r/assets/123/pic.png"
        );
        assert_eq!(
            attachments[0].download_url,
            "https://github.com/o/r/assets/123/pic.png?jwt=tok"
        );
    }
}
