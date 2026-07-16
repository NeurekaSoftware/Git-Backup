# Git Backup

Automatically back up repositories from major Git forges to your S3-compatible object storage.

> [!WARNING]  
> This software is under active development. Expect breaking changes and incomplete features.

## Features

### Repository Modes

| Mode | Status | Description |
|---|---|---|
| `provider` | ✅ | Back up all repositories for an account from supported Git forges. |
| `url` | ✅ | Back up one or more repositories via direct URL, without forge API discovery. |

### Supported Providers

| Provider | Status |
|---|---|
| GitHub | ✅ |
| GitLab | ✅ |
| Forgejo | ✅ |

### Repository Capabilities

| Capability | GitHub | GitLab | Forgejo | URL mode |
|---|---|---|---|---|
| Git repository data | ✅ | ✅ | ✅ | ✅ |
| Git LFS objects | ✅ | ✅ | ✅ | ✅ |
| Issues | ❌ | ❌ | ❌ | ❌ |
| Issue comments | ❌ | ❌ | ❌ | ❌ |
| Pull requests / merge requests | ❌ | ❌ | ❌ | ❌ |
| PR/MR comments | ❌ | ❌ | ❌ | ❌ |
| Releases | ❌ | ❌ | ❌ | ❌ |
| Release artifacts | ❌ | ❌ | ❌ | ❌ |
| Gists / Snippets | ❌ | ❌ | ❌ | ❌ |

### Protocol Support

| Protocol | Status |
|---|---|
| HTTP / HTTPS | ✅ |
| SSH | ❌ |

## Quick Start

### Docker Compose

Download the compose file and the environment template (saved as `.env`):

```sh
curl -fsSLO https://code.neureka.dev/git/backup/-/raw/main/compose.yaml
curl -fsSL -o .env https://code.neureka.dev/git/backup/-/raw/main/.env.example
```

Create your `settings.yaml` next to `compose.yaml` (see [settings.yaml](#settingsyaml)),
then start the stack:

```sh
docker compose up -d
```

### settings.yaml

Place `settings.yaml` next to `compose.yaml`; `compose` binds it read-only into the container.

> [!TIP]
> These settings support hot reload so you don't have to restart your container after making changes.

```yaml
logging:
  logLevel: info

storage:
  endpoint: https://accountid.r2.cloudflarestorage.com
  region: auto
  bucket: git-backup
  accessKeyId: accessKeyId
  secretAccessKey: secretAccessKey
  forcePathStyle: false
  payloadSignatureMode: full
  retention: 30
  retentionMinimum: 1

credentials:
  github:
    username: git
    apiKey: githubToken
  gitlab:
    username: git
    apiKey: gitlabToken
  forgejo:
    username: git
    apiKey: forgejoToken

repositories:
  - mode: provider
    provider: github
    credential: github
  - mode: provider
    provider: gitlab
    credential: gitlab
  - mode: provider
    provider: forgejo
    credential: forgejo
    baseUrl: https://codeberg.org
  - mode: url
    credential: gitlab
    url:
      - https://code.neureka.dev/git/backup
      - https://code.neureka.dev/git/website

schedule:
  repositories:
    cron: "0 */6 * * *"
```

Each entry under `repositories` accepts:

| Option | Description | Default |
|---|---|---|
| `mode` | `provider` (discover all repositories for an account) or `url` (specific repositories). | *required* |
| `provider` | Forge to discover: `github`, `gitlab`, or `forgejo`. Provider mode. | *required (provider)* |
| `baseUrl` | Base URL of a self-hosted forge instance. Provider mode. | forge's public URL |
| `url` | A single repository URL, or a list of repository URLs. URL mode. | *required (url)* |
| `credential` | Credential key (from `credentials`) used to authenticate. Optional for public repositories in url mode. | *required (provider)* |
| `lfs` | Back up Git LFS objects. | `true` |
| `cache` | Keep a local mirror and `git fetch` it each run for fast subsequent syncs. Set `false` to clone fresh each run and delete the local copy after a successful upload, bounding local disk to one repository at a time. | `true` |
| `enabled` | Set `false` to skip the job. | `true` |

Local mirrors for repositories that are removed from the config (or disabled) are cleaned off disk automatically on the next run.
