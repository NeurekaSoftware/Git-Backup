//! Provider client seam + factory ← `IRepositoryProviderClient` / `RepositoryProviderClientFactory`.

use super::discovered::DiscoveredRepository;
use super::forgejo::ForgejoClient;
use super::github::GitHubClient;
use super::gitlab::GitLabClient;
use super::http_base::ProviderHttp;
use crate::config::{CredentialConfig, RepositoryJobConfig};
use anyhow::Result;
use async_trait::async_trait;
use std::sync::Arc;
use tokio_util::sync::CancellationToken;

#[async_trait]
pub trait RepositoryProviderClient: Send + Sync {
    fn provider(&self) -> &'static str;

    /// Whether the provider exposes gists or snippets. False means an `includeSnippets` job is reported
    /// unsupported rather than silently discovering nothing.
    fn supports_snippets(&self) -> bool;

    async fn list_repositories(
        &self,
        job: &RepositoryJobConfig,
        credential: &CredentialConfig,
        cancel: &CancellationToken,
    ) -> Result<Vec<DiscoveredRepository>>;
}

/// Holds one client per supported provider over a shared HTTP client, resolving by name.
pub struct RepositoryProviderClientFactory {
    github: GitHubClient,
    gitlab: GitLabClient,
    forgejo: ForgejoClient,
}

impl RepositoryProviderClientFactory {
    pub fn new() -> Result<Self> {
        let http = Arc::new(ProviderHttp::new()?);
        Ok(Self {
            github: GitHubClient::new(Arc::clone(&http)),
            gitlab: GitLabClient::new(Arc::clone(&http)),
            forgejo: ForgejoClient::new(http),
        })
    }

    /// Resolves the discovery client for a provider (case-insensitive), or `None` if unregistered.
    pub fn resolve(&self, provider: &str) -> Option<&dyn RepositoryProviderClient> {
        match provider.to_ascii_lowercase().as_str() {
            "github" => Some(&self.github),
            "gitlab" => Some(&self.gitlab),
            "forgejo" => Some(&self.forgejo),
            _ => None,
        }
    }
}
