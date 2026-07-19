//! Canonical string values for the enum-like config fields ← the `*Providers`/`*Modes`/`*Modes`
//! constant classes. Each lists its supported set (declaration order = the order used in validation
//! error messages) so the validator, its messages, and dispatch cannot drift apart.

/// Repository providers ← `RepositoryProviders`.
pub mod providers {
    pub const GITHUB: &str = "github";
    pub const GITLAB: &str = "gitlab";
    pub const FORGEJO: &str = "forgejo";

    pub const SUPPORTED: [&str; 3] = [GITHUB, GITLAB, FORGEJO];

    pub fn is_supported(value: &str) -> bool {
        SUPPORTED.iter().any(|s| s.eq_ignore_ascii_case(value))
    }
}

/// Repository job modes ← `RepositoryJobModes`.
pub mod modes {
    pub const PROVIDER: &str = "provider";
    pub const URL: &str = "url";

    pub const SUPPORTED: [&str; 2] = [PROVIDER, URL];

    pub fn is_supported(value: &str) -> bool {
        SUPPORTED.iter().any(|s| s.eq_ignore_ascii_case(value))
    }
}

/// S3 payload-signature modes ← `PayloadSignatureModes`.
pub mod payload_signature {
    pub const FULL: &str = "full";
    pub const STREAMING: &str = "streaming";
    pub const UNSIGNED: &str = "unsigned";

    pub const SUPPORTED: [&str; 3] = [FULL, STREAMING, UNSIGNED];

    pub fn is_supported(value: &str) -> bool {
        SUPPORTED.iter().any(|s| s.eq_ignore_ascii_case(value))
    }

    /// Maps a configured value to a canonical mode, defaulting to `full`.
    pub fn normalize(value: Option<&str>) -> String {
        match value.map(|v| v.trim().to_ascii_lowercase()).as_deref() {
            Some(STREAMING) => STREAMING.to_string(),
            Some(UNSIGNED) => UNSIGNED.to_string(),
            _ => FULL.to_string(),
        }
    }
}
