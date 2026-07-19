//! Storage-key builder ← `StorageKeyBuilder`.
//!
//! The byte-for-byte contract with existing production buckets: every prefix, object name, and
//! separator is reproduced exactly so a Rust-written key addresses the same object the .NET app wrote.
//! Retention scans by the two roots and the builders write them, so each root is one constant.

use super::git_url;
use super::repo_path::RepositoryPathInfo;

pub const ARCHIVE_OBJECT_NAME_SUFFIX: &str = "_repo.tar.gz";
pub const REPOSITORY_METADATA_OBJECT_NAME: &str = "metadata.json";

pub const REPOSITORIES_ROOT: &str = "repositories";
pub const SNIPPETS_ROOT: &str = "snippets";
pub const REPOSITORIES_PREFIX: &str = "repositories/";
pub const SNIPPETS_PREFIX: &str = "snippets/";

pub const ISSUES_COLLECTION_SEGMENT: &str = "issues";
pub const MERGE_REQUESTS_COLLECTION_SEGMENT: &str = "merge-requests";
pub const RELEASES_COLLECTION_SEGMENT: &str = "releases";
pub const ATTACHMENTS_COLLECTION_SEGMENT: &str = "attachments";
pub const COLLECTION_MANIFEST_OBJECT_NAME: &str = "index.json";

fn trim_slashes(value: &str) -> &str {
    value.trim_matches('/')
}

fn sanitize_identifier(identifier: &str) -> String {
    // A snippet/gist id comes straight from the provider's JSON; the shared normalizer stops a hostile
    // forge from steering objects under a different prefix via a '/' or '..' in the id.
    git_url::normalize_storage_segment(identifier, "unknown", false)
}

pub fn build_provider_repository_prefix(provider: &str, repository: &RepositoryPathInfo) -> String {
    let mut segments = vec![
        REPOSITORIES_ROOT.to_string(),
        "provider".to_string(),
        provider.trim().to_lowercase(),
    ];
    segments.extend(repository.hierarchy());
    segments.join("/")
}

/// `snippets/provider/{provider}/{identifier}` for a standalone gist or personal snippet.
pub fn build_snippet_resource_prefix(provider: &str, identifier: &str) -> String {
    [
        SNIPPETS_ROOT,
        "provider",
        &provider.trim().to_lowercase(),
        &sanitize_identifier(identifier),
    ]
    .join("/")
}

/// `{repositoryPrefix}/snippets/{identifier}` for a project snippet nested under its repository.
pub fn build_nested_snippet_prefix(repository_prefix: &str, identifier: &str) -> String {
    format!(
        "{}/snippets/{}",
        trim_slashes(repository_prefix),
        sanitize_identifier(identifier)
    )
}

pub fn build_url_repository_prefix(repository: &RepositoryPathInfo) -> String {
    let mut segments = vec![
        REPOSITORIES_ROOT.to_string(),
        "url".to_string(),
        repository.full_domain.clone(),
    ];
    segments.extend(repository.hierarchy());
    segments.join("/")
}

pub fn build_archive_object_key(repository_prefix: &str, timestamp_unix_seconds: i64) -> String {
    format!(
        "{}/{timestamp_unix_seconds}{ARCHIVE_OBJECT_NAME_SUFFIX}",
        trim_slashes(repository_prefix)
    )
}

pub fn build_repository_metadata_object_key(repository_prefix: &str) -> String {
    format!(
        "{}/{REPOSITORY_METADATA_OBJECT_NAME}",
        trim_slashes(repository_prefix)
    )
}

// Issues, merge requests, and releases are stored as latest-state JSON documents under their
// repository prefix: {repositoryPrefix}/{collection}/{identifier}.json, each with an index.json
// manifest and an attachments/{identifier}/ folder.

pub fn build_issues_collection_prefix(repository_prefix: &str) -> String {
    build_collection_prefix(repository_prefix, ISSUES_COLLECTION_SEGMENT)
}

pub fn build_issue_object_key(repository_prefix: &str, identifier: &str) -> String {
    build_document_object_key(
        &build_issues_collection_prefix(repository_prefix),
        identifier,
    )
}

pub fn build_issues_manifest_object_key(repository_prefix: &str) -> String {
    build_manifest_object_key(&build_issues_collection_prefix(repository_prefix))
}

pub fn build_issue_attachment_object_key(
    repository_prefix: &str,
    identifier: &str,
    file_name: &str,
) -> String {
    build_attachment_object_key(
        &build_issues_collection_prefix(repository_prefix),
        identifier,
        file_name,
    )
}

pub fn build_merge_requests_collection_prefix(repository_prefix: &str) -> String {
    build_collection_prefix(repository_prefix, MERGE_REQUESTS_COLLECTION_SEGMENT)
}

pub fn build_merge_request_object_key(repository_prefix: &str, identifier: &str) -> String {
    build_document_object_key(
        &build_merge_requests_collection_prefix(repository_prefix),
        identifier,
    )
}

pub fn build_merge_requests_manifest_object_key(repository_prefix: &str) -> String {
    build_manifest_object_key(&build_merge_requests_collection_prefix(repository_prefix))
}

pub fn build_merge_request_attachment_object_key(
    repository_prefix: &str,
    identifier: &str,
    file_name: &str,
) -> String {
    build_attachment_object_key(
        &build_merge_requests_collection_prefix(repository_prefix),
        identifier,
        file_name,
    )
}

pub fn build_releases_collection_prefix(repository_prefix: &str) -> String {
    build_collection_prefix(repository_prefix, RELEASES_COLLECTION_SEGMENT)
}

pub fn build_release_object_key(repository_prefix: &str, identifier: &str) -> String {
    build_document_object_key(
        &build_releases_collection_prefix(repository_prefix),
        identifier,
    )
}

pub fn build_releases_manifest_object_key(repository_prefix: &str) -> String {
    build_manifest_object_key(&build_releases_collection_prefix(repository_prefix))
}

pub fn build_release_attachment_object_key(
    repository_prefix: &str,
    identifier: &str,
    file_name: &str,
) -> String {
    build_attachment_object_key(
        &build_releases_collection_prefix(repository_prefix),
        identifier,
        file_name,
    )
}

fn build_collection_prefix(repository_prefix: &str, collection_segment: &str) -> String {
    format!("{}/{collection_segment}", trim_slashes(repository_prefix))
}

fn build_document_object_key(collection_prefix: &str, identifier: &str) -> String {
    format!("{collection_prefix}/{identifier}.json")
}

fn build_manifest_object_key(collection_prefix: &str) -> String {
    format!("{collection_prefix}/{COLLECTION_MANIFEST_OBJECT_NAME}")
}

fn build_attachment_object_key(
    collection_prefix: &str,
    identifier: &str,
    file_name: &str,
) -> String {
    format!("{collection_prefix}/{ATTACHMENTS_COLLECTION_SEGMENT}/{identifier}/{file_name}")
}

/// Parses the snapshot timestamp encoded in an archive key (`{prefix}/{unixSeconds}_repo.tar.gz`).
/// Returns `None` for a non-archive key or a non-positive timestamp.
pub fn try_get_archive_timestamp(object_key: &str) -> Option<i64> {
    let leaf_with_suffix = object_key.strip_suffix(ARCHIVE_OBJECT_NAME_SUFFIX)?;
    let timestamp_text = match leaf_with_suffix.rfind('/') {
        Some(slash) => &leaf_with_suffix[slash + 1..],
        None => leaf_with_suffix,
    };
    match timestamp_text.parse::<i64>() {
        Ok(value) if value > 0 => Some(value),
        _ => None,
    }
}

/// The parent prefix of an object key (everything before the last '/'); for an archive or metadata
/// object this is its repository prefix.
pub fn get_parent_prefix(object_key: &str) -> &str {
    match object_key.rfind('/') {
        Some(0) | None => "",
        Some(slash) => &object_key[..slash],
    }
}

/// Normalizes a key or prefix to a trailing-slash prefix (empty stays empty).
pub fn ensure_prefix(key_or_prefix: &str) -> String {
    let value = trim_slashes(key_or_prefix);
    if value.is_empty() {
        String::new()
    } else {
        format!("{value}/")
    }
}
