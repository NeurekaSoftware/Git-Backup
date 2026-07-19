//! Discovered repository model ← `DiscoveredRepository`.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DiscoveredRepositoryKind {
    Repository,
    Gist,
    Snippet,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DiscoveredRepository {
    pub clone_url: String,
    pub web_url: Option<String>,
    pub kind: DiscoveredRepositoryKind,
    /// Gist/snippet identifier, used to build the storage key for those resources.
    pub identifier: Option<String>,
    /// For a project snippet: the owning project's web URL, used to nest it under that project's
    /// storage prefix. `None` for gists and personal snippets.
    pub parent_url: Option<String>,
    /// Provider-native project id (e.g. GitLab's numeric project id), used to fetch project metadata.
    /// `None` when the provider addresses projects by owner/repo path instead.
    pub provider_project_id: Option<String>,
    /// True when discovered only because it is starred, not owned. Project metadata is never backed up
    /// for starred repositories.
    pub is_starred: bool,
}

impl DiscoveredRepository {
    /// A plain repository entry (the common case) with everything else defaulted.
    pub fn repository(clone_url: String, web_url: Option<String>) -> Self {
        Self {
            clone_url,
            web_url,
            kind: DiscoveredRepositoryKind::Repository,
            identifier: None,
            parent_url: None,
            provider_project_id: None,
            is_starred: false,
        }
    }
}
