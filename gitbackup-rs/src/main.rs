//! Composition root. The full manual object graph (mirroring the .NET `Program.cs`) is wired in
//! phase P9; this scaffold only proves the binary builds, runs, and shuts down cleanly.

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    println!("gitbackup (rust rewrite) — scaffold build");
    Ok(())
}
