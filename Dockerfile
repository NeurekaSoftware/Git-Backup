# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY GitBackup.csproj ./
RUN dotnet restore GitBackup.csproj

COPY . ./
RUN dotnet publish GitBackup.csproj \
    -c ${BUILD_CONFIGURATION} \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
ARG GIT_TAG=dev
ARG GIT_HASH=unknown
ENV GIT_TAG=${GIT_TAG}
ENV GIT_HASH=${GIT_HASH}

RUN apk add --no-cache git git-lfs ca-certificates \
    && git lfs install --system --skip-repo

RUN mkdir -p /app/bin /app/data

WORKDIR /app/bin
COPY --from=build /app/publish/ ./

ENTRYPOINT ["dotnet", "GitBackup.dll"]
