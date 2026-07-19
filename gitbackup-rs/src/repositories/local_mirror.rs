//! On-disk git mirror store ← `LocalMirrorStore`.
//!
//! Each repository maps to one flat directory under `{workingRoot}/repositories`, named by a
//! deterministic SHA-256 of its storage prefix, so cleaning up mirrors for repositories no longer
//! backed up is a simple set difference.

use sha2::{Digest, Sha256};
use std::collections::HashSet;
use std::path::{Path, PathBuf};

pub struct LocalMirrorStore {
    mirrors_root: PathBuf,
}

impl LocalMirrorStore {
    pub fn new(working_root: impl AsRef<Path>) -> Self {
        Self {
            mirrors_root: working_root.as_ref().join("repositories"),
        }
    }

    pub fn get_mirror_path(&self, repository_prefix: &str) -> PathBuf {
        self.mirrors_root
            .join(Self::mirror_directory_name(repository_prefix))
    }

    /// The deterministic directory name for a repository prefix (lowercase hex SHA-256).
    pub fn mirror_directory_name(repository_prefix: &str) -> String {
        let digest = Sha256::digest(repository_prefix.as_bytes());
        let mut hex = String::with_capacity(digest.len() * 2);
        for byte in digest {
            hex.push_str(&format!("{byte:02x}"));
        }
        hex
    }

    pub fn try_delete_mirror(&self, repository_prefix: &str) {
        try_delete_directory(&self.get_mirror_path(repository_prefix));
    }

    /// Deletes any mirror directory not in `expected` — i.e. one belonging to a repository no longer
    /// backed up. Callers must pass a *complete* expected set, otherwise a transient discovery error
    /// could remove a valid mirror.
    pub fn remove_orphans(&self, expected: &HashSet<String>) {
        let Ok(entries) = std::fs::read_dir(&self.mirrors_root) else {
            return;
        };
        for entry in entries.flatten() {
            if !entry.path().is_dir() {
                continue;
            }
            let name = entry.file_name().to_string_lossy().to_string();
            if expected.contains(&name) {
                continue;
            }
            if try_delete_directory(&entry.path()) {
                tracing::info!(
                    "Removed local mirror for a repository that is no longer backed up. path={}.",
                    entry.path().display()
                );
            }
        }
    }
}

fn try_delete_directory(path: &Path) -> bool {
    if !path.exists() {
        return false;
    }
    match std::fs::remove_dir_all(path) {
        Ok(()) => true,
        Err(error) => {
            tracing::warn!(
                "Failed to remove local mirror directory. path={}, error={error}.",
                path.display()
            );
            false
        }
    }
}
