//! Git CLI wrapper ← `GitCliRepositoryService`.
//!
//! Shells out to `git`/`git-lfs`. Stores repositories as bare `--mirror` clones; when caching an
//! existing mirror it fetches incrementally and self-heals a broken mirror by re-cloning. Security is
//! reproduced verbatim: only http/https transports (blocking `ext::`/`file://`), no credential over
//! plaintext http to a non-loopback host, the auth header injected through git's env-config (never on
//! argv) scoped to the remote's own origin, credential helpers disabled, terminal prompts off, and the
//! whole git process group killed on cancellation.

use super::credential::GitCredential;
use super::error::GitError;
use crate::paths::git_url;
use async_trait::async_trait;
use base64::Engine;
use std::path::Path;
use std::process::Stdio;
use tokio::io::AsyncReadExt;
use tokio_util::sync::CancellationToken;

#[async_trait]
pub trait GitRepository: Send + Sync {
    async fn sync_bare_repository(
        &self,
        remote_url: &str,
        local_path: &Path,
        credential: Option<&GitCredential>,
        cache: bool,
        include_lfs: bool,
        cancel: &CancellationToken,
    ) -> Result<(), GitError>;
}

#[derive(Debug, Default, Clone)]
pub struct GitCliRepositoryService;

#[async_trait]
impl GitRepository for GitCliRepositoryService {
    async fn sync_bare_repository(
        &self,
        remote_url: &str,
        local_path: &Path,
        credential: Option<&GitCredential>,
        cache: bool,
        include_lfs: bool,
        cancel: &CancellationToken,
    ) -> Result<(), GitError> {
        if cancel.is_cancelled() {
            return Err(GitError::Cancelled);
        }

        // Single choke point: a provider-supplied URL must never reach a git transport helper.
        if !is_supported_transport(remote_url) {
            return Err(GitError::Other(format!(
                "Unsupported repository URL '{remote_url}'. Only http and https clone URLs are allowed."
            )));
        }

        // Never put a credential on the wire in the clear.
        if credential.is_some() && is_plaintext_http_to_remote_host(remote_url) {
            return Err(GitError::Other(format!(
                "Refusing to send credentials to '{remote_url}' over plaintext http. Use https, or remove the credential for this remote."
            )));
        }

        let has_mirror = is_bare_repository(local_path, cancel).await;

        if cache && has_mirror {
            // Update the existing mirror; self-heal by re-cloning if the incremental fetch fails.
            let incremental = async {
                set_remote_url(local_path, remote_url, credential, cancel).await?;
                fetch(local_path, remote_url, credential, cancel).await?;
                Ok::<(), GitError>(())
            }
            .await;

            match incremental {
                Ok(()) => {}
                // A shutdown is not a corrupt mirror; re-cloning would delete the cache and then fail on
                // the same cancelled token.
                Err(GitError::Cancelled) => return Err(GitError::Cancelled),
                Err(error) => {
                    tracing::warn!(
                        "Incremental mirror fetch failed; re-cloning from scratch. localPath={}, error={error}.",
                        local_path.display()
                    );
                    fresh_clone(remote_url, local_path, credential, cancel).await?;
                }
            }
        } else {
            fresh_clone(remote_url, local_path, credential, cancel).await?;
        }

        if include_lfs {
            fetch_lfs(local_path, remote_url, credential, cancel).await?;
        }

        Ok(())
    }
}

async fn fresh_clone(
    remote_url: &str,
    local_path: &Path,
    credential: Option<&GitCredential>,
    cancel: &CancellationToken,
) -> Result<(), GitError> {
    if local_path.exists() {
        std::fs::remove_dir_all(local_path).map_err(|e| {
            GitError::Other(format!("failed to remove '{}': {e}", local_path.display()))
        })?;
    }
    if let Some(parent) = local_path.parent() {
        std::fs::create_dir_all(parent).map_err(|e| {
            GitError::Other(format!("failed to create '{}': {e}", parent.display()))
        })?;
    }

    let local = local_path.to_string_lossy();
    execute_git(
        &["clone", "--mirror", "--", remote_url, &local],
        credential,
        Some(remote_url),
        cancel,
        true,
    )
    .await
    .map(|_| ())
}

async fn is_bare_repository(local_path: &Path, cancel: &CancellationToken) -> bool {
    if !local_path.exists() {
        return false;
    }
    let local = local_path.to_string_lossy();
    match execute_git(
        &["-C", &local, "rev-parse", "--is-bare-repository"],
        None,
        None,
        cancel,
        false,
    )
    .await
    {
        Ok(result) => result.exit_code == 0 && result.stdout.trim().eq_ignore_ascii_case("true"),
        Err(_) => false,
    }
}

async fn set_remote_url(
    local_path: &Path,
    remote_url: &str,
    credential: Option<&GitCredential>,
    cancel: &CancellationToken,
) -> Result<(), GitError> {
    let local = local_path.to_string_lossy();
    execute_git(
        &["-C", &local, "remote", "set-url", "origin", remote_url],
        credential,
        Some(remote_url),
        cancel,
        true,
    )
    .await
    .map(|_| ())
}

async fn fetch(
    local_path: &Path,
    remote_url: &str,
    credential: Option<&GitCredential>,
    cancel: &CancellationToken,
) -> Result<(), GitError> {
    let local = local_path.to_string_lossy();
    execute_git(
        &["-C", &local, "fetch", "--all", "--prune"],
        credential,
        Some(remote_url),
        cancel,
        true,
    )
    .await
    .map(|_| ())
}

async fn fetch_lfs(
    local_path: &Path,
    remote_url: &str,
    credential: Option<&GitCredential>,
    cancel: &CancellationToken,
) -> Result<(), GitError> {
    let local = local_path.to_string_lossy();
    let result = execute_git(
        &["-C", &local, "lfs", "fetch", "--all"],
        credential,
        Some(remote_url),
        cancel,
        false,
    )
    .await?;

    if result.exit_code == 0 {
        return Ok(());
    }

    // A remote can have Git LFS disabled entirely; that is an expected state, not a backup failure.
    if is_lfs_disabled_on_remote(&result.stderr) {
        tracing::info!(
            "Skipped Git LFS fetch because it is disabled on the remote. repository={remote_url}."
        );
        return Ok(());
    }

    Err(GitError::Other(format!(
        "git lfs fetch failed (exit={}). stdout: {}. stderr: {}",
        result.exit_code, result.stdout, result.stderr
    )))
}

struct CommandResult {
    exit_code: i32,
    stdout: String,
    stderr: String,
}

async fn execute_git(
    args: &[&str],
    credential: Option<&GitCredential>,
    credential_scope_url: Option<&str>,
    cancel: &CancellationToken,
    throw_on_failure: bool,
) -> Result<CommandResult, GitError> {
    let mut std_cmd = std::process::Command::new("git");
    std_cmd
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    for (key, value) in build_git_env(credential, credential_scope_url) {
        std_cmd.env(key, value);
    }
    // Put git in its own process group so the whole tree (http, git-lfs) can be signalled on cancel.
    #[cfg(unix)]
    {
        use std::os::unix::process::CommandExt;
        std_cmd.process_group(0);
    }

    let mut command = tokio::process::Command::from(std_cmd);
    command.kill_on_drop(true);
    let mut child = command
        .spawn()
        .map_err(|e| GitError::Other(format!("failed to start git: {e}")))?;
    let pid = child.id();

    let mut stdout_pipe = child.stdout.take().expect("stdout piped");
    let mut stderr_pipe = child.stderr.take().expect("stderr piped");
    let out_task = tokio::spawn(async move {
        let mut buffer = String::new();
        let _ = stdout_pipe.read_to_string(&mut buffer).await;
        buffer
    });
    let err_task = tokio::spawn(async move {
        let mut buffer = String::new();
        let _ = stderr_pipe.read_to_string(&mut buffer).await;
        buffer
    });

    let status = tokio::select! {
        _ = cancel.cancelled() => {
            // Don't leave a detached clone/fetch/LFS transfer running after cancellation.
            kill_process_tree(pid);
            let _ = child.start_kill();
            let _ = child.wait().await;
            let _ = out_task.await;
            let _ = err_task.await;
            return Err(GitError::Cancelled);
        }
        status = child.wait() => {
            status.map_err(|e| GitError::Other(format!("git wait failed: {e}")))?
        }
    };

    let stdout = out_task.await.unwrap_or_default();
    let stderr = err_task.await.unwrap_or_default();
    let exit_code = status.code().unwrap_or(-1);
    let result = CommandResult {
        exit_code,
        stdout,
        stderr,
    };

    if throw_on_failure && result.exit_code != 0 {
        let message = format!(
            "git command failed (exit={}). stdout: {}. stderr: {}",
            result.exit_code, result.stdout, result.stderr
        );
        return Err(if is_remote_inaccessible(&result.stderr) {
            GitError::RemoteInaccessible(message)
        } else {
            GitError::Other(message)
        });
    }

    Ok(result)
}

fn kill_process_tree(pid: Option<u32>) {
    #[cfg(unix)]
    if let Some(pid) = pid {
        // The child leads its own process group (process_group(0)), so its pid is the pgid; SIGKILL the
        // whole group to take git's http/lfs subprocesses down with it.
        unsafe {
            libc::killpg(pid as libc::pid_t, libc::SIGKILL);
        }
    }
    #[cfg(not(unix))]
    let _ = pid;
}

/// Builds the environment for a git invocation ← the `GIT_CONFIG_*` injection. Returned (not applied)
/// so the exact security-critical values can be asserted in tests.
fn build_git_env(
    credential: Option<&GitCredential>,
    credential_scope_url: Option<&str>,
) -> Vec<(String, String)> {
    let mut env = vec![("GIT_TERMINAL_PROMPT".to_string(), "0".to_string())];
    if let Some(credential) = credential {
        // Inject the auth header via env-config, not `-c`, so the token never reaches
        // /proc/<pid>/cmdline, and scope it to the remote's own origin so a redirect or a
        // repository-supplied LFS endpoint on another host never receives it.
        let scope = build_credential_scope(credential_scope_url);
        env.push(("GIT_CONFIG_COUNT".to_string(), "2".to_string()));
        env.push((
            "GIT_CONFIG_KEY_0".to_string(),
            format!("http.{scope}.extraheader"),
        ));
        env.push((
            "GIT_CONFIG_VALUE_0".to_string(),
            format!("Authorization: Basic {}", create_basic_header(credential)),
        ));
        env.push((
            "GIT_CONFIG_KEY_1".to_string(),
            "credential.helper".to_string(),
        ));
        env.push(("GIT_CONFIG_VALUE_1".to_string(), String::new()));
    }
    env
}

fn create_basic_header(credential: &GitCredential) -> String {
    base64::engine::general_purpose::STANDARD
        .encode(format!("{}:{}", credential.username, credential.password))
}

/// Derives the `scheme://host[:port]/` origin used to scope the auth header. A parse failure yields a
/// scope that matches no request (fails closed, withholding the header) rather than leaking the token.
fn build_credential_scope(credential_scope_url: Option<&str>) -> String {
    match credential_scope_url.and_then(|value| url::Url::parse(value).ok()) {
        Some(uri) => format!("{}/", uri.origin().ascii_serialization()),
        None => credential_scope_url.unwrap_or_default().to_string(),
    }
}

fn is_supported_transport(remote_url: &str) -> bool {
    git_url::try_create_http_url(Some(remote_url)).is_some()
}

fn is_plaintext_http_to_remote_host(remote_url: &str) -> bool {
    match url::Url::parse(remote_url) {
        Ok(uri) => uri.scheme() == "http" && !git_url::is_loopback(&uri),
        Err(_) => false,
    }
}

fn is_lfs_disabled_on_remote(stderr: &str) -> bool {
    stderr.to_lowercase().contains("git lfs is disabled")
}

// git's stderr signals for a remote that cannot be accessed (private, removed, or wrong/missing
// credentials). Genuine failures (DNS, TLS, connection refused, corruption) match none of these.
const REMOTE_INACCESSIBLE_SIGNALS: [&str; 8] = [
    "could not read Username",
    "could not read Password",
    "terminal prompts disabled",
    "Authentication failed",
    "Repository not found",
    "The requested URL returned error: 401",
    "The requested URL returned error: 403",
    "The requested URL returned error: 404",
];

fn is_remote_inaccessible(stderr: &str) -> bool {
    let lower = stderr.to_lowercase();
    REMOTE_INACCESSIBLE_SIGNALS
        .iter()
        .any(|signal| lower.contains(&signal.to_lowercase()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn transport_allowlist_blocks_non_http() {
        assert!(is_supported_transport("https://github.com/o/r.git"));
        assert!(is_supported_transport("http://localhost/o/r"));
        assert!(!is_supported_transport("ssh://git@github.com/o/r"));
        assert!(!is_supported_transport("ext::sh -c whoami"));
        assert!(!is_supported_transport("file:///etc/passwd"));
    }

    #[test]
    fn plaintext_http_credential_refusal() {
        assert!(is_plaintext_http_to_remote_host("http://example.com/o/r"));
        // Loopback http is allowed for local testing.
        assert!(!is_plaintext_http_to_remote_host(
            "http://localhost:3000/o/r"
        ));
        assert!(!is_plaintext_http_to_remote_host("http://127.0.0.1/o/r"));
        // https is always fine.
        assert!(!is_plaintext_http_to_remote_host("https://example.com/o/r"));
    }

    #[test]
    fn credential_scope_is_origin_only() {
        assert_eq!(
            build_credential_scope(Some("https://github.com/owner/repo.git")),
            "https://github.com/"
        );
        assert_eq!(
            build_credential_scope(Some("https://code.neureka.dev:8443/a/b")),
            "https://code.neureka.dev:8443/"
        );
    }

    #[test]
    fn git_env_injects_scoped_auth_header_and_disables_helpers() {
        let credential = GitCredential::new("git", "secret-token");
        let env = build_git_env(Some(&credential), Some("https://github.com/o/r.git"));
        let get = |key: &str| env.iter().find(|(k, _)| k == key).map(|(_, v)| v.clone());

        assert_eq!(get("GIT_TERMINAL_PROMPT").as_deref(), Some("0"));
        assert_eq!(get("GIT_CONFIG_COUNT").as_deref(), Some("2"));
        assert_eq!(
            get("GIT_CONFIG_KEY_0").as_deref(),
            Some("http.https://github.com/.extraheader")
        );
        // base64("git:secret-token")
        assert_eq!(
            get("GIT_CONFIG_VALUE_0").as_deref(),
            Some("Authorization: Basic Z2l0OnNlY3JldC10b2tlbg==")
        );
        assert_eq!(
            get("GIT_CONFIG_KEY_1").as_deref(),
            Some("credential.helper")
        );
        assert_eq!(get("GIT_CONFIG_VALUE_1").as_deref(), Some(""));
    }

    #[test]
    fn no_credential_still_disables_terminal_prompt_only() {
        let env = build_git_env(None, Some("https://github.com/o/r"));
        assert_eq!(env.len(), 1);
        assert_eq!(env[0], ("GIT_TERMINAL_PROMPT".to_string(), "0".to_string()));
    }

    #[test]
    fn classifies_inaccessible_vs_genuine_errors() {
        assert!(is_remote_inaccessible(
            "fatal: Authentication failed for 'https://...'"
        ));
        assert!(is_remote_inaccessible("remote: Repository not found."));
        assert!(is_remote_inaccessible(
            "The requested URL returned error: 404"
        ));
        assert!(is_remote_inaccessible("terminal prompts disabled"));
        assert!(!is_remote_inaccessible(
            "fatal: unable to access: Could not resolve host"
        ));
        assert!(!is_remote_inaccessible(
            "error: RPC failed; curl 56 GnuTLS recv error"
        ));
    }

    #[test]
    fn detects_lfs_disabled_signal() {
        assert!(is_lfs_disabled_on_remote(
            "batch response: Git LFS is disabled for this repository."
        ));
        assert!(!is_lfs_disabled_on_remote(
            "error: failed to fetch some objects"
        ));
    }
}
