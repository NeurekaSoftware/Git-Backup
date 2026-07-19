//! GitHub provider ← `GitHubRepositoryProviderClient` (discovery).
//!
//! Owned repos, optionally starred repos and gists (plus starred gists), offset-paginated. Metadata
//! lands in P7.

use super::client::RepositoryProviderClient;
use super::discovered::{DiscoveredRepository, DiscoveredRepositoryKind};
use super::gitea::map_gitea_repository;
use super::http_base::{self, Auth, AuthScheme, PageStrategy, ProviderHttp};
use super::json;
use crate::config::{CredentialConfig, RepositoryJobConfig};
use anyhow::Result;
use async_trait::async_trait;
use serde_json::Value;
use std::sync::Arc;
use tokio_util::sync::CancellationToken;

const DEFAULT_API_BASE_URL: &str = "https://api.github.com";
const PAGE_SIZE: usize = 100;

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
}
