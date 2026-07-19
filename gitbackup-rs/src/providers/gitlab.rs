//! GitLab provider ← `GitLabRepositoryProviderClient` (discovery).
//!
//! Owned projects, optionally starred projects and snippets, walked owned-first and merged with
//! first-wins clone-URL dedup. Pagination follows the `X-Next-Page` header. Metadata lands in P7.

use super::client::RepositoryProviderClient;
use super::discovered::{DiscoveredRepository, DiscoveredRepositoryKind};
use super::http_base::{self, Auth, AuthScheme, PageStrategy, ProviderHttp};
use super::json;
use crate::config::{CredentialConfig, RepositoryJobConfig};
use anyhow::Result;
use async_trait::async_trait;
use serde_json::Value;
use std::sync::Arc;
use tokio_util::sync::CancellationToken;

const DEFAULT_BASE_URL: &str = "https://gitlab.com";
const PAGE_SIZE: usize = 100;

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
}
