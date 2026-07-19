//! Forgejo provider ← `ForgejoRepositoryProviderClient` (discovery).
//!
//! Owned repos, optionally starred repos, offset-paginated (page size 50). No gists/snippets API.
//! Metadata lands in P7.

use super::attachments::AttachmentStream;
use super::client::{ProjectMetadataProviderClient, RepositoryProviderClient};
use super::discovered::DiscoveredRepository;
use super::gitea::{
    extract_asset_array, map_gitea_comment, map_gitea_issue, map_gitea_pull_request,
    map_gitea_release, map_gitea_repository,
};
use super::http_base::{self, api_key, Auth, AuthScheme, PageStrategy, ProviderHttp};
use super::models::{
    BackedUpAttachment, BackedUpIssue, BackedUpMergeRequest, BackedUpRelease,
    ProjectMetadataContext,
};
use crate::config::{CredentialConfig, RepositoryJobConfig};
use anyhow::{anyhow, Result};
use async_trait::async_trait;
use futures::stream::{StreamExt, TryStreamExt};
use serde_json::Value;
use std::collections::HashSet;
use std::sync::Arc;
use tokio_util::sync::CancellationToken;

const DEFAULT_BASE_URL: &str = "https://codeberg.org";
const PAGE_SIZE: usize = 50;

pub struct ForgejoClient {
    http: Arc<ProviderHttp>,
}

impl ForgejoClient {
    pub fn new(http: Arc<ProviderHttp>) -> Self {
        Self { http }
    }
}

#[async_trait]
impl RepositoryProviderClient for ForgejoClient {
    fn provider(&self) -> &'static str {
        "forgejo"
    }

    fn supports_snippets(&self) -> bool {
        false
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
            scheme: AuthScheme::Token,
            token: &token,
        };
        let base = resolve_api_base_url(job.base_url.as_deref());
        let full_page = PageStrategy::FullPage(PAGE_SIZE);

        let mut all = self
            .http
            .collect_pages(
                &auth,
                |page| format!("{base}/user/repos?affiliation=owner&limit={PAGE_SIZE}&page={page}"),
                |item| map_gitea_repository(item, false),
                full_page,
                cancel,
            )
            .await?;

        if job.include_starred == Some(true) {
            tracing::debug!("Including starred repositories. provider=forgejo.");
            all.extend(
                self.http
                    .collect_pages(
                        &auth,
                        |page| format!("{base}/user/starred?limit={PAGE_SIZE}&page={page}"),
                        |item| map_gitea_repository(item, true),
                        full_page,
                        cancel,
                    )
                    .await?,
            );
        }

        Ok(http_base::distinct_by_clone_url(all))
    }
}

fn resolve_api_base_url(configured: Option<&str>) -> String {
    http_base::compose_api_base_url(configured, DEFAULT_BASE_URL, "/api/v1")
}

#[async_trait]
impl ProjectMetadataProviderClient for ForgejoClient {
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
            scheme: AuthScheme::Token,
            token: &token,
        };
        let base = resolve_api_base_url(context.base_url.as_deref());
        let repo_path = http_base::build_owner_repo_path(&context.clone_url)?;

        let issues = self
            .http
            .collect_pages(
                &auth,
                |page| format!("{base}/repos/{repo_path}/issues?type=issues&state=all&limit={PAGE_SIZE}&page={page}"),
                |item| {
                    let mut issue = map_gitea_issue(item)?;
                    issue.attachments = extract_asset_array(item);
                    Some(issue)
                },
                PageStrategy::FullPage(PAGE_SIZE),
                cancel,
            )
            .await?;

        futures::stream::iter(issues.into_iter().map(|mut issue| {
            let auth = &auth;
            let base = &base;
            let repo_path = &repo_path;
            async move {
                let (comments, comment_attachments) = self
                    .fetch_comments(auth, base, repo_path, issue.number, cancel)
                    .await?;
                issue.comments = comments;
                issue.attachments = merge_attachments(issue.attachments, comment_attachments);
                Ok::<_, anyhow::Error>(issue)
            }
        }))
        .buffer_unordered(context.concurrency)
        .try_collect()
        .await
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
            scheme: AuthScheme::Token,
            token: &token,
        };
        let base = resolve_api_base_url(context.base_url.as_deref());
        let repo_path = http_base::build_owner_repo_path(&context.clone_url)?;

        let pulls = self
            .http
            .collect_pages(
                &auth,
                |page| {
                    format!(
                        "{base}/repos/{repo_path}/pulls?state=all&limit={PAGE_SIZE}&page={page}"
                    )
                },
                |item| {
                    let mut pull = map_gitea_pull_request(item)?;
                    pull.attachments = extract_asset_array(item);
                    Some(pull)
                },
                PageStrategy::FullPage(PAGE_SIZE),
                cancel,
            )
            .await?;

        futures::stream::iter(pulls.into_iter().map(|mut pull| {
            let auth = &auth;
            let base = &base;
            let repo_path = &repo_path;
            async move {
                // Pull requests share the issue comment thread in the Gitea/Forgejo API.
                let (comments, comment_attachments) = self
                    .fetch_comments(auth, base, repo_path, pull.number, cancel)
                    .await?;
                pull.comments = comments;
                pull.attachments = merge_attachments(pull.attachments, comment_attachments);
                Ok::<_, anyhow::Error>(pull)
            }
        }))
        .buffer_unordered(context.concurrency)
        .try_collect()
        .await
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
            scheme: AuthScheme::Token,
            token: &token,
        };
        let base = resolve_api_base_url(context.base_url.as_deref());
        let repo_path = http_base::build_owner_repo_path(&context.clone_url)?;

        self.http
            .collect_pages(
                &auth,
                |page| format!("{base}/repos/{repo_path}/releases?limit={PAGE_SIZE}&page={page}"),
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
                "forgejo: missing credential for attachment download"
            ));
        };
        let auth = Auth {
            scheme: AuthScheme::Token,
            token: &token,
        };
        let trusted_host = context.instance_host();
        self.http
            .download_attachment(download_url, &auth, trusted_host.as_deref(), cancel)
            .await
    }
}

impl ForgejoClient {
    /// Fetches one issue/PR's comment thread and the attachments carried on those comments.
    async fn fetch_comments(
        &self,
        auth: &Auth<'_>,
        base: &str,
        repo_path: &str,
        number: i64,
        cancel: &CancellationToken,
    ) -> Result<(Vec<super::models::BackedUpComment>, Vec<BackedUpAttachment>)> {
        // Collect raw comment elements so both the comment and its assets are read from each.
        let raw: Vec<Value> = self
            .http
            .collect_pages(
                auth,
                |page| format!("{base}/repos/{repo_path}/issues/{number}/comments?limit={PAGE_SIZE}&page={page}"),
                |item| Some(item.clone()),
                PageStrategy::FullPage(PAGE_SIZE),
                cancel,
            )
            .await?;

        let comments = raw.iter().filter_map(map_gitea_comment).collect();
        let attachments = raw.iter().flat_map(extract_asset_array).collect();
        Ok((comments, attachments))
    }
}

/// First-wins dedup by `original_path` when merging item and comment attachments.
fn merge_attachments(
    first: Vec<BackedUpAttachment>,
    second: Vec<BackedUpAttachment>,
) -> Vec<BackedUpAttachment> {
    let mut seen = HashSet::new();
    first
        .into_iter()
        .chain(second)
        .filter(|attachment| seen.insert(attachment.original_path.clone()))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolves_api_base_url() {
        assert_eq!(resolve_api_base_url(None), "https://codeberg.org/api/v1");
        assert_eq!(
            resolve_api_base_url(Some("https://forgejo.example.com/")),
            "https://forgejo.example.com/api/v1"
        );
    }
}
