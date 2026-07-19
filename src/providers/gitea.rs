//! Shared Gitea-lineage JSON mappers ← `GiteaProviderClientBase`, used by GitHub and Forgejo.
//!
//! Kept off GitLab (whose payloads use different field names) so a GitLab item can never be silently
//! mapped to nothing here. The issue/PR/release mappers land in P7; discovery needs only the repository.

use super::attachments::build_storage_filename;
use super::discovered::{DiscoveredRepository, DiscoveredRepositoryKind};
use super::json;
use super::mappers::map_comment;
use super::models::{
    BackedUpAttachment, BackedUpComment, BackedUpIssue, BackedUpMergeRequest, BackedUpRelease,
};
use serde_json::Value;
use std::collections::HashSet;

pub fn map_gitea_repository(item: &Value, is_starred: bool) -> Option<DiscoveredRepository> {
    let clone_url = json::get_str(item, "clone_url").filter(|v| !v.trim().is_empty())?;
    Some(DiscoveredRepository {
        clone_url: clone_url.to_string(),
        web_url: json::get_str(item, "html_url").map(str::to_string),
        kind: DiscoveredRepositoryKind::Repository,
        identifier: None,
        parent_url: None,
        provider_project_id: None,
        is_starred,
    })
}

/// Gitea-lineage comment (GitHub, Forgejo): author at `user.login`, no system flag.
pub fn map_gitea_comment(item: &Value) -> Option<BackedUpComment> {
    map_comment(item, "user", "login", false)
}

pub fn map_gitea_issue(item: &Value) -> Option<BackedUpIssue> {
    let number = json::get_i64(item, "number")?;
    let title = json::get_str(item, "title").filter(|t| !t.trim().is_empty())?;
    Some(BackedUpIssue {
        number,
        title: title.to_string(),
        state: json::get_str(item, "state").map(str::to_string),
        author: json::get_nested_str(item, "user", "login").map(str::to_string),
        body: json::get_str(item, "body").map(str::to_string),
        created_at: json::get_str(item, "created_at").map(str::to_string),
        updated_at: json::get_str(item, "updated_at").map(str::to_string),
        closed_at: json::get_str(item, "closed_at").map(str::to_string),
        labels: json::get_label_names(item, "labels"),
        web_url: json::get_str(item, "html_url").map(str::to_string),
        comments: Vec::new(),
        attachments: Vec::new(),
    })
}

pub fn map_gitea_pull_request(item: &Value) -> Option<BackedUpMergeRequest> {
    let number = json::get_i64(item, "number")?;
    let title = json::get_str(item, "title").filter(|t| !t.trim().is_empty())?;
    Some(BackedUpMergeRequest {
        number,
        title: title.to_string(),
        state: json::get_str(item, "state").map(str::to_string),
        author: json::get_nested_str(item, "user", "login").map(str::to_string),
        body: json::get_str(item, "body").map(str::to_string),
        source_branch: json::get_nested_str(item, "head", "ref").map(str::to_string),
        target_branch: json::get_nested_str(item, "base", "ref").map(str::to_string),
        created_at: json::get_str(item, "created_at").map(str::to_string),
        updated_at: json::get_str(item, "updated_at").map(str::to_string),
        merged_at: json::get_str(item, "merged_at").map(str::to_string),
        closed_at: json::get_str(item, "closed_at").map(str::to_string),
        labels: json::get_label_names(item, "labels"),
        web_url: json::get_str(item, "html_url").map(str::to_string),
        comments: Vec::new(),
        attachments: Vec::new(),
    })
}

pub fn map_gitea_release(item: &Value) -> Option<BackedUpRelease> {
    let tag = json::get_str(item, "tag_name").filter(|t| !t.trim().is_empty())?;
    Some(BackedUpRelease {
        tag: tag.to_string(),
        name: json::get_str(item, "name").map(str::to_string),
        body: json::get_str(item, "body").map(str::to_string),
        author: json::get_nested_str(item, "author", "login").map(str::to_string),
        draft: Some(json::get_bool(item, "draft")),
        prerelease: Some(json::get_bool(item, "prerelease")),
        created_at: json::get_str(item, "created_at").map(str::to_string),
        published_at: json::get_str(item, "published_at").map(str::to_string),
        commit: None,
        web_url: json::get_str(item, "html_url").map(str::to_string),
        attachments: extract_asset_array(item),
    })
}

/// Downloadable assets from a Gitea-lineage `assets` array, deduped by download URL ← `ExtractAssetArray`.
pub fn extract_asset_array(item: &Value) -> Vec<BackedUpAttachment> {
    let Some(Value::Array(assets)) = item.get("assets") else {
        return Vec::new();
    };
    let mut seen = HashSet::new();
    let mut attachments = Vec::new();
    for asset in assets {
        let (Some(url), Some(name)) = (
            json::get_str(asset, "browser_download_url"),
            json::get_str(asset, "name"),
        ) else {
            continue;
        };
        if url.trim().is_empty() || name.trim().is_empty() || !seen.insert(url.to_string()) {
            continue;
        }
        let mut attachment = BackedUpAttachment::new(
            build_storage_filename(url, name),
            url.to_string(),
            url.to_string(),
        );
        attachment.size_bytes = json::get_i64(asset, "size");
        attachments.push(attachment);
    }
    attachments
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn maps_repository() {
        let item = json!({ "clone_url": "https://h/o/r.git", "html_url": "https://h/o/r" });
        let repo = map_gitea_repository(&item, false).unwrap();
        assert_eq!(repo.clone_url, "https://h/o/r.git");
        assert_eq!(repo.web_url.as_deref(), Some("https://h/o/r"));
        assert!(!repo.is_starred);
        assert!(map_gitea_repository(&json!({}), false).is_none());
    }
}
