//! S3-compatible object storage ← `SimpleS3ObjectStorageService`, on the `rust-s3` client.
//!
//! Targets R2/Backblaze/MinIO. Streams the bare mirror as tar→gzip→multipart with flat memory (no
//! temp archive), sends small payloads as a single PUT, lists via the paginating client, and deletes
//! per object. A transient status (429/5xx) or network error is retried with capped backoff, and any
//! non-2xx is turned into a failure so a bad request is never mistaken for a stored object.
//!
//! `rust-s3` limitations vs the original, accepted for this port:
//! - `payloadSignatureMode` is parsed and honored only as `full` (the client's default); `streaming`
//!   and `unsigned` log a warning and behave as `full` until the client exposes them.
//! - Metadata JSON is stored uncompressed (the client cannot set a per-request `Content-Encoding`);
//!   the object key and readable JSON are unchanged, only the stored bytes differ.
//! - Bulk delete is issued one object at a time (the client has no multi-object delete).

use super::object_store::ObjectStorage;
use crate::config::enums::payload_signature;
use crate::config::StorageConfig;
use crate::retry;
use anyhow::{anyhow, bail, Context, Result};
use async_trait::async_trait;
use flate2::write::GzEncoder;
use flate2::Compression;
use s3::creds::Credentials;
use s3::{Bucket, Region};
use std::future::Future;
use std::path::{Path, PathBuf};
use std::time::Duration;
use tokio::io::{AsyncRead, AsyncReadExt};
use tokio_util::io::SyncIoBridge;
use tokio_util::sync::CancellationToken;

const JSON_CONTENT_TYPE: &str = "application/json";
const SINGLE_PUT_MAX_BYTES: u64 = 5 * 1024 * 1024;
const MAX_ATTEMPTS: u32 = 5;
const MAX_BACKOFF: Duration = Duration::from_secs(30);
const DUPLEX_BUFFER_BYTES: usize = 256 * 1024;

pub struct S3ObjectStorage {
    bucket: Box<Bucket>,
}

impl S3ObjectStorage {
    pub fn new(storage: &StorageConfig) -> Result<Self> {
        let bucket_name = require(storage.bucket.as_deref(), "storage.bucket")?;
        let endpoint = require(storage.endpoint.as_deref(), "storage.endpoint")?;
        let region_name = require(storage.region.as_deref(), "storage.region")?;
        let access_key_id = require(storage.access_key_id.as_deref(), "storage.accessKeyId")?;
        let secret_access_key = require(
            storage.secret_access_key.as_deref(),
            "storage.secretAccessKey",
        )?;

        let mode = payload_signature::normalize(storage.payload_signature_mode.as_deref());
        if mode != payload_signature::FULL {
            tracing::warn!(
                "payloadSignatureMode={mode} is not yet supported by the storage client; using full-signature. mode={mode}."
            );
        }

        let region = Region::Custom {
            region: region_name.to_string(),
            endpoint: endpoint.trim_end_matches('/').to_string(),
        };
        let credentials = Credentials::new(
            Some(access_key_id),
            Some(secret_access_key),
            None,
            None,
            None,
        )
        .context("storage: invalid credentials")?;

        let mut bucket = Bucket::new(bucket_name, region, credentials)
            .context("storage: failed to initialize S3 client")?;
        if storage.force_path_style == Some(true) {
            bucket = bucket.with_path_style();
        }

        tracing::debug!(
            "Object storage client initialized. endpoint={endpoint}, region={region_name}, bucket={bucket_name}, forcePathStyle={}, payloadSignatureMode={mode}.",
            storage.force_path_style == Some(true)
        );

        Ok(Self { bucket })
    }

    /// Runs a buffered S3 operation under one uniform failure model: retry transient status codes and
    /// network errors with capped backoff, and treat any other non-2xx as a failure.
    async fn run_with_retry<F, Fut>(
        &self,
        operation: &str,
        object_key: &str,
        cancel: &CancellationToken,
        make: F,
    ) -> Result<s3::request::ResponseData>
    where
        F: Fn() -> Fut,
        Fut: Future<Output = Result<s3::request::ResponseData, s3::error::S3Error>>,
    {
        for attempt in 1..=MAX_ATTEMPTS {
            let outcome = tokio::select! {
                _ = cancel.cancelled() => bail!("storage: {operation} '{object_key}' cancelled"),
                outcome = make() => outcome,
            };

            match outcome {
                Ok(response) => {
                    let status = response.status_code();
                    if (200..=299).contains(&status) || status == 304 {
                        return Ok(response);
                    }
                    if is_retryable_status(status) && attempt < MAX_ATTEMPTS {
                        let delay = retry::resolve(None, attempt, MAX_BACKOFF, status != 429, true);
                        tracing::warn!(
                            "Storage is temporarily unavailable (HTTP {status}). Backing off before retry. operation={operation}, objectKey={object_key}, attempt={attempt}, delaySeconds={:.1}.",
                            delay.as_secs_f64()
                        );
                        sleep_or_cancel(delay, cancel).await?;
                        continue;
                    }
                    let body = String::from_utf8_lossy(response.bytes());
                    return Err(anyhow!(
                        "storage: failed to {operation} '{object_key}'. statusCode={status}, detail={body}"
                    ));
                }
                Err(error) if attempt < MAX_ATTEMPTS => {
                    let delay = retry::resolve(None, attempt, MAX_BACKOFF, true, true);
                    tracing::warn!(
                        "Storage request failed; retrying. operation={operation}, objectKey={object_key}, attempt={attempt}, error={error}."
                    );
                    sleep_or_cancel(delay, cancel).await?;
                    continue;
                }
                Err(error) => {
                    return Err(anyhow::Error::new(error)
                        .context(format!("storage: failed to {operation} '{object_key}'")));
                }
            }
        }
        unreachable!("loop returns on the final attempt")
    }
}

#[async_trait]
impl ObjectStorage for S3ObjectStorage {
    async fn upload_directory_as_targz(
        &self,
        local_dir: &Path,
        object_key: &str,
        cancel: &CancellationToken,
    ) -> Result<()> {
        if !local_dir.is_dir() {
            bail!(
                "storage: directory '{}' does not exist",
                local_dir.display()
            );
        }
        let key = normalize_object_key(object_key)?;
        tracing::debug!(
            "Streaming archive upload. localDirectory={}, objectKey={key}.",
            local_dir.display()
        );

        // tar -> gzip runs on a blocking thread, feeding a bounded duplex pipe; the multipart uploader
        // drains the read half. The bounded pipe backpressures the producer, so peak memory is the pipe
        // buffer plus the client's in-flight part — flat, independent of repository size.
        let (mut reader, producer) = spawn_targz_reader(local_dir.to_path_buf());

        let upload = async {
            let response = tokio::select! {
                _ = cancel.cancelled() => bail!("storage: upload archive '{key}' cancelled"),
                response = self.bucket.put_object_stream(&mut reader, &key) => response,
            };
            let status = response
                .context("storage: failed to upload archive")?
                .status_code();
            if !(200..=299).contains(&status) {
                bail!("storage: failed to upload archive '{key}'. statusCode={status}");
            }
            Ok(())
        }
        .await;

        // Surface a producer (tar/gzip) failure, but the upload error is primary.
        let produced = producer
            .await
            .context("storage: archive producer panicked")?;
        upload?;
        produced?;

        tracing::info!("Archive uploaded. objectKey={key}.");
        Ok(())
    }

    async fn upload_text(
        &self,
        object_key: &str,
        content: &str,
        cancel: &CancellationToken,
    ) -> Result<()> {
        let key = normalize_object_key(object_key)?;
        tracing::debug!("Uploading object. objectKey={key}, contentType={JSON_CONTENT_TYPE}.");
        let body = content.as_bytes();
        self.run_with_retry("upload object", &key, cancel, || {
            self.bucket
                .put_object_with_content_type(&key, body, JSON_CONTENT_TYPE)
        })
        .await
        .map(|_| ())
    }

    async fn upload_stream(
        &self,
        object_key: &str,
        mut content: Box<dyn AsyncRead + Send + Unpin>,
        content_type: &str,
        known_length: Option<u64>,
        cancel: &CancellationToken,
    ) -> Result<()> {
        let key = normalize_object_key(object_key)?;
        let content_type = if content_type.trim().is_empty() {
            super::mime::DEFAULT_CONTENT_TYPE
        } else {
            content_type
        };

        // Small, known-length payloads (most attachments) go as a single PUT; larger or unknown-length
        // ones stream via multipart so they are never buffered whole.
        if matches!(known_length, Some(length) if length <= SINGLE_PUT_MAX_BYTES) {
            let mut buffer = Vec::with_capacity(known_length.unwrap_or(0) as usize);
            content
                .read_to_end(&mut buffer)
                .await
                .context("storage: failed to read attachment")?;
            tracing::debug!(
                "Uploading object. objectKey={key}, contentType={content_type}, bytes={}.",
                buffer.len()
            );
            return self
                .run_with_retry("upload object", &key, cancel, || {
                    self.bucket
                        .put_object_with_content_type(&key, &buffer, content_type)
                })
                .await
                .map(|_| ());
        }

        tracing::debug!("Streaming object upload. objectKey={key}.");
        let status = tokio::select! {
            _ = cancel.cancelled() => bail!("storage: upload object '{key}' cancelled"),
            response = self.bucket.put_object_stream(&mut content, &key) => {
                response.context("storage: failed to stream object")?.status_code()
            }
        };
        if !(200..=299).contains(&status) {
            bail!("storage: failed to upload object '{key}'. statusCode={status}");
        }
        Ok(())
    }

    async fn list_object_keys(
        &self,
        prefix: &str,
        cancel: &CancellationToken,
    ) -> Result<Vec<String>> {
        let normalized = crate::paths::key_builder::ensure_prefix(prefix);
        tracing::debug!("Listing object keys. prefix={normalized}.");

        let results = tokio::select! {
            _ = cancel.cancelled() => bail!("storage: list objects '{normalized}' cancelled"),
            results = self.bucket.list(normalized.clone(), None) => {
                results.with_context(|| format!("storage: failed to list objects '{normalized}'"))?
            }
        };

        let mut keys = Vec::new();
        for page in results {
            for object in page.contents {
                if !object.key.trim().is_empty() {
                    keys.push(object.key);
                }
            }
        }
        tracing::debug!(
            "Object key listing completed. prefix={normalized}, keyCount={}.",
            keys.len()
        );
        Ok(keys)
    }

    async fn delete_objects(
        &self,
        object_keys: &[String],
        cancel: &CancellationToken,
    ) -> Result<()> {
        // Deduplicate and strip framing slashes, matching the original's key hygiene.
        let mut keys: Vec<String> = object_keys
            .iter()
            .map(|key| key.trim_matches('/').to_string())
            .filter(|key| !key.is_empty())
            .collect();
        keys.sort();
        keys.dedup();

        if keys.is_empty() {
            tracing::debug!("No object deletions needed.");
            return Ok(());
        }

        tracing::debug!("Deleting objects. count={}.", keys.len());
        for key in &keys {
            self.run_with_retry("delete object", key, cancel, || {
                self.bucket.delete_object(key)
            })
            .await?;
        }
        Ok(())
    }
}

/// Spawns the tar→gzip producer on a blocking thread and returns the read half of the bounded pipe.
fn spawn_targz_reader(
    local_dir: PathBuf,
) -> (tokio::io::DuplexStream, tokio::task::JoinHandle<Result<()>>) {
    let (reader, writer) = tokio::io::duplex(DUPLEX_BUFFER_BYTES);
    let handle = tokio::task::spawn_blocking(move || -> Result<()> {
        // A bare mirror is mostly packfile data git already compressed, so fast (level 1) deflate spends
        // far less CPU for near-identical size — and this stream throttles the upload to the compression
        // rate anyway.
        let sync_writer = SyncIoBridge::new(writer);
        let encoder = GzEncoder::new(sync_writer, Compression::fast());
        let mut builder = tar::Builder::new(encoder);
        builder
            .append_dir_all(".", &local_dir)
            .context("archive: failed to add directory")?;
        let encoder = builder
            .into_inner()
            .context("archive: failed to finish tar")?;
        encoder.finish().context("archive: failed to finish gzip")?;
        Ok(())
    });
    (reader, handle)
}

async fn sleep_or_cancel(delay: Duration, cancel: &CancellationToken) -> Result<()> {
    tokio::select! {
        _ = cancel.cancelled() => bail!("cancelled"),
        _ = tokio::time::sleep(delay) => Ok(()),
    }
}

fn is_retryable_status(status: u16) -> bool {
    matches!(status, 429 | 500 | 502 | 503 | 504)
}

fn require<'a>(value: Option<&'a str>, name: &str) -> Result<&'a str> {
    match value.map(str::trim).filter(|v| !v.is_empty()) {
        Some(value) => Ok(value),
        None => bail!("Storage configuration '{name}' is required."),
    }
}

/// Trims framing slashes and rejects an empty key — shared by every upload path so the bucket's only
/// writers cannot disagree about what a valid key is.
fn normalize_object_key(object_key: &str) -> Result<String> {
    let normalized = object_key.trim_matches('/');
    if normalized.is_empty() {
        bail!("Object key is required.");
    }
    Ok(normalized.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Read;

    #[test]
    fn normalize_object_key_trims_and_rejects_empty() {
        assert_eq!(normalize_object_key("/a/b/").unwrap(), "a/b");
        assert!(normalize_object_key("").is_err());
        assert!(normalize_object_key("///").is_err());
    }

    #[test]
    fn is_retryable_status_matches_transient_codes() {
        for status in [429, 500, 502, 503, 504] {
            assert!(is_retryable_status(status), "{status} should be retryable");
        }
        for status in [200, 301, 400, 401, 403, 404, 501] {
            assert!(
                !is_retryable_status(status),
                "{status} should not be retryable"
            );
        }
    }

    #[tokio::test]
    async fn targz_reader_round_trips_directory_tree() {
        // Verifies the memory-critical pipeline end to end (tar→gzip→bounded pipe) without a bucket: the
        // produced stream must gunzip+untar back to the exact file tree.
        let dir = std::env::temp_dir().join(format!("gitbackup-tar-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(dir.join("sub")).unwrap();
        std::fs::write(dir.join("a.txt"), b"alpha").unwrap();
        std::fs::write(dir.join("sub/b.txt"), b"bravo").unwrap();

        let (mut reader, producer) = spawn_targz_reader(dir.clone());
        let mut gz_bytes = Vec::new();
        reader.read_to_end(&mut gz_bytes).await.unwrap();
        producer.await.unwrap().unwrap();

        let mut tar_bytes = Vec::new();
        flate2::read::GzDecoder::new(&gz_bytes[..])
            .read_to_end(&mut tar_bytes)
            .unwrap();

        let mut archive = tar::Archive::new(&tar_bytes[..]);
        let mut found = std::collections::BTreeMap::new();
        for entry in archive.entries().unwrap() {
            let mut entry = entry.unwrap();
            if !entry.header().entry_type().is_file() {
                continue;
            }
            let path = entry
                .path()
                .unwrap()
                .to_string_lossy()
                .replace('\\', "/")
                .trim_start_matches("./")
                .to_string();
            let mut content = String::new();
            entry.read_to_string(&mut content).unwrap();
            found.insert(path, content);
        }

        let _ = std::fs::remove_dir_all(&dir);
        assert_eq!(found.get("a.txt").map(String::as_str), Some("alpha"));
        assert_eq!(found.get("sub/b.txt").map(String::as_str), Some("bravo"));
    }
}
