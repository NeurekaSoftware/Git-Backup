//! Repositories ← `Services/Repositories/`.
//!
//! Sync orchestration (provider/url dispatch, bounded concurrency, snapshot upload), metadata sync
//! (owned-only, partial-fetch-never-deletes), retention, and the local mirror store.

pub mod documents;
pub mod local_mirror;
pub mod metadata_sync;
pub mod retention;
pub mod sync;

pub use local_mirror::LocalMirrorStore;
pub use metadata_sync::ProjectMetadataSyncService;
pub use retention::RepositoryRetentionService;
pub use sync::RepositorySyncService;
