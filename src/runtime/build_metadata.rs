//! Build metadata ← `BuildMetadata`.
//!
//! Version and commit are read once from `GIT_TAG`/`GIT_HASH` (baked in by the image build) and used
//! for the startup banner and the HTTP User-Agent.

use std::sync::OnceLock;

static VERSION: OnceLock<String> = OnceLock::new();
static COMMIT: OnceLock<String> = OnceLock::new();

const DEFAULT_VERSION: &str = "dev";
const DEFAULT_COMMIT: &str = "unknown";

pub fn load_from_environment() {
    // OnceLock::set is idempotent-safe: a second call (e.g. in tests) is ignored rather than panicking.
    let _ = VERSION.set(read_value("GIT_TAG", DEFAULT_VERSION));
    let _ = COMMIT.set(read_value("GIT_HASH", DEFAULT_COMMIT));
}

pub fn version() -> &'static str {
    VERSION.get().map(String::as_str).unwrap_or(DEFAULT_VERSION)
}

pub fn commit() -> &'static str {
    COMMIT.get().map(String::as_str).unwrap_or(DEFAULT_COMMIT)
}

fn read_value(variable: &str, fallback: &str) -> String {
    match std::env::var(variable) {
        Ok(value) if !value.trim().is_empty() => value.trim().to_string(),
        _ => fallback.to_string(),
    }
}
