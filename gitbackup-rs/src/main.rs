//! Composition root ← `Program.cs`.
//!
//! Manually wires the object graph (no DI container), installs SIGINT/SIGTERM shutdown, and runs the
//! scheduler for the process lifetime.

use anyhow::Result;
use gitbackup::config::{self, LiveSettings, StorageConfig};
use gitbackup::git::GitCliRepositoryService;
use gitbackup::providers::RepositoryProviderClientFactory;
use gitbackup::repositories::sync::StorageFactory;
use gitbackup::repositories::{
    LocalMirrorStore, ProjectMetadataSyncService, RepositoryRetentionService, RepositorySyncService,
};
use gitbackup::runtime::{build_metadata, logger};
use gitbackup::scheduling::ScheduledJobRunner;
use gitbackup::storage::{ObjectStorage, S3ObjectStorage};
use std::path::PathBuf;
use std::sync::Arc;
use tokio_util::sync::CancellationToken;

const CONTAINER_CONFIG_PATH: &str = "/app/config";
const CONTAINER_DATA_PATH: &str = "/app/data";

#[tokio::main]
async fn main() -> Result<()> {
    build_metadata::load_from_environment();
    logger::init();
    tracing::info!(
        "Git Backup started. Version {} ({}).",
        build_metadata::version(),
        build_metadata::commit()
    );

    let settings_path = resolve_settings_path();
    tracing::info!("Using settings file {}.", settings_path.display());

    let result = config::load(&settings_path);
    let Some(settings) = result.settings else {
        tracing::error!("Failed to load settings file {}.", settings_path.display());
        for error in result.errors {
            tracing::error!("Settings validation error. error={error}.");
        }
        logger::shutdown();
        std::process::exit(1);
    };

    if let Some(level) = logger::try_parse_log_level(settings.logging.log_level.as_deref()) {
        logger::set_min_level(level);
        tracing::info!(
            "Active log level set. logLevel={}.",
            logger::to_config_value(level)
        );
    }

    let live = Arc::new(LiveSettings::new(&settings_path, settings));
    live.start();
    tracing::info!(
        "Configuration loaded. repositories={}, watcher={}.",
        live.current().repositories.len(),
        live.settings_path().display()
    );

    let working_root = resolve_working_root();
    std::fs::create_dir_all(&working_root)?;
    tracing::info!(
        "Working directory ready. workingRoot={}.",
        working_root.display()
    );

    // Manual object graph (mirrors the .NET `new` graph). The storage client is built per run via a
    // factory because the storage config can hot-reload.
    let storage_factory: StorageFactory = Arc::new(|storage: &StorageConfig| {
        Ok(Box::new(S3ObjectStorage::new(storage)?) as Box<dyn ObjectStorage>)
    });

    let provider_factory = Arc::new(RepositoryProviderClientFactory::new()?);
    let git = Arc::new(GitCliRepositoryService);
    let mirror_store = Arc::new(LocalMirrorStore::new(&working_root));
    let metadata = Arc::new(ProjectMetadataSyncService::new(Arc::clone(
        &provider_factory,
    )));
    let sync = Arc::new(RepositorySyncService::new(
        Arc::clone(&provider_factory),
        git,
        Arc::clone(&storage_factory),
        mirror_store,
        metadata,
    ));
    let retention = Arc::new(RepositoryRetentionService::new(storage_factory));
    let runner = ScheduledJobRunner::new(Arc::clone(&live), sync, retention);

    let shutdown = CancellationToken::new();
    spawn_shutdown_listener(shutdown.clone());

    tracing::info!("Scheduler is running. Press Ctrl+C to stop.");
    runner.run_forever(&shutdown).await;
    tracing::info!("Scheduler stopped.");

    logger::shutdown();
    Ok(())
}

/// Cancels the token on Ctrl+C (SIGINT) or SIGTERM (`docker stop`), so an in-flight clone/upload is
/// cancelled cleanly and logs are flushed instead of the process being hard-killed.
fn spawn_shutdown_listener(shutdown: CancellationToken) {
    tokio::spawn(async move {
        tokio::select! {
            _ = tokio::signal::ctrl_c() => tracing::warn!("Shutdown requested by Ctrl+C."),
            _ = terminate_signal() => tracing::warn!("Shutdown requested by SIGTERM."),
        }
        shutdown.cancel();
    });
}

#[cfg(unix)]
async fn terminate_signal() {
    if let Ok(mut signal) =
        tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
    {
        signal.recv().await;
    } else {
        std::future::pending::<()>().await;
    }
}

#[cfg(not(unix))]
async fn terminate_signal() {
    std::future::pending::<()>().await;
}

fn resolve_settings_path() -> PathBuf {
    if let Some(arg) = std::env::args().nth(1).filter(|a| !a.trim().is_empty()) {
        return PathBuf::from(arg);
    }

    if is_running_in_container() {
        PathBuf::from(CONTAINER_CONFIG_PATH).join("settings.yaml")
    } else {
        std::env::current_dir()
            .unwrap_or_default()
            .join("settings.yaml")
    }
}

fn resolve_working_root() -> PathBuf {
    if let Ok(configured) = std::env::var("GITBACKUP_WORKING_ROOT") {
        if !configured.trim().is_empty() {
            return PathBuf::from(configured);
        }
    }

    // In a container keep the git mirrors under the persisted data directory so the incremental fetch
    // cache survives restarts; outside a container fall back to a temp directory.
    if is_running_in_container() {
        PathBuf::from(CONTAINER_DATA_PATH)
    } else {
        std::env::temp_dir().join(".git-backup")
    }
}

fn is_running_in_container() -> bool {
    std::env::var("DOTNET_RUNNING_IN_CONTAINER")
        .map(|value| value.eq_ignore_ascii_case("true"))
        .unwrap_or(false)
        || std::path::Path::new("/.dockerenv").exists()
}
