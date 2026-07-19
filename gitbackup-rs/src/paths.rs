//! Paths ← `Services/Paths/`.
//!
//! Storage-key builder (the backward-compat contract with existing buckets), repository-path parser,
//! and git-URL normalization/sanitization. The full key builder + path parser land in phase P2; the
//! `git_url` helpers are brought forward here because config validation depends on them.

pub mod git_url;
