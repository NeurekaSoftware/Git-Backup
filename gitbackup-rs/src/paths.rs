//! Paths ← `Services/Paths/`.
//!
//! Storage-key builder (the backward-compat contract with existing buckets), repository-path parser,
//! and git-URL normalization/sanitization.

pub mod git_url;
pub mod key_builder;
pub mod repo_path;

pub use repo_path::RepositoryPathInfo;
