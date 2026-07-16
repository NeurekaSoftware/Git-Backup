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

```yaml
services:
  git-backup:
    image: registry.neureka.dev/git/git-backup:edge
    container_name: git-backup
    volumes:
      - ./data:/app/data
    restart: unless-stopped
```

### settings.yaml

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
  alwaysCalculateContentMd5: false
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
    lfs: false
    enabled: true
  - mode: provider
    provider: gitlab
    credential: gitlab
    lfs: false
    enabled: true
  - mode: provider
    provider: forgejo
    credential: forgejo
    baseUrl: https://codeberg.org
    lfs: false
    enabled: true
  - mode: url
    url: https://code.neureka.dev/git/git-backup
    credential: gitlab
    lfs: true
    enabled: true
  - mode: url
    url: https://gitlab.com/gitlab-org/gitlab
    lfs: false
    enabled: true

schedule:
  repositories:
    cron: "0 */6 * * *"
```
