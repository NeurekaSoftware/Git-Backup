//! Stored metadata documents ← `StorageMetadataDocuments`.
//!
//! JSON is camelCase and pretty-printed. These documents are advisory (never read back for retention),
//! so their exact bytes are not a compatibility contract — only the object keys are.

use anyhow::{Context, Result};
use serde::Serialize;

/// Serializes a document as pretty camelCase JSON.
pub fn serialize<T: Serialize>(document: &T) -> Result<String> {
    serde_json::to_string_pretty(document).context("failed to serialize metadata document")
}

/// Advisory metadata written alongside a repository's snapshots ← `RepositoryMetadataDocument`. Records
/// only what cannot be derived from the object keys (source URL and mode).
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RepositoryMetadataDocument {
    pub mode: String,
    pub repository_url: String,
    pub updated_at_unix_seconds: i64,
}
