//! Git ← `Services/Git/`.
//!
//! `git`/`git-lfs` CLI wrapper: bare `--mirror` clones, incremental fetch, self-heal, transport
//! allowlist, origin-scoped credential env injection, and process-tree kill on cancel.

pub mod credential;
pub mod error;
pub mod service;

pub use credential::GitCredential;
pub use error::GitError;
pub use service::{GitCliRepositoryService, GitRepository};
