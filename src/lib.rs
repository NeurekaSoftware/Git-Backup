//! GitBackup — Rust rewrite of the .NET 10 backup daemon.
//!
//! Parity-first port: byte-identical S3 key layout, identical `settings.yaml` schema, and every
//! load-bearing invariant of the original reproduced. See the migration plan for the full contract.
//!
//! Modules map 1:1 onto the original's folders:
//! - [`config`]       ← `Configuration/`
//! - [`runtime`]      ← `Runtime/`
//! - [`git`]          ← `Services/Git/`
//! - [`providers`]    ← `Services/Providers/`
//! - [`repositories`] ← `Services/Repositories/`
//! - [`scheduling`]   ← `Services/Scheduling/`
//! - [`storage`]      ← `Services/Storage/`
//! - [`paths`]        ← `Services/Paths/`

pub mod config;
pub mod git;
pub mod paths;
pub mod providers;
pub mod repositories;
pub mod retry;
pub mod runtime;
pub mod scheduling;
pub mod storage;
