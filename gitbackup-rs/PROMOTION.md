# Promotion runbook — swap the Rust rewrite into the repo root

This is the final, one-commit swap that removes the .NET app and promotes `gitbackup-rs/` to the repo
root. **Do not run it until the gates below are green** — the destructive deletion of the production
.NET app is exactly what the "no holes" rule guards against.

## Gates (all must pass first)

1. **`rust-check` CI job green** on `main` — fmt, clippy, tests, release build.
2. **S3 parity** — run both apps against the same MinIO bucket *and* the Backblaze test bucket, diff the
   resulting key trees and the gunzip'd archives; they must be identical. This exercises the rust-s3
   quirks (SigV4 modes, path/virtual-host, multipart, Backblaze Content-Type) the unit tests can't.
3. **Git parity** — clone/fetch a representative repo (with LFS) via both apps; the bare mirrors match
   (ref list + object count); a 404 warns-and-skips; cancel leaves no orphan `git` subprocess.
4. **Provider parity** — discovery lists and metadata key trees match .NET for GitHub/GitLab/Forgejo.
5. **Shadow soak** — run the Rust image beside the .NET one on a staging bucket for a full schedule
   cycle; full key trees match, and RSS stays flat across a multi-GiB repo.

The container-dependent gates (2–5) run in CI / staging — this machine has no Docker (per project setup).

## The promotion commit

From the repo root (`git/backup`):

```sh
# 1. Remove the .NET application (keep entrypoint.sh, compose.yaml, .env.example, CLAUDE.md, README.md).
git rm -r Program.cs GitBackup.csproj GitBackup.slnx Configuration Runtime Services
git rm -r --ignore-unmatch bin obj
git rm Dockerfile            # replaced by the Rust one below

# 2. Move the Rust crate to the root.
git mv gitbackup-rs/Dockerfile Dockerfile
git mv gitbackup-rs/Cargo.toml gitbackup-rs/Cargo.lock gitbackup-rs/rust-toolchain.toml .
git mv gitbackup-rs/src gitbackup-rs/tests .
# Fold the Rust ignore rules into the root .gitignore, then drop the now-empty crate dir.
cat gitbackup-rs/.gitignore >> .gitignore
git rm gitbackup-rs/.gitignore gitbackup-rs/PROMOTION.md
git rm -r --ignore-unmatch gitbackup-rs/target gitbackup-rs/.cargo
rmdir gitbackup-rs 2>/dev/null || true
```

Then, by hand:

- **`.dockerignore`** — remove the `gitbackup-rs/` line; replace the .NET `**/bin`/`**/obj` entries with
  `target` (the Rust build dir) plus `.git`, `config/`, `.env`, `compose.yaml`, `*.md` as before.
- **`.gitlab-ci.yml`** — delete the `rust-check` job (the crate is at root now; keep it only if you want
  a root-context lint/test job, with the `gitbackup-rs/` path prefixes removed). Drop
  `--build-arg BUILD_CONFIGURATION=Release` from the `docker-edge` inputs (the Rust Dockerfile always
  builds `--release`); keep `GIT_TAG=edge` and `GIT_HASH=$CI_COMMIT_SHA`.
- **`README.md`** — no user-facing config changes (settings.yaml schema, key layout, and compose flow
  are identical). Note the two rust-s3 behaviors that differ from the .NET app: `payloadSignatureMode`
  is honored as `full` only, and metadata JSON is stored uncompressed.
- **`compose.yaml`** and **`entrypoint.sh`** — unchanged (same image name, caps, volumes, config mount;
  same gosu privilege drop; the binary path `/app/bin/gitbackup` matches the .NET `CMD`).

## Verify after promotion

```sh
docker buildx build --platform linux/amd64,linux/arm64 \
  --build-arg GIT_TAG=edge --build-arg GIT_HASH=$(git rev-parse HEAD) -t git-backup:test .
# Run via compose.yaml unchanged; confirm gosu privilege drop, /app/config read, /app/data write,
# a clean SIGTERM shutdown, and a staging run whose key tree matches the last .NET run.
```

The .NET app runs in production right up to this commit; the Rust app takes over immediately after.
