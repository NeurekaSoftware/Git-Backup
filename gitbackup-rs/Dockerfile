# syntax=docker/dockerfile:1.7
#
# Production image for the Rust rewrite. This is the PROMOTION artifact: it is written for a repo-root
# build context (Cargo.toml, src/, entrypoint.sh all at ./), so at promotion it moves to ./Dockerfile
# and replaces the .NET one unchanged. Until then, the Rust code is validated by the `rust-check` CI job.
#
# The runtime stage is deliberately identical in shape to the .NET image (Ubuntu noble + git/git-lfs/
# gosu/tzdata/ca-certificates, /app/bin + /app/data, the same entrypoint.sh privilege-drop, the same
# GIT_TAG/GIT_HASH/PUID/PGID args), so compose.yaml and entrypoint.sh carry over untouched.

FROM --platform=$BUILDPLATFORM lukemathwalker/cargo-chef:latest-rust-1-bookworm AS chef
WORKDIR /app/src

FROM chef AS planner
COPY . .
RUN cargo chef prepare --recipe-path recipe.json

FROM chef AS build
# Cook dependencies on their own cached layer from the recipe alone, so a source-only change reuses them.
COPY --from=planner /app/src/recipe.json recipe.json
RUN cargo chef cook --release --recipe-path recipe.json
COPY . .
RUN cargo build --release --locked \
 && cp target/release/gitbackup /app/gitbackup

FROM ubuntu:24.04
ARG GIT_TAG=dev
ARG GIT_HASH=unknown
# Default to a non-root UID/GID so an image run without PUID/PGID still drops privileges in the
# entrypoint (running as root now requires explicitly passing PUID=0/PGID=0).
ARG PUID=1000
ARG PGID=1000
ENV GIT_TAG=${GIT_TAG}
ENV GIT_HASH=${GIT_HASH}
ENV PUID=$PUID
ENV PGID=$PGID

RUN apt-get update \
 && apt-get install -y --no-install-recommends git git-lfs gosu tzdata ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && git lfs install --system --skip-repo

WORKDIR /app
RUN mkdir -p /app/bin /app/data
# /app/data holds the persisted settings-independent git-mirror cache; declare it a volume so the cache
# survives container recreation instead of forcing a full re-clone each run.
VOLUME ["/app/data"]
COPY --from=build /app/gitbackup /app/bin/gitbackup
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
CMD ["/app/bin/gitbackup"]
