//! Configuration ← `Configuration/`.
//!
//! Settings model, YAML loader (unknown-key + enum + deprecated-key rejection), `${ENV}`/`*File`
//! secret resolution, `url` scalar-or-list, and `LiveSettings` hot reload (2s SHA-256 poll).
//! Ported in phase P1.
