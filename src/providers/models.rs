//! Project-metadata models ← `ProjectMetadataModels`.
//!
//! Serialized documents are camelCase. Date fields hold the raw provider string (advisory docs, never
//! read back for retention). Attachment `download_url`/`downloadable` are runtime-only (never stored).

use serde::Serialize;
use std::sync::Arc;
use tokio::sync::Semaphore;

/// Everything a metadata client needs to address one project ← `ProjectMetadataContext`.
#[derive(Debug, Clone)]
pub struct ProjectMetadataContext {
    pub clone_url: String,
    pub web_url: Option<String>,
    pub provider_project_id: Option<String>,
    pub base_url: Option<String>,
    pub concurrency: usize,
    /// Shared across the run to cap concurrent attachment downloads. `None` disables throttling.
    pub download_throttle: Option<Arc<Semaphore>>,
}

impl ProjectMetadataContext {
    /// The forge host this repository came from, for the SSRF trusted-host exemption.
    pub fn instance_host(&self) -> Option<String> {
        let reference = self.web_url.as_deref().unwrap_or(&self.clone_url);
        url::Url::parse(reference)
            .ok()
            .and_then(|u| u.host_str().map(str::to_string))
    }
}

/// A file attached to an issue/MR body or comment ← `BackedUpAttachment`.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BackedUpAttachment {
    pub file_name: String,
    /// The reference as it appears in source text, e.g. `/uploads/{sha}/{filename}`.
    pub original_path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub storage_key: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub size_bytes: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub content_type: Option<String>,

    /// Resolved fetch URL — never serialized.
    #[serde(skip)]
    pub download_url: String,
    /// When false, the reference is recorded but never fetched (e.g. an external release link, so the
    /// credential is not sent to a third party). Never serialized.
    #[serde(skip)]
    pub downloadable: bool,
}

impl BackedUpAttachment {
    pub fn new(file_name: String, original_path: String, download_url: String) -> Self {
        Self {
            file_name,
            original_path,
            storage_key: None,
            size_bytes: None,
            content_type: None,
            download_url,
            downloadable: true,
        }
    }
}

/// Trait over the three collections whose attachments the orchestrator downloads.
pub trait ArtifactItem {
    fn attachments(&self) -> &[BackedUpAttachment];
    fn attachments_mut(&mut self) -> &mut Vec<BackedUpAttachment>;
    fn set_attachments(&mut self, attachments: Vec<BackedUpAttachment>);
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BackedUpComment {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub id: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub author: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub body: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub created_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub updated_at: Option<String>,
    pub system: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BackedUpIssue {
    pub number: i64,
    pub title: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub state: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub author: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub body: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub created_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub updated_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub closed_at: Option<String>,
    pub labels: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub web_url: Option<String>,
    pub comments: Vec<BackedUpComment>,
    pub attachments: Vec<BackedUpAttachment>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BackedUpMergeRequest {
    pub number: i64,
    pub title: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub state: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub author: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub body: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_branch: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target_branch: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub created_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub updated_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub merged_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub closed_at: Option<String>,
    pub labels: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub web_url: Option<String>,
    pub comments: Vec<BackedUpComment>,
    pub attachments: Vec<BackedUpAttachment>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BackedUpRelease {
    pub tag: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub body: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub author: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub draft: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub prerelease: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub created_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub published_at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub commit: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub web_url: Option<String>,
    pub attachments: Vec<BackedUpAttachment>,
}

macro_rules! impl_artifact_item {
    ($ty:ty) => {
        impl ArtifactItem for $ty {
            fn attachments(&self) -> &[BackedUpAttachment] {
                &self.attachments
            }
            fn attachments_mut(&mut self) -> &mut Vec<BackedUpAttachment> {
                &mut self.attachments
            }
            fn set_attachments(&mut self, attachments: Vec<BackedUpAttachment>) {
                self.attachments = attachments;
            }
        }
    };
}
impl_artifact_item!(BackedUpIssue);
impl_artifact_item!(BackedUpMergeRequest);
impl_artifact_item!(BackedUpRelease);

/// One row in an issue/MR `index.json` manifest ← `CollectionManifestEntry`.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CollectionManifestEntry {
    pub number: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub title: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub state: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub updated_at: Option<String>,
}

/// One row in a release `index.json` manifest ← `ReleaseManifestEntry`.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReleaseManifestEntry {
    pub tag: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub published_at: Option<String>,
}
