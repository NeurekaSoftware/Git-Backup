# Git Backup

Automatically back up repositories from major Git forges to your S3-compatible object storage.

> [!WARNING]  
> This software is under active development. Expect breaking changes and incomplete features.

## Features

### Repository Modes

| Mode | Status | Description |
|---|---|---|
| `provider` | ✅ | Back up all repositories for an account from supported Git forges. |
| `url` | ✅ | Back up any repository via direct URL, without forge API discovery. |

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
  # Defaults: cache on (keep a local mirror and git-fetch each run), LFS on, enabled.
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
    url: https://code.neureka.dev/git/backup
    credential: gitlab
  # Very large repo: clone fresh each run and delete the local mirror after a
  # successful upload instead of keeping it cached on disk.
  - mode: url
    url: https://gitlab.com/gitlab-org/gitlab
    cache: false

schedule:
  repositories:
    cron: "0 */6 * * *"
```

Each entry under `repositories` accepts:

- `mode` — `provider` (discover all repositories for an account) or `url` (a single repository).
- `provider` / `baseUrl` — the forge (`github`, `gitlab`, `forgejo`) and, for self-hosted instances, its base URL (provider mode).
- `url` — the repository URL (url mode).
- `credential` — the credential key used to authenticate.
- `lfs` — back up Git LFS objects. **Default `true`.**
- `cache` — keep a local mirror and `git fetch` it each run (fast subsequent syncs). Set `false` to clone fresh each run and delete the local copy right after a successful upload, bounding local disk to one repository at a time. **Default `true`.**
- `enabled` — set `false` to skip the job. **Default `true`.**

Local mirrors for repositories that are removed from the config (or disabled) are cleaned off disk automatically on the next run.
