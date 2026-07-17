# syntax=docker/dockerfile:1.7

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
# Restore on its own layer, from the project file alone: a source-only change then reuses it instead of
# re-resolving every package. The NuGet cache is locked for the whole RUN, so keeping compilation out of
# this step releases it as early as possible, and the per-arch id stops the amd64 and arm64 stages of a
# multi-platform build from serializing behind each other on one shared cache.
COPY GitBackup.csproj /app/src/
RUN --mount=type=cache,target=/root/.nuget/packages,id=nuget-$TARGETARCH,sharing=locked \
    case "$TARGETARCH" in \
      amd64) DOTNET_RID=linux-x64 ;; \
      arm64) DOTNET_RID=linux-arm64 ;; \
      *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac \
 && echo "$DOTNET_RID" > /tmp/dotnet-rid \
 && dotnet restore /app/src/GitBackup.csproj -r "$DOTNET_RID"

COPY . /app/src/
RUN --mount=type=cache,target=/root/.nuget/packages,id=nuget-$TARGETARCH,sharing=locked \
    dotnet publish /app/src/GitBackup.csproj \
      --no-restore \
      --no-self-contained \
      -c ${BUILD_CONFIGURATION} \
      -r "$(cat /tmp/dotnet-rid)" \
      -o /app/bin

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble
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
# /app/data holds the persisted settings file and the incremental git-mirror cache; declare it a
# volume so the cache survives container recreation instead of forcing a full re-clone each run.
VOLUME ["/app/data"]
COPY --from=build /app/bin /app/bin
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
CMD ["/app/bin/GitBackup"]
