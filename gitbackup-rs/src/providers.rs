//! Providers ← `Services/Providers/`.
//!
//! Forge REST clients (GitHub/GitLab/Forgejo) over a shared HTTP base with retry/`Retry-After` and
//! pagination. P5 implements discovery; metadata listing and SSRF-guarded attachment download land in P7.

pub mod client;
pub mod discovered;
pub mod forgejo;
pub mod gitea;
pub mod github;
pub mod gitlab;
pub mod http_base;
pub mod json;

pub use client::{RepositoryProviderClient, RepositoryProviderClientFactory};
pub use discovered::{DiscoveredRepository, DiscoveredRepositoryKind};
