//! GitLab provider ← `GitLabRepositoryProviderClient` (discovery).
//!
//! Owned projects, optionally starred projects and snippets, walked owned-first and merged with
//! first-wins clone-URL dedup. Pagination follows the `X-Next-Page` header. Metadata lands in P7.

use super::attachments::{
    build_storage_filename, is_instance_host, sanitize_file_name, AttachmentStream,
};
use super::client::{ProjectMetadataProviderClient, RepositoryProviderClient};
use super::discovered::{DiscoveredRepository, DiscoveredRepositoryKind};
use super::http_base::{self, api_key, Auth, AuthScheme, PageStrategy, ProviderHttp};
use super::json;
use super::mappers::{map_comment, scan_body_and_comments};
use super::models::{
    BackedUpAttachment, BackedUpComment, BackedUpIssue, BackedUpMergeRequest, BackedUpRelease,
    ProjectMetadataContext,
};
use crate::config::{CredentialConfig, RepositoryJobConfig};
use crate::paths::git_url;
use anyhow::{anyhow, Result};
use async_trait::async_trait;
use futures::stream::{StreamExt, TryStreamExt};
use regex::Regex;
use serde_json::Value;
use std::collections::HashSet;
use std::sync::{Arc, LazyLock};
use tokio_util::sync::CancellationToken;

const DEFAULT_BASE_URL: &str = "https://gitlab.com";
const PAGE_SIZE: usize = 100;

// GitLab renders upload references as /uploads/{32-hex}/{filename} in issue/MR/note bodies.
static UPLOAD_REFERENCE: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"/uploads/([0-9a-fA-F]{32})/([^\s)\]"'<>]+)"#).unwrap());

pub struct GitLabClient {
    http: Arc<ProviderHttp>,
}

impl GitLabClient {
    pub fn new(http: Arc<ProviderHttp>) -> Self {
        Self { http }
    }
}

#[async_trait]
impl RepositoryProviderClient for GitLabClient {
    fn provider(&self) -> &'static str {
        "gitlab"
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
        let base = resolve_api_base_url(job.base_url.as_deref());

        let mut all = self
            .http
            .collect_pages(
                &auth,
                |page| {
                    format!(
                        "{base}/projects?owned=true&simple=true&per_page={PAGE_SIZE}&page={page}"
                    )
                },
                |item| map_project(item, false),
                PageStrategy::GitLabNextPage,
                cancel,
            )
            .await?;

        if job.include_starred == Some(true) {
            tracing::debug!("Including starred repositories. provider=gitlab.");
            all.extend(
                self.http
                    .collect_pages(
                        &auth,
                        |page| format!("{base}/projects?starred=true&simple=true&per_page={PAGE_SIZE}&page={page}"),
                        |item| map_project(item, true),
                        PageStrategy::GitLabNextPage,
                        cancel,
                    )
                    .await?,
            );
        }

        if job.include_snippets == Some(true) {
            // GitLab has no "starred snippets" endpoint, so includeStarred adds nothing here.
            tracing::debug!("Including snippets. provider=gitlab.");
            all.extend(
                self.http
                    .collect_pages(
                        &auth,
                        |page| format!("{base}/snippets?per_page={PAGE_SIZE}&page={page}"),
                        map_snippet,
                        PageStrategy::GitLabNextPage,
                        cancel,
                    )
                    .await?,
            );
        }

        Ok(http_base::distinct_by_clone_url(all))
    }
}

fn resolve_api_base_url(configured: Option<&str>) -> String {
    http_base::compose_api_base_url(configured, DEFAULT_BASE_URL, "/api/v4")
}

fn map_project(item: &Value, is_starred: bool) -> Option<DiscoveredRepository> {
    let clone_url = json::get_str(item, "http_url_to_repo").filter(|v| !v.trim().is_empty())?;
    Some(DiscoveredRepository {
        clone_url: clone_url.to_string(),
        web_url: json::get_str(item, "web_url").map(str::to_string),
        kind: DiscoveredRepositoryKind::Repository,
        identifier: None,
        parent_url: None,
        provider_project_id: json::get_i64(item, "id").map(|id| id.to_string()),
        is_starred,
    })
}

fn map_snippet(item: &Value) -> Option<DiscoveredRepository> {
    // The list endpoint omits http_url_to_repo, so clone via the web URL + ".git".
    let id = snippet_id(item)?;
    let web_url = json::get_str(item, "web_url").filter(|v| !v.trim().is_empty())?;

    // A numeric project_id marks a project snippet, which nests under its owning project.
    let is_project_snippet = matches!(item.get("project_id"), Some(Value::Number(_)));

    Some(DiscoveredRepository {
        clone_url: format!("{web_url}.git"),
        web_url: Some(web_url.to_string()),
        kind: DiscoveredRepositoryKind::Snippet,
        identifier: Some(id),
        parent_url: if is_project_snippet {
            trim_snippet_suffix(web_url)
        } else {
            None
        },
        provider_project_id: None,
        is_starred: false,
    })
}

fn snippet_id(item: &Value) -> Option<String> {
    match item.get("id") {
        Some(Value::Number(number)) => Some(number.to_string()),
        Some(Value::String(text)) => Some(text.clone()),
        _ => None,
    }
}

fn trim_snippet_suffix(web_url: &str) -> Option<String> {
    // Project snippet web URLs look like https://host/<ns>/<project>/-/snippets/<id> (or the legacy
    // .../snippets/<id>). Strip the marker to get the owning project's URL.
    let marker = web_url
        .find("/-/snippets/")
        .or_else(|| web_url.rfind("/snippets/"));
    match marker {
        Some(index) if index > 0 => Some(web_url[..index].to_string()),
        _ => None,
    }
}

#[async_trait]
impl ProjectMetadataProviderClient for GitLabClient {
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
        let base = resolve_api_base_url(context.base_url.as_deref());
        let project_id = resolve_project_identifier(context)?;

        let issues = self
            .http
            .collect_pages(
                &auth,
                |page| {
                    format!("{base}/projects/{project_id}/issues?per_page={PAGE_SIZE}&page={page}")
                },
                map_issue,
                PageStrategy::GitLabNextPage,
                cancel,
            )
            .await?;

        // Fetch each issue's comment thread (bounded by the metadata concurrency), then extract its
        // attachment references.
        futures::stream::iter(issues.into_iter().map(|mut issue| {
            let auth = &auth;
            let base = &base;
            let project_id = &project_id;
            async move {
                let notes = self
                    .http
                    .collect_pages(
                        auth,
                        |page| {
                            format!(
                                "{base}/projects/{project_id}/issues/{}/notes?per_page={PAGE_SIZE}&sort=asc&page={page}",
                                issue.number
                            )
                        },
                        map_note,
                        PageStrategy::GitLabNextPage,
                        cancel,
                    )
                    .await?;
                issue.attachments = extract_attachments(context, issue.body.as_deref(), &notes);
                issue.comments = notes;
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
            scheme: AuthScheme::Bearer,
            token: &token,
        };
        let base = resolve_api_base_url(context.base_url.as_deref());
        let project_id = resolve_project_identifier(context)?;

        let merge_requests = self
            .http
            .collect_pages(
                &auth,
                |page| format!("{base}/projects/{project_id}/merge_requests?per_page={PAGE_SIZE}&page={page}"),
                map_merge_request,
                PageStrategy::GitLabNextPage,
                cancel,
            )
            .await?;

        futures::stream::iter(merge_requests.into_iter().map(|mut mr| {
            let auth = &auth;
            let base = &base;
            let project_id = &project_id;
            async move {
                let notes = self
                    .http
                    .collect_pages(
                        auth,
                        |page| {
                            format!(
                                "{base}/projects/{project_id}/merge_requests/{}/notes?per_page={PAGE_SIZE}&sort=asc&page={page}",
                                mr.number
                            )
                        },
                        map_note,
                        PageStrategy::GitLabNextPage,
                        cancel,
                    )
                    .await?;
                mr.attachments = extract_attachments(context, mr.body.as_deref(), &notes);
                mr.comments = notes;
                Ok::<_, anyhow::Error>(mr)
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
            scheme: AuthScheme::Bearer,
            token: &token,
        };
        let base = resolve_api_base_url(context.base_url.as_deref());
        let project_id = resolve_project_identifier(context)?;
        let instance_host = context.instance_host().unwrap_or_default();

        self.http
            .collect_pages(
                &auth,
                |page| {
                    format!(
                        "{base}/projects/{project_id}/releases?per_page={PAGE_SIZE}&page={page}"
                    )
                },
                |item| map_release(item, &instance_host),
                PageStrategy::GitLabNextPage,
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
                "gitlab: missing credential for attachment download"
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

/// Resolves the project addressing token: the numeric id, else the URL-encoded namespace/project path.
fn resolve_project_identifier(context: &ProjectMetadataContext) -> Result<String> {
    if let Some(id) = context
        .provider_project_id
        .as_deref()
        .map(str::trim)
        .filter(|s| !s.is_empty())
    {
        return Ok(id.to_string());
    }

    let uri = url::Url::parse(&context.clone_url).map_err(|_| {
        anyhow!(
            "Cannot resolve a GitLab project id from '{}'.",
            context.clone_url
        )
    })?;
    let path = git_url::trim_git_suffix(uri.path().trim_matches('/'));
    Ok(http_base::escape_data_string(path))
}

fn map_issue(item: &Value) -> Option<BackedUpIssue> {
    let number = json::get_i64(item, "iid").or_else(|| json::get_i64(item, "id"))?;
    let title = json::get_str(item, "title").filter(|t| !t.trim().is_empty())?;
    Some(BackedUpIssue {
        number,
        title: title.to_string(),
        state: json::get_str(item, "state").map(str::to_string),
        author: json::get_nested_str(item, "author", "username").map(str::to_string),
        body: json::get_str(item, "description").map(str::to_string),
        created_at: json::get_str(item, "created_at").map(str::to_string),
        updated_at: json::get_str(item, "updated_at").map(str::to_string),
        closed_at: json::get_str(item, "closed_at").map(str::to_string),
        labels: json::get_label_names(item, "labels"),
        web_url: json::get_str(item, "web_url").map(str::to_string),
        comments: Vec::new(),
        attachments: Vec::new(),
    })
}

fn map_merge_request(item: &Value) -> Option<BackedUpMergeRequest> {
    let number = json::get_i64(item, "iid").or_else(|| json::get_i64(item, "id"))?;
    let title = json::get_str(item, "title").filter(|t| !t.trim().is_empty())?;
    Some(BackedUpMergeRequest {
        number,
        title: title.to_string(),
        state: json::get_str(item, "state").map(str::to_string),
        author: json::get_nested_str(item, "author", "username").map(str::to_string),
        body: json::get_str(item, "description").map(str::to_string),
        source_branch: json::get_str(item, "source_branch").map(str::to_string),
        target_branch: json::get_str(item, "target_branch").map(str::to_string),
        created_at: json::get_str(item, "created_at").map(str::to_string),
        updated_at: json::get_str(item, "updated_at").map(str::to_string),
        merged_at: json::get_str(item, "merged_at").map(str::to_string),
        closed_at: json::get_str(item, "closed_at").map(str::to_string),
        labels: json::get_label_names(item, "labels"),
        web_url: json::get_str(item, "web_url").map(str::to_string),
        comments: Vec::new(),
        attachments: Vec::new(),
    })
}

fn map_note(item: &Value) -> Option<BackedUpComment> {
    map_comment(item, "author", "username", true)
}

fn map_release(item: &Value, instance_host: &str) -> Option<BackedUpRelease> {
    let tag = json::get_str(item, "tag_name").filter(|t| !t.trim().is_empty())?;
    Some(BackedUpRelease {
        tag: tag.to_string(),
        name: json::get_str(item, "name").map(str::to_string),
        body: json::get_str(item, "description").map(str::to_string),
        author: json::get_nested_str(item, "author", "username").map(str::to_string),
        draft: None,
        prerelease: None,
        created_at: json::get_str(item, "created_at").map(str::to_string),
        published_at: json::get_str(item, "released_at").map(str::to_string),
        commit: json::get_nested_str(item, "commit", "id").map(str::to_string),
        web_url: json::get_nested_str(item, "_links", "self").map(str::to_string),
        attachments: extract_release_links(item, instance_host),
    })
}

/// Extracts `/uploads/{sha}/{name}` attachment references from a body and its comments. The name is
/// untrusted, so a `/`, `\`, `.`, or `..` is rejected rather than allowed to walk the download URL off
/// the uploads path.
fn extract_attachments(
    context: &ProjectMetadataContext,
    body: Option<&str>,
    comments: &[BackedUpComment],
) -> Vec<BackedUpAttachment> {
    let project_url = context
        .web_url
        .clone()
        .unwrap_or_else(|| git_url::trim_git_suffix(&context.clone_url).to_string());
    let project_url = project_url.trim_end_matches('/').to_string();

    scan_body_and_comments(body, comments, &UPLOAD_REFERENCE, |captures| {
        let sha = &captures[1];
        let raw_name = &captures[2];
        if raw_name.contains('/') || raw_name.contains('\\') || raw_name == "." || raw_name == ".."
        {
            return None;
        }
        let original_path = format!("/uploads/{sha}/{raw_name}");
        Some(BackedUpAttachment::new(
            format!("{}-{}", &sha[..8], sanitize_file_name(raw_name)),
            original_path.clone(),
            format!("{project_url}{original_path}"),
        ))
    })
}

/// Extracts GitLab release asset links. Only links whose target is on the instance are downloadable;
/// external links are recorded as references so the token is never sent to a third party.
fn extract_release_links(item: &Value, instance_host: &str) -> Vec<BackedUpAttachment> {
    let Some(links) = item
        .get("assets")
        .and_then(|assets| assets.get("links"))
        .and_then(Value::as_array)
    else {
        return Vec::new();
    };

    let mut seen = HashSet::new();
    let mut attachments = Vec::new();
    for link in links {
        let name = json::get_str(link, "name").filter(|v| !v.trim().is_empty());
        let url = json::get_str(link, "url").filter(|v| !v.trim().is_empty());
        let direct = json::get_str(link, "direct_asset_url").filter(|v| !v.trim().is_empty());
        let reference = url.or(direct);
        let fetch = direct.or(url);
        let (Some(name), Some(reference), Some(fetch)) = (name, reference, fetch) else {
            continue;
        };
        if !seen.insert(reference.to_string()) {
            continue;
        }
        let mut attachment = BackedUpAttachment::new(
            build_storage_filename(reference, name),
            reference.to_string(),
            fetch.to_string(),
        );
        attachment.downloadable = is_instance_host(url.or(direct).unwrap_or(""), instance_host);
        attachments.push(attachment);
    }
    attachments
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn maps_project_with_id_and_starred_flag() {
        let item = json!({
            "http_url_to_repo": "https://gitlab.com/o/r.git",
            "web_url": "https://gitlab.com/o/r",
            "id": 42
        });
        let repo = map_project(&item, true).unwrap();
        assert_eq!(repo.clone_url, "https://gitlab.com/o/r.git");
        assert_eq!(repo.web_url.as_deref(), Some("https://gitlab.com/o/r"));
        assert_eq!(repo.provider_project_id.as_deref(), Some("42"));
        assert!(repo.is_starred);
        assert!(map_project(&json!({ "id": 1 }), false).is_none());
    }

    #[test]
    fn maps_project_snippet_nested_under_parent() {
        let item = json!({
            "id": 7,
            "web_url": "https://gitlab.com/o/r/-/snippets/7",
            "project_id": 99
        });
        let snip = map_snippet(&item).unwrap();
        assert_eq!(snip.clone_url, "https://gitlab.com/o/r/-/snippets/7.git");
        assert_eq!(snip.identifier.as_deref(), Some("7"));
        assert_eq!(snip.parent_url.as_deref(), Some("https://gitlab.com/o/r"));
        assert_eq!(snip.kind, DiscoveredRepositoryKind::Snippet);
    }

    #[test]
    fn personal_snippet_has_no_parent() {
        let item = json!({ "id": "abc", "web_url": "https://gitlab.com/-/snippets/abc" });
        let snip = map_snippet(&item).unwrap();
        assert_eq!(snip.identifier.as_deref(), Some("abc"));
        assert_eq!(snip.parent_url, None);
    }

    #[test]
    fn resolves_api_base_url() {
        assert_eq!(resolve_api_base_url(None), "https://gitlab.com/api/v4");
        assert_eq!(
            resolve_api_base_url(Some("https://gl.example.com")),
            "https://gl.example.com/api/v4"
        );
    }

    #[test]
    fn extract_attachments_rejects_path_traversal() {
        let context = ProjectMetadataContext {
            clone_url: "https://gitlab.com/o/r.git".into(),
            web_url: Some("https://gitlab.com/o/r".into()),
            provider_project_id: None,
            base_url: None,
            concurrency: 1,
            download_throttle: None,
        };
        let sha = "0123456789abcdef0123456789abcdef";
        let body = format!("![](/uploads/{sha}/shot.png) and /uploads/{sha}/../evil");
        let attachments = extract_attachments(&context, Some(&body), &[]);

        // Only the safe reference survives; the "../evil" name (containing '/') is rejected.
        assert_eq!(attachments.len(), 1);
        assert_eq!(
            attachments[0].original_path,
            format!("/uploads/{sha}/shot.png")
        );
        assert_eq!(
            attachments[0].download_url,
            format!("https://gitlab.com/o/r/uploads/{sha}/shot.png")
        );
        assert_eq!(attachments[0].file_name, "01234567-shot.png");
    }
}
