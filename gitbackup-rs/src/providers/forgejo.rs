//! Forgejo provider ← `ForgejoRepositoryProviderClient` (discovery).
//!
//! Owned repos, optionally starred repos, offset-paginated (page size 50). No gists/snippets API.
//! Metadata lands in P7.

use super::client::RepositoryProviderClient;
use super::discovered::DiscoveredRepository;
use super::gitea::map_gitea_repository;
use super::http_base::{self, Auth, AuthScheme, PageStrategy, ProviderHttp};
use crate::config::{CredentialConfig, RepositoryJobConfig};
use anyhow::Result;
use async_trait::async_trait;
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
