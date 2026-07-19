//! Project metadata sync ← `ProjectMetadataSyncService`.
//!
//! Backs up one owned repository's issues, merge requests, and releases (with embedded comments and,
//! when enabled, downloaded attachments) as latest-state JSON documents, then reconciles the stored set
//! to match the provider. A partial fetch never drives a delete: a listing failure leaves the manifest
//! untouched, and any single item's failure skips reconciliation so a not-yet-uploaded document is never
//! mistaken for one removed upstream.

use super::documents::serialize;
use crate::config::{CredentialConfig, RepositoryJobConfig};
use crate::paths::key_builder;
use crate::providers::attachments::redact_url;
use crate::providers::client::ProjectMetadataProviderClient;
use crate::providers::models::{
    ArtifactItem, BackedUpAttachment, BackedUpIssue, BackedUpMergeRequest, BackedUpRelease,
    CollectionManifestEntry, ProjectMetadataContext, ReleaseManifestEntry,
};
use crate::providers::{DiscoveredRepository, RepositoryProviderClientFactory};
use crate::storage::{mime, ObjectStorage};
use anyhow::Result;
use futures::stream::StreamExt;
use serde::Serialize;
use serde_json::Value;
use std::collections::HashSet;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use tokio::sync::Semaphore;
use tokio_util::sync::CancellationToken;

pub struct ProjectMetadataSyncService {
    provider_factory: Arc<RepositoryProviderClientFactory>,
}

impl ProjectMetadataSyncService {
    pub fn new(provider_factory: Arc<RepositoryProviderClientFactory>) -> Self {
        Self { provider_factory }
    }

    #[allow(clippy::too_many_arguments)]
    pub async fn sync(
        &self,
        job: &RepositoryJobConfig,
        discovered: &DiscoveredRepository,
        repository_prefix: &str,
        credential: &CredentialConfig,
        storage: &dyn ObjectStorage,
        metadata_concurrency: usize,
        download_throttle: &Arc<Semaphore>,
        cancel: &CancellationToken,
    ) -> Result<()> {
        let provider = job.provider.as_deref().unwrap_or_default();
        let Some(client) = self.provider_factory.resolve_metadata(provider) else {
            tracing::debug!(
                "Provider does not support project metadata backup. provider={provider}."
            );
            return Ok(());
        };

        let display = discovered
            .web_url
            .clone()
            .unwrap_or_else(|| discovered.clone_url.clone());
        let context = ProjectMetadataContext {
            clone_url: discovered.clone_url.clone(),
            web_url: discovered.web_url.clone(),
            provider_project_id: discovered.provider_project_id.clone(),
            base_url: job.base_url.clone(),
            concurrency: metadata_concurrency.max(1),
            download_throttle: Some(Arc::clone(download_throttle)),
        };

        if job.include_issues == Some(true) {
            let items = client.list_issues(&context, credential, cancel).await;
            self.back_up_collection(
                CollectionParams {
                    label: "Issue",
                    support_label: "issues",
                    count_label: "issues",
                    supported: client.supports_issues(),
                    include_artifacts: job.include_issue_artifacts == Some(true),
                    collection_prefix: key_builder::build_issues_collection_prefix(
                        repository_prefix,
                    ),
                    manifest_key: key_builder::build_issues_manifest_object_key(repository_prefix),
                },
                items,
                |issue: &BackedUpIssue| issue.number.to_string(),
                |slug| key_builder::build_issue_object_key(repository_prefix, slug),
                |slug, file| {
                    key_builder::build_issue_attachment_object_key(repository_prefix, slug, file)
                },
                |issue: &BackedUpIssue| {
                    to_value(CollectionManifestEntry {
                        number: issue.number,
                        title: Some(issue.title.clone()),
                        state: issue.state.clone(),
                        updated_at: issue.updated_at.clone(),
                    })
                },
                client,
                &context,
                credential,
                storage,
                &display,
                cancel,
            )
            .await;
        }

        if job.include_merge_requests == Some(true) {
            let items = client
                .list_merge_requests(&context, credential, cancel)
                .await;
            self.back_up_collection(
                CollectionParams {
                    label: "Merge request",
                    support_label: "merge requests",
                    count_label: "mergeRequests",
                    supported: client.supports_merge_requests(),
                    include_artifacts: job.include_merge_requests_artifacts == Some(true),
                    collection_prefix: key_builder::build_merge_requests_collection_prefix(
                        repository_prefix,
                    ),
                    manifest_key: key_builder::build_merge_requests_manifest_object_key(
                        repository_prefix,
                    ),
                },
                items,
                |mr: &BackedUpMergeRequest| mr.number.to_string(),
                |slug| key_builder::build_merge_request_object_key(repository_prefix, slug),
                |slug, file| {
                    key_builder::build_merge_request_attachment_object_key(
                        repository_prefix,
                        slug,
                        file,
                    )
                },
                |mr: &BackedUpMergeRequest| {
                    to_value(CollectionManifestEntry {
                        number: mr.number,
                        title: Some(mr.title.clone()),
                        state: mr.state.clone(),
                        updated_at: mr.updated_at.clone(),
                    })
                },
                client,
                &context,
                credential,
                storage,
                &display,
                cancel,
            )
            .await;
        }

        if job.include_releases == Some(true) {
            let items = client.list_releases(&context, credential, cancel).await;
            self.back_up_collection(
                CollectionParams {
                    label: "Release",
                    support_label: "releases",
                    count_label: "releases",
                    supported: client.supports_releases(),
                    include_artifacts: job.include_release_artifacts == Some(true),
                    collection_prefix: key_builder::build_releases_collection_prefix(
                        repository_prefix,
                    ),
                    manifest_key: key_builder::build_releases_manifest_object_key(
                        repository_prefix,
                    ),
                },
                items,
                |release: &BackedUpRelease| resolve_release_slug(&release.tag),
                |slug| key_builder::build_release_object_key(repository_prefix, slug),
                |slug, file| {
                    key_builder::build_release_attachment_object_key(repository_prefix, slug, file)
                },
                |release: &BackedUpRelease| {
                    to_value(ReleaseManifestEntry {
                        tag: release.tag.clone(),
                        name: release.name.clone(),
                        published_at: release.published_at.clone(),
                    })
                },
                client,
                &context,
                credential,
                storage,
                &display,
                cancel,
            )
            .await;
        }

        Ok(())
    }

    #[allow(clippy::too_many_arguments)]
    async fn back_up_collection<T, FSlug, FKey, FAttKey, FEntry>(
        &self,
        params: CollectionParams,
        items: Result<Vec<T>>,
        get_slug: FSlug,
        build_object_key: FKey,
        build_attachment_key: FAttKey,
        to_manifest_entry: FEntry,
        client: &dyn ProjectMetadataProviderClient,
        context: &ProjectMetadataContext,
        credential: &CredentialConfig,
        storage: &dyn ObjectStorage,
        repository_display: &str,
        cancel: &CancellationToken,
    ) where
        T: ArtifactItem + Serialize + Send,
        FSlug: Fn(&T) -> String + Sync,
        FKey: Fn(&str) -> String + Sync,
        FAttKey: Fn(&str, &str) -> String + Sync,
        FEntry: Fn(&T) -> Value + Sync,
    {
        if !params.supported {
            tracing::debug!("Provider does not support {}.", params.support_label);
            return;
        }

        tracing::info!(
            "{} backup started. repository={repository_display}.",
            params.label
        );

        let items = match items {
            Ok(items) => items,
            Err(error) => {
                // Listing/paging failed, so the set is incomplete: leave the previous manifest in place
                // and delete nothing.
                tracing::error!(
                    "{} backup failed while listing. repository={repository_display}, error={error}.",
                    params.label
                );
                return;
            }
        };

        let download_artifacts = params.include_artifacts && client.supports_artifacts();
        let manifest_entries: Mutex<Vec<Value>> = Mutex::new(Vec::new());
        let fetched_slugs: Mutex<HashSet<String>> = Mutex::new(HashSet::new());
        let backed_up = AtomicUsize::new(0);
        let any_item_failed = AtomicBool::new(false);

        futures::stream::iter(items)
            .for_each_concurrent(context.concurrency, |mut item| {
                let manifest_entries = &manifest_entries;
                let fetched_slugs = &fetched_slugs;
                let backed_up = &backed_up;
                let any_item_failed = &any_item_failed;
                let get_slug = &get_slug;
                let build_object_key = &build_object_key;
                let build_attachment_key = &build_attachment_key;
                let to_manifest_entry = &to_manifest_entry;
                async move {
                    let slug = get_slug(&item);
                    let result: Result<Value> = async {
                        if download_artifacts {
                            download_attachments(
                                item.attachments_mut(),
                                &slug,
                                client,
                                context,
                                credential,
                                storage,
                                build_attachment_key,
                                cancel,
                            )
                            .await;
                        } else {
                            // Artifacts gated off: record no attachment references.
                            item.set_attachments(Vec::new());
                        }
                        let json = serialize(&item)?;
                        storage.upload_text(&build_object_key(&slug), &json, cancel).await?;
                        Ok(to_manifest_entry(&item))
                    }
                    .await;

                    match result {
                        Ok(entry) => {
                            manifest_entries.lock().unwrap().push(entry);
                            fetched_slugs.lock().unwrap().insert(slug);
                            backed_up.fetch_add(1, Ordering::Relaxed);
                        }
                        Err(error) => {
                            any_item_failed.store(true, Ordering::Relaxed);
                            // Record the slug even on failure so reconcile never treats this still-present
                            // item as removed (reconcile is skipped this run regardless).
                            fetched_slugs.lock().unwrap().insert(slug.clone());
                            tracing::error!(
                                "{} item backup failed. repository={repository_display}, slug={slug}, error={error}.",
                                params.label
                            );
                        }
                    }
                }
            })
            .await;

        let manifest_entries = manifest_entries.into_inner().unwrap();
        let fetched_slugs = fetched_slugs.into_inner().unwrap();
        let count = backed_up.into_inner();

        if let Ok(manifest_json) = serialize(&manifest_entries) {
            if let Err(error) = storage
                .upload_text(&params.manifest_key, &manifest_json, cancel)
                .await
            {
                tracing::error!(
                    "{} manifest upload failed. repository={repository_display}, error={error}.",
                    params.label
                );
            }
        }

        if any_item_failed.into_inner() {
            tracing::warn!(
                "{} reconciliation skipped because one or more items failed to back up. repository={repository_display}.",
                params.label
            );
            tracing::warn!(
                "{} backup finished incomplete; stored items were kept and nothing was deleted. repository={repository_display}, {}={count}.",
                params.label,
                params.count_label
            );
            return;
        }

        reconcile(
            &fetched_slugs,
            &params.collection_prefix,
            &params.manifest_key,
            storage,
            cancel,
        )
        .await;

        tracing::info!(
            "{} backup completed. repository={repository_display}, {}={count}.",
            params.label,
            params.count_label
        );
    }
}

struct CollectionParams {
    label: &'static str,
    support_label: &'static str,
    count_label: &'static str,
    supported: bool,
    include_artifacts: bool,
    collection_prefix: String,
    manifest_key: String,
}

fn to_value<T: Serialize>(value: T) -> Value {
    serde_json::to_value(value).unwrap_or(Value::Null)
}

/// Downloads each downloadable attachment straight to storage, recording its storage key. A single bad
/// attachment is logged and skipped — it never fails the whole item.
#[allow(clippy::too_many_arguments)]
async fn download_attachments<FAttKey: Fn(&str, &str) -> String>(
    attachments: &mut [BackedUpAttachment],
    slug: &str,
    client: &dyn ProjectMetadataProviderClient,
    context: &ProjectMetadataContext,
    credential: &CredentialConfig,
    storage: &dyn ObjectStorage,
    build_attachment_key: &FAttKey,
    cancel: &CancellationToken,
) {
    for attachment in attachments.iter_mut() {
        if cancel.is_cancelled() {
            return;
        }
        if !attachment.downloadable {
            continue;
        }

        let download_url = attachment.download_url.clone();
        let file_name = attachment.file_name.clone();
        let original_path = attachment.original_path.clone();

        // Bound concurrent downloads across the whole run; the permit is held for the transfer.
        let _permit = match context.download_throttle.as_ref() {
            Some(semaphore) => match semaphore.acquire().await {
                Ok(permit) => Some(permit),
                Err(_) => return,
            },
            None => None,
        };

        match client
            .open_attachment(context, credential, &download_url, cancel)
            .await
        {
            Ok(stream) => {
                let object_key = build_attachment_key(slug, &file_name);
                let content_type = mime::resolve_from_file_name(&file_name);
                let known_length = stream.known_length;
                match storage
                    .upload_stream(
                        &object_key,
                        stream.reader,
                        content_type,
                        known_length,
                        cancel,
                    )
                    .await
                {
                    Ok(()) => {
                        attachment.storage_key = Some(object_key);
                        attachment.content_type = Some(content_type.to_string());
                        attachment.size_bytes =
                            known_length.map(|l| l as i64).or(attachment.size_bytes);
                    }
                    Err(error) => {
                        tracing::error!(
                            "Attachment backup failed. originalPath={}, error={error}.",
                            redact_url(&original_path)
                        );
                    }
                }
            }
            Err(error) => {
                tracing::error!(
                    "Attachment backup failed. originalPath={}, error={error}.",
                    redact_url(&original_path)
                );
            }
        }
    }
}

/// Deletes stored documents/attachments for items the provider no longer returns ← `ReconcileAsync`.
async fn reconcile(
    fetched_slugs: &HashSet<String>,
    collection_prefix: &str,
    manifest_key: &str,
    storage: &dyn ObjectStorage,
    cancel: &CancellationToken,
) {
    let normalized_prefix = key_builder::ensure_prefix(collection_prefix);
    let existing = match storage.list_object_keys(collection_prefix, cancel).await {
        Ok(keys) => keys,
        Err(error) => {
            tracing::error!(
                "Reconciliation listing failed. prefix={collection_prefix}, error={error}."
            );
            return;
        }
    };

    let mut to_delete = Vec::new();
    for object_key in existing {
        if object_key == manifest_key || !object_key.starts_with(&normalized_prefix) {
            continue;
        }
        let relative = &object_key[normalized_prefix.len()..];
        if let Some(slug) = reconcilable_slug(relative) {
            if !fetched_slugs.contains(slug) {
                to_delete.push(object_key);
            }
        }
    }

    if !to_delete.is_empty() {
        tracing::debug!(
            "Reconciling stored collection. removedObjects={}.",
            to_delete.len()
        );
        if let Err(error) = storage.delete_objects(&to_delete, cancel).await {
            tracing::error!("Reconciliation delete failed. error={error}.");
        }
    }
}

/// Extracts the owning item slug from a collection-relative key: `{slug}.json` or
/// `attachments/{slug}/...`. `None` for the manifest and anything else ← `TryGetReconcilableSlug`.
fn reconcilable_slug(relative_key: &str) -> Option<&str> {
    match relative_key.find('/') {
        None => relative_key
            .strip_suffix(".json")
            .filter(|slug| !slug.is_empty()),
        Some(first_slash) => {
            if &relative_key[..first_slash] != key_builder::ATTACHMENTS_COLLECTION_SEGMENT {
                return None;
            }
            let after = &relative_key[first_slash + 1..];
            let slug = after.split('/').next().unwrap_or(after);
            (!slug.is_empty()).then_some(slug)
        }
    }
}

/// Turns a release tag into a safe storage-key leaf, guarding the reserved `index` base name.
fn resolve_release_slug(tag: &str) -> String {
    use crate::providers::attachments::{sanitize_file_name, short_hash};
    let slug = sanitize_file_name(tag);
    let reserved = key_builder::COLLECTION_MANIFEST_OBJECT_NAME
        .strip_suffix(".json")
        .unwrap_or("index");
    if slug == reserved {
        format!("{slug}-{}", short_hash(tag))
    } else {
        slug
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reconcilable_slug_parses_docs_and_attachments_only() {
        assert_eq!(reconcilable_slug("42.json"), Some("42"));
        assert_eq!(reconcilable_slug("attachments/42/shot.png"), Some("42"));
        assert_eq!(reconcilable_slug("attachments/7"), Some("7"));
        // index.json parses like any doc; the caller guards the manifest by key before calling this.
        assert_eq!(reconcilable_slug("index.json"), Some("index"));
        // Not reconcilable: a stray non-json file or an unknown subfolder.
        assert_eq!(reconcilable_slug("notes.txt"), None);
        assert_eq!(reconcilable_slug("other/42/x"), None);
    }

    #[test]
    fn release_slug_guards_reserved_index_name() {
        assert_eq!(resolve_release_slug("v1.0.0"), "v1.0.0");
        // A tag that sanitizes to "index" gets a short hash suffix.
        assert!(resolve_release_slug("index").starts_with("index-"));
    }
}
