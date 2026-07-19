//! End-to-end sync-driver test using in-memory doubles for git and storage. Verifies the driver
//! produces the exact object keys and applies URL-mode dedup — the local stand-in for the CI key-tree
//! parity gate (which runs the real git + S3 against MinIO).

use async_trait::async_trait;
use gitbackup::config::{RepositoryJobConfig, Settings};
use gitbackup::git::{GitCredential, GitError, GitRepository};
use gitbackup::providers::RepositoryProviderClientFactory;
use gitbackup::repositories::{
    LocalMirrorStore, ProjectMetadataSyncService, RepositorySyncService,
};
use gitbackup::storage::ObjectStorage;
use std::path::Path;
use std::sync::{Arc, Mutex};
use tokio::io::AsyncRead;
use tokio_util::sync::CancellationToken;

/// Git double: succeeds without touching the network or disk.
struct MockGit;

#[async_trait]
impl GitRepository for MockGit {
    async fn sync_bare_repository(
        &self,
        _remote_url: &str,
        _local_path: &Path,
        _credential: Option<&GitCredential>,
        _cache: bool,
        _include_lfs: bool,
        _cancel: &CancellationToken,
    ) -> Result<(), GitError> {
        Ok(())
    }
}

/// Storage double: records every uploaded object key.
struct MockStorage {
    uploads: Arc<Mutex<Vec<String>>>,
}

#[async_trait]
impl ObjectStorage for MockStorage {
    async fn upload_directory_as_targz(
        &self,
        _local_dir: &Path,
        object_key: &str,
        _cancel: &CancellationToken,
    ) -> anyhow::Result<()> {
        self.uploads.lock().unwrap().push(object_key.to_string());
        Ok(())
    }

    async fn upload_text(
        &self,
        object_key: &str,
        _content: &str,
        _cancel: &CancellationToken,
    ) -> anyhow::Result<()> {
        self.uploads.lock().unwrap().push(object_key.to_string());
        Ok(())
    }

    async fn upload_stream(
        &self,
        _object_key: &str,
        _content: Box<dyn AsyncRead + Send + Unpin>,
        _content_type: &str,
        _known_length: Option<u64>,
        _cancel: &CancellationToken,
    ) -> anyhow::Result<()> {
        Ok(())
    }

    async fn list_object_keys(
        &self,
        _prefix: &str,
        _cancel: &CancellationToken,
    ) -> anyhow::Result<Vec<String>> {
        Ok(Vec::new())
    }

    async fn delete_objects(
        &self,
        _object_keys: &[String],
        _cancel: &CancellationToken,
    ) -> anyhow::Result<()> {
        Ok(())
    }
}

#[tokio::test]
async fn url_mode_uploads_expected_keys_and_dedupes() {
    let uploads = Arc::new(Mutex::new(Vec::new()));

    let mut settings = Settings::default();
    settings.concurrency.repositories = Some(2);
    settings.concurrency.metadata = Some(1);

    let job = RepositoryJobConfig {
        mode: Some("url".into()),
        // "backup" and "BACKUP" collapse to one (case-insensitive); "website" is distinct.
        urls: Some(vec![
            "https://code.neureka.dev/git/backup".into(),
            "https://code.neureka.dev/git/BACKUP".into(),
            "https://code.neureka.dev/git/website".into(),
        ]),
        enabled: Some(true),
        cache: Some(true),
        lfs: Some(false),
        ..Default::default()
    };
    settings.repositories = vec![Some(job)];

    let working_root = std::env::temp_dir().join(format!("gitbackup-sync-{}", std::process::id()));
    let uploads_for_factory = Arc::clone(&uploads);

    let service = RepositorySyncService::new(
        Arc::new(RepositoryProviderClientFactory::new().unwrap()),
        Arc::new(MockGit),
        Arc::new(move |_cfg: &_| {
            Ok(Box::new(MockStorage {
                uploads: Arc::clone(&uploads_for_factory),
            }) as Box<dyn ObjectStorage>)
        }),
        Arc::new(LocalMirrorStore::new(&working_root)),
        Arc::new(ProjectMetadataSyncService::new()),
    );

    service
        .run(&settings, &CancellationToken::new())
        .await
        .unwrap();
    let _ = std::fs::remove_dir_all(&working_root);

    let uploaded = uploads.lock().unwrap().clone();

    // Two unique repositories: an archive + a metadata object each, and nothing for the dropped dup.
    let metadata: Vec<&String> = uploaded
        .iter()
        .filter(|k| k.ends_with("/metadata.json"))
        .collect();
    assert_eq!(metadata.len(), 2, "uploaded: {uploaded:?}");
    assert!(metadata
        .iter()
        .any(|k| *k == "repositories/url/code.neureka.dev/git/backup/metadata.json"));
    assert!(metadata
        .iter()
        .any(|k| *k == "repositories/url/code.neureka.dev/git/website/metadata.json"));

    let archives: Vec<&String> = uploaded
        .iter()
        .filter(|k| k.ends_with("_repo.tar.gz"))
        .collect();
    assert_eq!(archives.len(), 2, "uploaded: {uploaded:?}");
    assert!(archives
        .iter()
        .all(|k| k.starts_with("repositories/url/code.neureka.dev/git/")));
    // The case-variant duplicate produced no extra objects.
    assert_eq!(uploaded.len(), 4, "uploaded: {uploaded:?}");
}
