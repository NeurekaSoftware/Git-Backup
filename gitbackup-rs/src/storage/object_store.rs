//! The object-storage seam ← `IObjectStorageService`.
//!
//! One production implementation (S3-compatible); modeled as a trait so the sync/retention services
//! take it generically and tests can substitute a double. `async_trait` keeps the futures `Send` and
//! the trait object-safe — the per-call boxing is negligible for these coarse-grained I/O operations.

use anyhow::Result;
use async_trait::async_trait;
use std::path::Path;
use tokio::io::AsyncRead;
use tokio_util::sync::CancellationToken;

#[async_trait]
pub trait ObjectStorage: Send + Sync {
    /// Streams a local directory to `object_key` as tar+gzip with flat memory (no temp archive).
    async fn upload_directory_as_targz(
        &self,
        local_dir: &Path,
        object_key: &str,
        cancel: &CancellationToken,
    ) -> Result<()>;

    /// Uploads a small in-memory document (e.g. a metadata JSON).
    async fn upload_text(
        &self,
        object_key: &str,
        content: &str,
        cancel: &CancellationToken,
    ) -> Result<()>;

    /// Uploads a stream. A known length at or below the single-PUT threshold is sent as one request;
    /// otherwise (or when the length is unknown) it streams via multipart.
    async fn upload_stream(
        &self,
        object_key: &str,
        content: Box<dyn AsyncRead + Send + Unpin>,
        content_type: &str,
        known_length: Option<u64>,
        cancel: &CancellationToken,
    ) -> Result<()>;

    /// Lists every object key under `prefix`.
    async fn list_object_keys(
        &self,
        prefix: &str,
        cancel: &CancellationToken,
    ) -> Result<Vec<String>>;

    /// Deletes the given object keys.
    async fn delete_objects(
        &self,
        object_keys: &[String],
        cancel: &CancellationToken,
    ) -> Result<()>;
}
