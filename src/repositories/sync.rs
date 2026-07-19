//! Repository sync driver ← `RepositorySyncService`.
//!
//! Iterates enabled jobs, dispatches provider vs url mode, and for each discovered/configured
//! repository (fanned out to `concurrency.repositories`): git-syncs a bare mirror, streams it to storage
//! as `{unixSeconds}_repo.tar.gz`, writes the advisory `metadata.json`, and — for owned repositories —
//! backs up project metadata. An inaccessible remote is skipped with a warning; any discovery gap marks
//! the run incomplete so local-mirror cleanup is suppressed (a discovery error never deletes a mirror).

use super::documents::{self, RepositoryMetadataDocument};
use super::local_mirror::LocalMirrorStore;
use super::metadata_sync::ProjectMetadataSyncService;
use crate::config::enums::modes;
use crate::config::{CredentialConfig, RepositoryJobConfig, Settings, StorageConfig};
use crate::git::{GitCredential, GitError, GitRepository};
use crate::paths::{key_builder, repo_path};
use crate::providers::{
    DiscoveredRepository, DiscoveredRepositoryKind, RepositoryProviderClientFactory,
};
use crate::storage::ObjectStorage;
use anyhow::{anyhow, Result};
use futures::stream::StreamExt;
use std::collections::HashSet;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use tokio::sync::Semaphore;
use tokio_util::sync::CancellationToken;

/// Builds an object-storage instance for a run from the (hot-reloadable) storage config.
pub type StorageFactory =
    Arc<dyn Fn(&StorageConfig) -> Result<Box<dyn ObjectStorage>> + Send + Sync>;

pub struct RepositorySyncService {
    providers: Arc<RepositoryProviderClientFactory>,
    git: Arc<dyn GitRepository>,
    storage_factory: StorageFactory,
    mirror_store: Arc<LocalMirrorStore>,
    metadata: Arc<ProjectMetadataSyncService>,
}

/// Per-repository snapshot failure, classified so the driver can skip an inaccessible remote while a
/// genuine failure is logged and a shutdown propagates.
enum SnapshotError {
    RemoteInaccessible(String),
    Cancelled,
    Other(anyhow::Error),
}

impl From<GitError> for SnapshotError {
    fn from(error: GitError) -> Self {
        match error {
            GitError::RemoteInaccessible(message) => SnapshotError::RemoteInaccessible(message),
            GitError::Cancelled => SnapshotError::Cancelled,
            GitError::Other(message) => SnapshotError::Other(anyhow!(message)),
        }
    }
}

impl RepositorySyncService {
    pub fn new(
        providers: Arc<RepositoryProviderClientFactory>,
        git: Arc<dyn GitRepository>,
        storage_factory: StorageFactory,
        mirror_store: Arc<LocalMirrorStore>,
        metadata: Arc<ProjectMetadataSyncService>,
    ) -> Self {
        Self {
            providers,
            git,
            storage_factory,
            mirror_store,
            metadata,
        }
    }

    pub async fn run(&self, settings: &Settings, cancel: &CancellationToken) -> Result<()> {
        let enabled: Vec<&Option<RepositoryJobConfig>> = settings
            .repositories
            .iter()
            .filter(|repository| repository.as_ref().is_none_or(|r| r.enabled != Some(false)))
            .collect();
        tracing::info!("Repository run started. enabledJobs={}.", enabled.len());

        let storage = (self.storage_factory)(&settings.storage)?;
        tracing::debug!(
            "Repository storage target configured. endpoint={:?}, bucket={:?}, region={:?}.",
            settings.storage.endpoint,
            settings.storage.bucket,
            settings.storage.region
        );

        // Every repository's mirror directory this run, plus whether the picture is complete. Local
        // cleanup only runs when complete, so a discovery error never deletes a valid mirror.
        let expected: Mutex<HashSet<String>> = Mutex::new(HashSet::new());
        let mut picture_complete = true;
        let mut synced_total = 0usize;
        let mut skipped_total = 0usize;

        let repository_concurrency = settings.concurrency.repositories.unwrap_or(1).max(1) as usize;
        let metadata_concurrency = settings.concurrency.metadata.unwrap_or(1).max(1) as usize;
        // Shared across every repository, capping attachments buffered in memory at metadataConcurrency.
        let download_throttle = Arc::new(Semaphore::new(metadata_concurrency));

        for repository in enabled {
            if cancel.is_cancelled() {
                break;
            }
            let Some(repository) = repository else {
                tracing::warn!("Skipping repository job because the entry is missing.");
                picture_complete = false;
                continue;
            };

            let mode = repository.mode.as_deref().unwrap_or_default();
            let outcome = if mode.eq_ignore_ascii_case(modes::PROVIDER) {
                self.run_provider_mode(
                    settings,
                    repository,
                    storage.as_ref(),
                    &expected,
                    repository_concurrency,
                    metadata_concurrency,
                    &download_throttle,
                    cancel,
                )
                .await
            } else if mode.eq_ignore_ascii_case(modes::URL) {
                self.run_url_mode(
                    settings,
                    repository,
                    storage.as_ref(),
                    &expected,
                    repository_concurrency,
                    cancel,
                )
                .await
            } else {
                tracing::warn!("Skipping repository job because mode is invalid. mode={mode}.");
                Ok((0, 0, false))
            };

            match outcome {
                Ok((synced, skipped, complete)) => {
                    synced_total += synced;
                    skipped_total += skipped;
                    picture_complete &= complete;
                }
                Err(error) => {
                    tracing::error!("Repository job failed. mode={mode}, error={error}.");
                    picture_complete = false;
                }
            }
        }

        if picture_complete {
            self.mirror_store
                .remove_orphans(&expected.into_inner().unwrap());
        } else {
            tracing::info!("Skipping local mirror cleanup because the repository set for this run is incomplete.");
        }

        tracing::info!(
            "Repository run completed. syncedRepositories={synced_total}, skippedInaccessible={skipped_total}."
        );
        Ok(())
    }

    #[allow(clippy::too_many_arguments)]
    async fn run_provider_mode(
        &self,
        settings: &Settings,
        job: &RepositoryJobConfig,
        storage: &dyn ObjectStorage,
        expected: &Mutex<HashSet<String>>,
        repository_concurrency: usize,
        metadata_concurrency: usize,
        download_throttle: &Arc<Semaphore>,
        cancel: &CancellationToken,
    ) -> Result<(usize, usize, bool)> {
        let (Some(provider), Some(credential_name)) = (
            non_blank(job.provider.as_deref()),
            non_blank(job.credential.as_deref()),
        ) else {
            tracing::warn!(
                "Skipping provider repository job because provider or credential is missing."
            );
            return Ok((0, 0, false));
        };

        let Some(credential) = settings.credential(credential_name) else {
            tracing::warn!(
                "Skipping provider repository job because credential is missing. provider={provider}, credential={credential_name}."
            );
            return Ok((0, 0, false));
        };

        tracing::info!("Provider repository discovery started. provider={provider}.");
        let Some(client) = self.providers.resolve(provider) else {
            tracing::warn!("Skipping provider repository job because no client is registered. provider={provider}.");
            return Ok((0, 0, false));
        };

        if job.include_snippets == Some(true) && !client.supports_snippets() {
            tracing::warn!(
                "Provider does not support gists or snippets, so includeSnippets is ignored. provider={provider}."
            );
        }

        let discovered = match client.list_repositories(job, credential, cancel).await {
            Ok(list) => list,
            Err(error) => {
                // Discovery failed, so the full set is unknown — signal an incomplete picture.
                tracing::error!(
                    "Provider repository discovery failed. provider={provider}, error={error}."
                );
                return Ok((0, 0, false));
            }
        };
        tracing::info!(
            "Provider repository discovery completed. provider={provider}, repositories={}.",
            discovered.len()
        );

        let git_credential = resolve_git_credential(credential);
        let cache = job.cache != Some(false);
        let include_lfs = job.lfs != Some(false);
        let synced = AtomicUsize::new(0);
        let skipped = AtomicUsize::new(0);

        futures::stream::iter(discovered.iter())
            .for_each_concurrent(repository_concurrency, |repository| {
                let git_credential = git_credential.as_ref();
                let synced = &synced;
                let skipped = &skipped;
                async move {
                    if repository.clone_url.trim().is_empty() {
                        return;
                    }
                    let prefix = match resolve_provider_prefix(provider, repository) {
                        Ok(prefix) => prefix,
                        Err(error) => {
                            tracing::error!(
                                "Provider repository sync failed. provider={provider}, repository={}, error={error}.",
                                repository.clone_url
                            );
                            return;
                        }
                    };
                    expected
                        .lock()
                        .unwrap()
                        .insert(LocalMirrorStore::mirror_directory_name(&prefix));

                    match self
                        .sync_repository_snapshot(
                            modes::PROVIDER,
                            &repository.clone_url,
                            &prefix,
                            cache,
                            include_lfs,
                            git_credential,
                            storage,
                            cancel,
                        )
                        .await
                    {
                        Ok(()) => {
                            synced.fetch_add(1, Ordering::Relaxed);
                            if should_back_up_project_metadata(job, repository) {
                                if let Err(error) = self
                                    .metadata
                                    .sync(
                                        job,
                                        repository,
                                        &prefix,
                                        credential,
                                        storage,
                                        metadata_concurrency,
                                        download_throttle,
                                        cancel,
                                    )
                                    .await
                                {
                                    tracing::error!(
                                        "Project metadata sync failed. provider={provider}, repository={}, error={error}.",
                                        repository.clone_url
                                    );
                                }
                            }
                        }
                        Err(SnapshotError::Cancelled) => {}
                        Err(SnapshotError::RemoteInaccessible(detail)) => {
                            skipped.fetch_add(1, Ordering::Relaxed);
                            tracing::warn!(
                                "Skipped repository because its remote is not accessible; it may be private, removed, or the credential lacks access. provider={provider}, repository={}.",
                                repository.clone_url
                            );
                            tracing::debug!(
                                "Remote access failure detail. provider={provider}, repository={}, detail={detail}.",
                                repository.clone_url
                            );
                        }
                        Err(SnapshotError::Other(error)) => {
                            tracing::error!(
                                "Provider repository sync failed. provider={provider}, repository={}, error={error}.",
                                repository.clone_url
                            );
                        }
                    }
                }
            })
            .await;

        Ok((synced.into_inner(), skipped.into_inner(), true))
    }

    async fn run_url_mode(
        &self,
        settings: &Settings,
        job: &RepositoryJobConfig,
        storage: &dyn ObjectStorage,
        expected: &Mutex<HashSet<String>>,
        repository_concurrency: usize,
        cancel: &CancellationToken,
    ) -> Result<(usize, usize, bool)> {
        let Some(urls) = job.urls.as_ref().filter(|u| !u.is_empty()) else {
            tracing::warn!("Skipping URL repository job because url is missing.");
            return Ok((0, 0, false));
        };

        // Collapse duplicate URLs (case-insensitive, since storage keys lowercase every segment),
        // warning on each dropped one so a copy-paste mistake is visible.
        let mut repository_urls = Vec::with_capacity(urls.len());
        let mut seen = HashSet::new();
        for configured in urls {
            let trimmed = configured.trim();
            if trimmed.is_empty() {
                continue;
            }
            if seen.insert(trimmed.to_lowercase()) {
                repository_urls.push(configured.clone());
            } else {
                tracing::warn!(
                    "Ignoring duplicate repository URL in URL job. repository={configured}."
                );
            }
        }

        if repository_urls.is_empty() {
            tracing::warn!("Skipping URL repository job because it has no usable url.");
            return Ok((0, 0, false));
        }

        let mut git_credential = None;
        if let Some(credential_name) = non_blank(job.credential.as_deref()) {
            let Some(credential) = settings.credential(credential_name) else {
                tracing::warn!("Skipping URL repository job because credential is missing. credential={credential_name}.");
                return Ok((0, 0, false));
            };
            git_credential = resolve_git_credential(credential);
        }

        let cache = job.cache != Some(false);
        let include_lfs = job.lfs != Some(false);
        let synced = AtomicUsize::new(0);
        let skipped = AtomicUsize::new(0);

        futures::stream::iter(repository_urls.iter())
            .for_each_concurrent(repository_concurrency, |url| {
                let git_credential = git_credential.as_ref();
                let synced = &synced;
                let skipped = &skipped;
                async move {
                    let prefix = match repo_path::parse(url) {
                        Ok(info) => key_builder::build_url_repository_prefix(&info),
                        Err(error) => {
                            tracing::error!("URL repository sync failed. repository={url}, error={error}.");
                            return;
                        }
                    };
                    expected
                        .lock()
                        .unwrap()
                        .insert(LocalMirrorStore::mirror_directory_name(&prefix));

                    match self
                        .sync_repository_snapshot(
                            modes::URL,
                            url,
                            &prefix,
                            cache,
                            include_lfs,
                            git_credential,
                            storage,
                            cancel,
                        )
                        .await
                    {
                        Ok(()) => {
                            synced.fetch_add(1, Ordering::Relaxed);
                        }
                        Err(SnapshotError::Cancelled) => {}
                        Err(SnapshotError::RemoteInaccessible(detail)) => {
                            skipped.fetch_add(1, Ordering::Relaxed);
                            tracing::warn!(
                                "Skipped repository because its remote is not accessible; it may be private, removed, or require credentials. repository={url}."
                            );
                            tracing::debug!("Remote access failure detail. repository={url}, detail={detail}.");
                        }
                        Err(SnapshotError::Other(error)) => {
                            tracing::error!("URL repository sync failed. repository={url}, error={error}.");
                        }
                    }
                }
            })
            .await;

        // The URL set is fully known from config (no discovery), so the picture stays complete even when
        // an individual URL fails.
        Ok((synced.into_inner(), skipped.into_inner(), true))
    }

    #[allow(clippy::too_many_arguments)]
    async fn sync_repository_snapshot(
        &self,
        mode: &str,
        repository_url: &str,
        repository_prefix: &str,
        cache: bool,
        include_lfs: bool,
        credential: Option<&GitCredential>,
        storage: &dyn ObjectStorage,
        cancel: &CancellationToken,
    ) -> Result<(), SnapshotError> {
        let local_path = self.mirror_store.get_mirror_path(repository_prefix);
        tracing::info!("Repository sync started. mode={mode}, repository={repository_url}.");
        tracing::debug!(
            "Repository working paths resolved. mode={mode}, repository={repository_url}, localPath={}, targetPrefix={repository_prefix}.",
            local_path.display()
        );

        self.git
            .sync_bare_repository(
                repository_url,
                &local_path,
                credential,
                cache,
                include_lfs,
                cancel,
            )
            .await?;

        let timestamp = chrono::Utc::now().timestamp();
        let archive_key = key_builder::build_archive_object_key(repository_prefix, timestamp);
        storage
            .upload_directory_as_targz(&local_path, &archive_key, cancel)
            .await
            .map_err(SnapshotError::Other)?;

        let document = RepositoryMetadataDocument {
            mode: mode.to_string(),
            repository_url: repository_url.to_string(),
            updated_at_unix_seconds: timestamp,
        };
        let json = documents::serialize(&document).map_err(SnapshotError::Other)?;
        storage
            .upload_text(
                &key_builder::build_repository_metadata_object_key(repository_prefix),
                &json,
                cancel,
            )
            .await
            .map_err(SnapshotError::Other)?;

        // A non-cached mirror exists only to build this snapshot; delete it now the upload succeeded.
        if !cache {
            tracing::debug!(
                "Removing local mirror after upload (cache disabled). repository={repository_url}."
            );
            self.mirror_store.try_delete_mirror(repository_prefix);
        }

        tracing::info!(
            "Repository sync completed. mode={mode}, repository={repository_url}, destination={repository_prefix}."
        );
        Ok(())
    }
}

/// Issues, MRs, and releases are backed up only for owned repositories: never starred (even with
/// includeStarred), never gists/snippets ← `ShouldBackUpProjectMetadata`.
fn should_back_up_project_metadata(
    job: &RepositoryJobConfig,
    discovered: &DiscoveredRepository,
) -> bool {
    (job.include_issues == Some(true)
        || job.include_merge_requests == Some(true)
        || job.include_releases == Some(true))
        && discovered.kind == DiscoveredRepositoryKind::Repository
        && !discovered.is_starred
}

/// Resolves the storage prefix for a discovered resource ← `ResolveProviderPrefix`.
fn resolve_provider_prefix(
    provider: &str,
    discovered: &DiscoveredRepository,
) -> Result<String, String> {
    let identifier = discovered.identifier.as_deref().unwrap_or_default();
    match discovered.kind {
        DiscoveredRepositoryKind::Gist => Ok(key_builder::build_snippet_resource_prefix(
            provider, identifier,
        )),
        DiscoveredRepositoryKind::Snippet
            if discovered
                .parent_url
                .as_deref()
                .is_none_or(|p| p.trim().is_empty()) =>
        {
            Ok(key_builder::build_snippet_resource_prefix(
                provider, identifier,
            ))
        }
        DiscoveredRepositoryKind::Snippet => {
            let parent = discovered.parent_url.as_deref().unwrap_or_default();
            let project_info = repo_path::parse(parent)?;
            let project_prefix =
                key_builder::build_provider_repository_prefix(provider, &project_info);
            Ok(key_builder::build_nested_snippet_prefix(
                &project_prefix,
                identifier,
            ))
        }
        DiscoveredRepositoryKind::Repository => {
            let info = repo_path::parse(&discovered.clone_url)?;
            Ok(key_builder::build_provider_repository_prefix(
                provider, &info,
            ))
        }
    }
}

/// Resolves a git credential from a forge credential ← `CredentialResolver.ResolveGitCredential`.
fn resolve_git_credential(credential: &CredentialConfig) -> Option<GitCredential> {
    let api_key = credential
        .api_key
        .as_deref()
        .map(str::trim)
        .filter(|k| !k.is_empty())?;
    let username = credential
        .username
        .as_deref()
        .map(str::trim)
        .filter(|u| !u.is_empty())
        .unwrap_or("git");
    Some(GitCredential::new(username, api_key))
}

fn non_blank(value: Option<&str>) -> Option<&str> {
    value.map(str::trim).filter(|v| !v.is_empty())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn discovered(kind: DiscoveredRepositoryKind, clone_url: &str) -> DiscoveredRepository {
        DiscoveredRepository {
            clone_url: clone_url.to_string(),
            web_url: None,
            kind,
            identifier: None,
            parent_url: None,
            provider_project_id: None,
            is_starred: false,
        }
    }

    #[test]
    fn provider_prefix_covers_every_kind() {
        let repo = discovered(
            DiscoveredRepositoryKind::Repository,
            "https://github.com/o/r.git",
        );
        assert_eq!(
            resolve_provider_prefix("github", &repo).unwrap(),
            "repositories/provider/github/o/r"
        );

        let mut gist = discovered(
            DiscoveredRepositoryKind::Gist,
            "https://gist.github.com/abc.git",
        );
        gist.identifier = Some("abc".into());
        assert_eq!(
            resolve_provider_prefix("github", &gist).unwrap(),
            "snippets/provider/github/abc"
        );

        let mut personal = discovered(
            DiscoveredRepositoryKind::Snippet,
            "https://gitlab.com/-/snippets/9.git",
        );
        personal.identifier = Some("9".into());
        assert_eq!(
            resolve_provider_prefix("gitlab", &personal).unwrap(),
            "snippets/provider/gitlab/9"
        );

        let mut project = discovered(
            DiscoveredRepositoryKind::Snippet,
            "https://gitlab.com/o/r/-/snippets/7.git",
        );
        project.identifier = Some("7".into());
        project.parent_url = Some("https://gitlab.com/o/r".into());
        assert_eq!(
            resolve_provider_prefix("gitlab", &project).unwrap(),
            "repositories/provider/gitlab/o/r/snippets/7"
        );
    }

    #[test]
    fn metadata_gate_is_owned_repository_only() {
        let job = RepositoryJobConfig {
            include_issues: Some(true),
            ..Default::default()
        };

        let owned = discovered(DiscoveredRepositoryKind::Repository, "https://h/o/r.git");
        assert!(should_back_up_project_metadata(&job, &owned));

        let mut starred = owned.clone();
        starred.is_starred = true;
        assert!(
            !should_back_up_project_metadata(&job, &starred),
            "never for starred"
        );

        let gist = discovered(DiscoveredRepositoryKind::Gist, "https://h/g.git");
        assert!(
            !should_back_up_project_metadata(&job, &gist),
            "never for gists"
        );

        let no_flags = RepositoryJobConfig::default();
        assert!(
            !should_back_up_project_metadata(&no_flags, &owned),
            "no include flags"
        );
    }

    #[test]
    fn git_credential_defaults_username_to_git() {
        let mut credential = CredentialConfig {
            api_key: Some("  token  ".into()),
            ..Default::default()
        };
        let resolved = resolve_git_credential(&credential).unwrap();
        assert_eq!(resolved.username, "git");
        assert_eq!(resolved.password, "token");

        credential.username = Some("alice".into());
        assert_eq!(
            resolve_git_credential(&credential).unwrap().username,
            "alice"
        );

        // No api key -> no credential.
        assert!(resolve_git_credential(&CredentialConfig::default()).is_none());
    }
}
