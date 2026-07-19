//! Storage ← `Services/Storage/`.
//!
//! The `ObjectStorage` trait and its S3-compatible implementation: streaming tar→gzip→multipart with
//! flat memory, single-PUT small objects, per-object delete, and transient-failure retries.

pub mod mime;
pub mod object_store;
pub mod s3;

pub use object_store::ObjectStorage;
pub use s3::S3ObjectStorage;
