# syntax=docker/dockerfile:1.7

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
COPY . /app/src/
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    case "$TARGETARCH" in \
      amd64) DOTNET_RID=linux-x64 ;; \
      arm64) DOTNET_RID=linux-arm64 ;; \
      *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac \
 && dotnet restore /app/src/GitBackup.csproj -r "$DOTNET_RID" \
 && dotnet publish /app/src/GitBackup.csproj \
      --no-restore \
      -c ${BUILD_CONFIGURATION} \
      -r "$DOTNET_RID" \
      -o /app/bin

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble
ARG GIT_TAG=dev
ARG GIT_HASH=unknown
ARG PUID
ARG PGID
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
COPY --from=build /app/bin /app/bin
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
CMD ["/app/bin/GitBackup"]
