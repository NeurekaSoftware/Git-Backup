# Development and Release Rules
- Run `dotnet build GitBackup.slnx` and confirm a successful build before every commit.
- Use Conventional Commits for all commit messages (for example: `feat: ...`, `fix: ...`, `chore: ...`).
- Use Semantic Versioning (`MAJOR.MINOR.PATCH`) for all versioning and release tags.

# Logging Standards
- Write log messages in plain language so users can immediately understand what happened.
- Keep log volume intentional: log key workflow steps at `info`, add deeper diagnostics at `debug`, and record failures with `error` plus useful context.
- Do not emit repetitive or duplicate log lines for the same event.
- Keep logs visually consistent and easy to scan, including clean formatting for timestamps and message structure.

# Object Storage API Cost Guardrails

## Goal
- Optimize API calls for the lowest total request cost across supported providers.
- Prices below exclude free-tier usage allowances.

## Cost-Avoidance Rules In This Codebase

- Minimize high-cost operations across providers by default (`R2 Class A`, `B2 Class C`).
- Prefer `HeadObject` hash checks before archive uploads; skip `PutObject` when the archive hash is unchanged.
- Use index objects (`indexes/...`) with `GetObject`/`HeadObject` for backup, mirror, retention, and prune discovery.
- Avoid routine `ListObjects`/`ListObjectsV2`/`ListBuckets`; allow list operations only for explicit cleanup flows that require enumeration.
- Skip marker writes when repository/archive content is unchanged.
- Update index and registry documents only when their content changed.
- Avoid runtime bucket configuration and ACL API calls (`PutBucket*`, `GetBucket*`, `HeadBucket`, `GetObjectAcl`, `PutObjectAcl`) in backup, mirror, and retention paths.
- Avoid copy and multipart APIs (`CopyObject`, `CreateMultipartUpload`, `UploadPart`, `UploadPartCopy`, `ListParts`, `CompleteMultipartUpload`, `ListMultipartUploads`) unless a feature explicitly requires them.
- Prefer delete operations for cleanup paths where possible (`DeleteObject`, `DeleteObjects`, `DeleteBucket`, `AbortMultipartUpload`) because they are low-cost/free in the listed pricing models.

## Cloudflare R2
> [!TIP]
> Last Updated `2026-02-17`

### Cost Classes
- Class A ($4.50 / million requests): `ListBuckets`, `PutBucket`, `ListObjects`, `PutObject`, `CopyObject`, `CompleteMultipartUpload`, `CreateMultipartUpload`, `LifecycleStorageTierTransition`, `ListMultipartUploads`, `UploadPart`, `UploadPartCopy`, `ListParts`, `PutBucketEncryption`, `PutBucketCors`, `PutBucketLifecycleConfiguration`.
- Class B ($0.36 / million requests): `HeadBucket`, `HeadObject`, `GetObject`, `UsageSummary`, `GetBucketEncryption`, `GetBucketLocation`, `GetBucketCors`, `GetBucketLifecycleConfiguration`.
- Free operations: `DeleteObject`, `DeleteBucket`, `AbortMultipartUpload`.

## Backblaze B2
> [!TIP]
> Last Updated `2026-02-17`

### Cost Classes
- Transactions Class A (Free): `AbortMultipartUpload`, `CreateMultipartUpload`, `CompleteMultipartUpload`, `DeleteBucket`, `DeleteObject`, `DeleteObjects`, `PutObject`, `PutObjectLegalHold`, `PutObjectLockConfiguration`, `PutObjectRetention`, `UploadPart`.
- Transactions Class B ($0.004 per 10,000): `GetObject`, `GetObjectLegalHold`, `GetObjectLockConfiguration`, `GetObjectRetention`, `HeadObject`.
- Transactions Class C ($0.004 per 1,000): `CopyObject (Put Object Copy)`, `CreateBucket`, `DeleteBucketCors`, `DeleteBucketEncryption`, `GetBucketAcl (List Objects)`, `GetBucketCors`, `GetBucketEncryption`, `GetBucketLocation`, `GetBucketVersioning`, `GetObjectAcl`, `HeadBucket`, `ListBuckets`, `ListMultipartUploads`, `ListObjectsV2`, `ListObjectVersions`, `ListParts`, `PutBucketAcl`, `PutBucketCors`, `PutBucketEncryption`, `PutObjectAcl`, `UploadPartCopy`.
