//! Hot-reloading settings ← `LiveSettings`.
//!
//! Polling, not a filesystem watcher: the settings file is bind-mounted into the container, where a
//! single-file mount pins the original inode and a host editor's write-and-rename raises no event.
//! Re-reading on a 2s interval detects the change wherever the mount surfaces it, on any host/editor.
//! Change detection is by SHA-256 of the content (not mtime); a failed reload keeps the last good
//! settings. The current snapshot is published through an `ArcSwap` so readers never block a writer.

use super::model::Settings;
use crate::runtime::logger;
use arc_swap::ArcSwap;
use sha2::{Digest, Sha256};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tokio::task::JoinHandle;
use tokio_util::sync::CancellationToken;

const POLL_INTERVAL: Duration = Duration::from_secs(2);

pub struct LiveSettings {
    settings_path: PathBuf,
    current: Arc<ArcSwap<Settings>>,
    cancel: CancellationToken,
    poll_task: Mutex<Option<JoinHandle<()>>>,
}

impl LiveSettings {
    /// Wraps the settings loaded at startup. Call [`LiveSettings::start`] to begin polling.
    pub fn new(settings_path: impl AsRef<Path>, initial: Settings) -> Self {
        let settings_path = settings_path.as_ref().to_path_buf();
        Self {
            settings_path,
            current: Arc::new(ArcSwap::from_pointee(initial)),
            cancel: CancellationToken::new(),
            poll_task: Mutex::new(None),
        }
    }

    pub fn settings_path(&self) -> &Path {
        &self.settings_path
    }

    /// The latest snapshot. Cheap and lock-free; callers read a fresh copy each run.
    pub fn current(&self) -> Arc<Settings> {
        self.current.load_full()
    }

    /// Starts the background poll task. Idempotent — a second call is a no-op.
    pub fn start(&self) {
        let mut guard = self.poll_task.lock().unwrap();
        if guard.is_some() {
            return;
        }

        // Seed the baseline from the file the initial settings were loaded from, so the first poll only
        // reloads when the content has actually changed since startup.
        let baseline = try_compute_content_hash(&self.settings_path);
        let path = self.settings_path.clone();
        let current = Arc::clone(&self.current);
        let cancel = self.cancel.clone();

        let handle = tokio::spawn(async move {
            poll_for_changes(path, current, cancel, baseline).await;
        });
        *guard = Some(handle);

        tracing::info!(
            "Watching settings file for changes. settingsPath={}.",
            self.settings_path.display()
        );
    }
}

impl Drop for LiveSettings {
    fn drop(&mut self) {
        self.cancel.cancel();
        if let Some(handle) = self.poll_task.lock().unwrap().take() {
            handle.abort();
        }
    }
}

async fn poll_for_changes(
    path: PathBuf,
    current: Arc<ArcSwap<Settings>>,
    cancel: CancellationToken,
    mut last_hash: Option<[u8; 32]>,
) {
    let mut ticker = tokio::time::interval(POLL_INTERVAL);
    // Missed ticks (a slow reload) should not burst-fire; skip to the next whole interval.
    ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);

    loop {
        tokio::select! {
            _ = cancel.cancelled() => break,
            _ = ticker.tick() => check_for_change(&path, &mut last_hash, &current),
        }
    }
}

fn check_for_change(path: &Path, last_hash: &mut Option<[u8; 32]>, current: &ArcSwap<Settings>) {
    let Some(current_hash) = try_compute_content_hash(path) else {
        // A transient read miss (the file briefly gone during a save) leaves the baseline untouched so
        // the next tick retries; the existing settings keep running.
        tracing::debug!(
            "Settings file was not readable this poll; keeping current settings. settingsPath={}.",
            path.display()
        );
        return;
    };

    if last_hash.as_ref() == Some(&current_hash) {
        return;
    }

    *last_hash = Some(current_hash);
    tracing::info!(
        "Settings file changed on disk. Reloading. settingsPath={}.",
        path.display()
    );
    reload(path, current);
}

fn reload(path: &Path, current: &ArcSwap<Settings>) {
    let result = super::loader::load(path);
    let Some(reloaded) = result.settings else {
        tracing::error!(
            "Settings reload failed. settingsPath={}. Existing settings will be kept.",
            path.display()
        );
        for error in result.errors {
            tracing::error!("Settings reload validation error. error={error}.");
        }
        return;
    };

    if let Some(level) = logger::try_parse_log_level(reloaded.logging.log_level.as_deref()) {
        logger::set_min_level(level);
    }

    let log_level = reloaded.logging.log_level.clone().unwrap_or_default();
    current.store(Arc::new(reloaded));

    tracing::info!(
        "Settings reloaded successfully. settingsPath={}.",
        path.display()
    );
    tracing::debug!("Current log level from settings. logLevel={log_level}.");
}

fn try_compute_content_hash(path: &Path) -> Option<[u8; 32]> {
    let bytes = std::fs::read(path).ok()?;
    let mut hasher = Sha256::new();
    hasher.update(&bytes);
    Some(hasher.finalize().into())
}
