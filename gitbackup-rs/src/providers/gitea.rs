//! Shared Gitea-lineage JSON mappers ← `GiteaProviderClientBase`, used by GitHub and Forgejo.
//!
//! Kept off GitLab (whose payloads use different field names) so a GitLab item can never be silently
//! mapped to nothing here. The issue/PR/release mappers land in P7; discovery needs only the repository.

use super::discovered::{DiscoveredRepository, DiscoveredRepositoryKind};
use super::json;
use serde_json::Value;

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
