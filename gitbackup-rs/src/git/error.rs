//! Git error taxonomy ← `GitRemoteInaccessibleException` + generic failures.

/// A git operation failure. `RemoteInaccessible` is the expected, per-repository condition (private,
/// removed, or wrong/missing credentials) that callers skip with a warning; everything else is a real
/// failure. `Cancelled` mirrors an `OperationCanceledException` on the shutdown token.
#[derive(Debug, thiserror::Error)]
pub enum GitError {
    #[error("{0}")]
    RemoteInaccessible(String),

    #[error("git operation cancelled")]
    Cancelled,

    #[error("{0}")]
    Other(String),
}
