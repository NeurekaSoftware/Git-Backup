//! Repositories ← `Services/Repositories/`.
//!
//! Sync orchestration (provider/url dispatch, bounded concurrency, snapshot upload), metadata sync
//! (owned-only, partial-fetch-never-deletes), retention, and the local mirror store. Ported P6–P8.
