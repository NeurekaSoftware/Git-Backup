//! Storage ← `Services/Storage/`.
//!
//! The `ObjectStorage` trait and its S3-compatible implementation: streaming tar→gzip→multipart with
//! flat memory, single-PUT small objects, batch delete with per-object fallback. Ported in phase P3.
