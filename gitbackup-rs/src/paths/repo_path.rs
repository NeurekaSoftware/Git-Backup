//! Repository-path parsing ← `RepositoryPathParser` / `RepositoryPathInfo`.
//!
//! Turns a repository URL into the normalized owner/group/secondary-group/repo hierarchy that the
//! storage-key builder nests objects under. Every segment is lowercased and sanitized through the same
//! choke point so a forge-controlled path can never inject extra key segments.

use super::git_url;

/// The normalized hierarchy pieces of a repository URL ← `RepositoryPathInfo`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RepositoryPathInfo {
    pub full_domain: String,
    pub owner: String,
    pub group: Option<String>,
    pub secondary_group: Option<String>,
    pub repository_name: String,
}

impl RepositoryPathInfo {
    /// The ordered storage-key hierarchy: owner, [group], [secondary group], repository.
    pub fn hierarchy(&self) -> Vec<String> {
        let mut hierarchy = Vec::with_capacity(4);
        hierarchy.push(self.owner.clone());
        if let Some(group) = self.group.as_ref().filter(|g| !g.trim().is_empty()) {
            hierarchy.push(group.clone());
        }
        if let Some(secondary) = self
            .secondary_group
            .as_ref()
            .filter(|g| !g.trim().is_empty())
        {
            hierarchy.push(secondary.clone());
        }
        hierarchy.push(self.repository_name.clone());
        hierarchy
    }
}

/// Parses a repository URL into its normalized hierarchy ← `RepositoryPathParser.Parse`. Errors carry
/// the original messages.
pub fn parse(repository_url: &str) -> Result<RepositoryPathInfo, String> {
    let uri = url::Url::parse(repository_url)
        .map_err(|_| format!("Invalid repository URL '{repository_url}'."))?;

    if !git_url::is_http_or_https(&uri) {
        return Err(format!(
            "Unsupported repository URL scheme in '{repository_url}'. Only http and https are supported."
        ));
    }

    let path_segments = git_url::split_unescaped_segments(&uri);
    if path_segments.len() < 2 {
        return Err(format!(
            "Repository URL '{repository_url}' does not contain owner and repository segments."
        ));
    }

    let owner = normalize_segment(&path_segments[0]);
    let repository_name = normalize_segment(git_url::trim_git_suffix(
        &path_segments[path_segments.len() - 1],
    ));

    // Segments between the owner and the repository name form the group hierarchy: the first is the
    // group, the rest are joined with '-' into a single secondary-group segment.
    let group_segments = &path_segments[1..path_segments.len() - 1];
    let group = group_segments.first().map(|g| normalize_segment(g));
    let secondary_group = if group_segments.len() > 1 {
        Some(normalize_segment(&group_segments[1..].join("-")))
    } else {
        None
    };

    let full_domain = normalize_segment(uri.host_str().unwrap_or_default());

    Ok(RepositoryPathInfo {
        full_domain,
        owner,
        group,
        secondary_group,
        repository_name,
    })
}

fn normalize_segment(value: &str) -> String {
    git_url::normalize_storage_segment(value, "unknown", true)
}
