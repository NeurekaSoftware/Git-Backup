//! Project metadata sync ← `ProjectMetadataSyncService`.
//!
//! P6 wires the driver's owned-only metadata call to this no-op placeholder; P7 implements the real
//! issues/merge-requests/releases + attachment backup with the partial-fetch-never-deletes reconcile.

use crate::config::{CredentialConfig, RepositoryJobConfig};
use crate::providers::DiscoveredRepository;
use crate::storage::ObjectStorage;
use anyhow::Result;
use std::sync::Arc;
use tokio::sync::Semaphore;
use tokio_util::sync::CancellationToken;

#[derive(Default)]
pub struct ProjectMetadataSyncService;

impl ProjectMetadataSyncService {
    pub fn new() -> Self {
        Self
    }

    #[allow(clippy::too_many_arguments)]
    pub async fn sync(
        &self,
        _job: &RepositoryJobConfig,
        _discovered: &DiscoveredRepository,
        _repository_prefix: &str,
        _credential: &CredentialConfig,
        _storage: &dyn ObjectStorage,
        _metadata_concurrency: usize,
        _download_throttle: &Arc<Semaphore>,
        _cancel: &CancellationToken,
    ) -> Result<()> {
        // TODO(P7): fetch issues/MRs/releases + attachments and reconcile stale documents.
        Ok(())
    }
}
