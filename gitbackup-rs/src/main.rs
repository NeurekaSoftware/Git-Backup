//! Composition root. The full manual object graph (mirroring the .NET `Program.cs`) is wired in
//! phase P9. For now it initializes build metadata + logging and emits the startup banner so the
//! console format can be verified against the original.

use gitbackup::runtime::{build_metadata, logger};

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    build_metadata::load_from_environment();
    logger::init();
    tracing::info!(
        "Git Backup started. Version {} ({}).",
        build_metadata::version(),
        build_metadata::commit()
    );

    // TODO(P9): resolve settings path + working root, wire the service graph, run the scheduler.

    logger::shutdown();
    Ok(())
}
