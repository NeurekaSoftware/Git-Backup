//! Snapshot retention ← `RepositoryRetentionService`.
//!
//! The object listing is the source of truth: each archive key encodes its own unix timestamp, so
//! retention needs no side index. Snapshots are grouped by repository prefix, the newest
//! `retentionMinimum` are always protected, and anything older than the cutoff beyond that is deleted.
//! When a repository loses all its snapshots, its non-archive objects (advisory metadata.json plus
//! issues/, merge-requests/, releases/ documents and attachments) are reclaimed too — but nested project
//! snippets, which are independent repositories with their own retention, are left untouched.

use super::sync::StorageFactory;
use crate::config::Settings;
use crate::paths::key_builder;
use anyhow::Result;
use chrono::Utc;
use std::collections::{HashMap, HashSet};
use std::sync::atomic::{AtomicBool, Ordering};
use tokio_util::sync::CancellationToken;

pub struct RepositoryRetentionService {
    storage_factory: StorageFactory,
    retention_minimum_zero_warning_shown: AtomicBool,
}

/// What a retention pass decided to delete, plus the counts for the run summary.
struct RetentionPlan {
    keys_to_delete: Vec<String>,
    deleted_snapshots: usize,
    deleted_orphans: usize,
    emptied_repositories: usize,
}

impl RepositoryRetentionService {
    pub fn new(storage_factory: StorageFactory) -> Self {
        Self {
            storage_factory,
            retention_minimum_zero_warning_shown: AtomicBool::new(false),
        }
    }

    pub async fn run(&self, settings: &Settings, cancel: &CancellationToken) -> Result<()> {
        let retention_days = settings.storage.retention;
        let retention_minimum = settings.storage.retention_minimum.unwrap_or(1).max(0);

        let Some(retention_days) = retention_days.filter(|days| *days > 0) else {
            tracing::info!(
                "Retention is disabled. Repository snapshots will be kept indefinitely."
            );
            return Ok(());
        };

        if retention_minimum == 0 {
            if !self
                .retention_minimum_zero_warning_shown
                .swap(true, Ordering::Relaxed)
            {
                tracing::warn!(
                    "Retention minimum is set to 0. Repository snapshots can be deleted after the retention window, including repositories removed from configuration or whose URL changed."
                );
            }
        } else {
            self.retention_minimum_zero_warning_shown
                .store(false, Ordering::Relaxed);
        }

        tracing::info!(
            "Retention run started. retentionDays={retention_days}, retentionMinimum={retention_minimum}."
        );

        let storage = (self.storage_factory)(&settings.storage)?;
        let cutoff = Utc::now() - chrono::Duration::days(retention_days);
        let cutoff_unix = cutoff.timestamp();
        tracing::info!(
            "Retention cutoff resolved. cutoff={}.",
            cutoff.format("%Y-%m-%d %H:%M:%S UTC")
        );

        // The two roots are independent subtrees, so list them concurrently.
        let (repository_keys, snippet_keys) = tokio::join!(
            storage.list_object_keys(key_builder::REPOSITORIES_PREFIX, cancel),
            storage.list_object_keys(key_builder::SNIPPETS_PREFIX, cancel),
        );
        let mut all_keys = repository_keys?;
        all_keys.extend(snippet_keys?);

        let plan = plan_deletions(all_keys, cutoff_unix, retention_minimum as usize);
        if !plan.keys_to_delete.is_empty() {
            storage.delete_objects(&plan.keys_to_delete, cancel).await?;
        }

        tracing::info!(
            "Retention run completed. deletedSnapshots={}, deletedOrphanObjects={}, emptiedRepositories={}.",
            plan.deleted_snapshots,
            plan.deleted_orphans,
            plan.emptied_repositories
        );
        Ok(())
    }
}

/// Pure retention decision over a flat key listing: which keys to delete, and the run counts.
fn plan_deletions(
    all_keys: Vec<String>,
    cutoff_unix: i64,
    retention_minimum: usize,
) -> RetentionPlan {
    // Classify each key once: archive keys feed the grouping, everything else is a candidate orphan.
    let mut snapshots_by_repository: HashMap<String, Vec<(String, i64)>> = HashMap::new();
    let mut non_archive_keys = Vec::new();
    for object_key in all_keys {
        match key_builder::try_get_archive_timestamp(&object_key) {
            Some(timestamp) => {
                let prefix = key_builder::get_parent_prefix(&object_key).to_string();
                snapshots_by_repository
                    .entry(prefix)
                    .or_default()
                    .push((object_key, timestamp));
            }
            None => non_archive_keys.push(object_key),
        }
    }

    let mut expired_keys = Vec::new();
    let mut emptied_repositories: HashSet<String> = HashSet::new();
    for (repository_prefix, mut snapshots) in snapshots_by_repository {
        snapshots.sort_by_key(|snapshot| std::cmp::Reverse(snapshot.1)); // newest first
        let protected = retention_minimum.min(snapshots.len());
        let expired: Vec<String> = snapshots
            .iter()
            .skip(protected)
            .filter(|(_, timestamp)| *timestamp < cutoff_unix)
            .map(|(key, _)| key.clone())
            .collect();

        if expired.len() == snapshots.len() {
            emptied_repositories.insert(repository_prefix);
        }
        expired_keys.extend(expired);
    }

    // Deep collection prefixes reclaimable for each emptied repository.
    let mut reclaimable_prefixes: HashSet<String> = HashSet::new();
    for prefix in &emptied_repositories {
        reclaimable_prefixes.insert(format!(
            "{prefix}/{}",
            key_builder::ISSUES_COLLECTION_SEGMENT
        ));
        reclaimable_prefixes.insert(format!(
            "{prefix}/{}",
            key_builder::MERGE_REQUESTS_COLLECTION_SEGMENT
        ));
        reclaimable_prefixes.insert(format!(
            "{prefix}/{}",
            key_builder::RELEASES_COLLECTION_SEGMENT
        ));
    }

    let orphan_keys: Vec<String> = non_archive_keys
        .into_iter()
        .filter(|key| is_reclaimable_orphan(key, &emptied_repositories, &reclaimable_prefixes))
        .collect();

    let deleted_snapshots = expired_keys.len();
    let deleted_orphans = orphan_keys.len();
    let emptied = emptied_repositories.len();
    let mut keys_to_delete = expired_keys;
    keys_to_delete.extend(orphan_keys);

    RetentionPlan {
        keys_to_delete,
        deleted_snapshots,
        deleted_orphans,
        emptied_repositories: emptied,
    }
}

fn is_reclaimable_orphan(
    object_key: &str,
    emptied_repositories: &HashSet<String>,
    reclaimable_prefixes: &HashSet<String>,
) -> bool {
    // The advisory metadata.json is a direct child of the repository prefix.
    if emptied_repositories.contains(key_builder::get_parent_prefix(object_key)) {
        return true;
    }

    // Documents/attachments nest deeper; probe this key's own ancestor prefixes.
    let mut search_from = 0;
    while let Some(offset) = object_key[search_from..].find('/') {
        let slash = search_from + offset;
        if slash > 0 && reclaimable_prefixes.contains(&object_key[..slash]) {
            return true;
        }
        search_from = slash + 1;
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;

    const CUTOFF: i64 = 1000;
    const OLD: &str = "500"; // < cutoff -> expired
    const RECENT: &str = "2000"; // >= cutoff -> kept

    fn plan(keys: &[&str], minimum: usize) -> RetentionPlan {
        plan_deletions(
            keys.iter().map(|k| k.to_string()).collect(),
            CUTOFF,
            minimum,
        )
    }

    #[test]
    fn protects_newest_and_expires_old_beyond_minimum() {
        let repo = "repositories/url/h/o/r";
        let keys = [
            format!("{repo}/{OLD}_repo.tar.gz"),
            format!("{repo}/600_repo.tar.gz"),
            format!("{repo}/{RECENT}_repo.tar.gz"),
            format!("{repo}/metadata.json"),
        ];
        let keys: Vec<&str> = keys.iter().map(String::as_str).collect();
        let result = plan(&keys, 1);

        // Newest (2000) protected; the two old snapshots expire; metadata.json kept (repo not emptied).
        assert_eq!(result.deleted_snapshots, 2);
        assert_eq!(result.deleted_orphans, 0);
        assert_eq!(result.emptied_repositories, 0);
        assert!(result
            .keys_to_delete
            .contains(&format!("{repo}/{OLD}_repo.tar.gz")));
        assert!(result
            .keys_to_delete
            .contains(&format!("{repo}/600_repo.tar.gz")));
        assert!(!result
            .keys_to_delete
            .contains(&format!("{repo}/{RECENT}_repo.tar.gz")));
        assert!(!result
            .keys_to_delete
            .contains(&format!("{repo}/metadata.json")));
    }

    #[test]
    fn emptied_repo_reclaims_orphans_but_leaves_nested_snippet() {
        let repo = "repositories/url/h/o/r";
        let snippet = "repositories/url/h/o/r/snippets/9";
        let keys = [
            // Repo has only an old snapshot -> emptied at minimum 0.
            format!("{repo}/{OLD}_repo.tar.gz"),
            format!("{repo}/metadata.json"),
            format!("{repo}/issues/1.json"),
            format!("{repo}/issues/attachments/1/x.png"),
            format!("{repo}/merge-requests/2.json"),
            // Nested project snippet has a recent snapshot -> NOT emptied, must be left alone.
            format!("{snippet}/{RECENT}_repo.tar.gz"),
            format!("{snippet}/metadata.json"),
        ];
        let keys: Vec<&str> = keys.iter().map(String::as_str).collect();
        let result = plan(&keys, 0);

        assert_eq!(result.emptied_repositories, 1, "only the repo empties");
        assert_eq!(result.deleted_snapshots, 1, "only the repo's old snapshot");
        // Orphans: metadata.json + issues doc + issues attachment + merge-request doc = 4.
        assert_eq!(result.deleted_orphans, 4);
        assert!(result
            .keys_to_delete
            .contains(&format!("{repo}/metadata.json")));
        assert!(result
            .keys_to_delete
            .contains(&format!("{repo}/issues/1.json")));
        assert!(result
            .keys_to_delete
            .contains(&format!("{repo}/issues/attachments/1/x.png")));
        assert!(result
            .keys_to_delete
            .contains(&format!("{repo}/merge-requests/2.json")));
        // The nested snippet's own objects are untouched by the parent's emptiness.
        assert!(!result
            .keys_to_delete
            .contains(&format!("{snippet}/metadata.json")));
        assert!(!result
            .keys_to_delete
            .contains(&format!("{snippet}/{RECENT}_repo.tar.gz")));
    }
}
