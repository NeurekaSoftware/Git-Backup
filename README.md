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
| Git repositories | ✅ | ✅ | ✅ | ✅ |
| Starred repositories | ✅ | ✅ | ✅ | ➖ |
| Git LFS objects | ✅ | ✅ | ✅ | ✅ |
| Issues | ✅ | ✅ | ✅ | ➖ |
| Issue comments | ✅ | ✅ | ✅ | ➖ |
| Pull requests / merge requests | ✅ | ✅ | ✅ | ➖ |
| PR/MR comments | ✅ | ✅ | ✅ | ➖ |
| Issue / PR / MR attachments | ✅ | ✅ | ✅ | ➖ |
| Releases | ✅ | ✅ | ✅ | ➖ |
| Release artifacts | ✅ | ✅ | ✅ | ➖ |
| Gists / Snippets | ✅ | ✅ | ➖ | ➖ |
| Starred gists / snippets | ✅ | ➖ | ➖ | ➖ |

✅ supported · 🚧 on the roadmap · ❌ unsupported · ➖ not applicable

### Protocol Support

| Protocol | Status |
|---|---|
| HTTP / HTTPS | ✅ |
| SSH | 🚧 |

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
    includeStarred: true
    includeSnippets: true
  - mode: provider
    provider: gitlab
    credential: gitlab
    includeIssues: true
    includeIssueArtifacts: true
    includeMergeRequests: true
    includeMergeRequestsArtifacts: true
    includeReleases: true
    includeReleaseArtifacts: true
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
| `includeStarred` | Also back up repositories the account has starred. Provider mode. | `false` |
| `includeSnippets` | Also back up your GitHub gists and GitLab snippets. GitHub also backs up starred gists when `includeStarred` is on; GitLab has no starred snippets. Provider mode. | `false` |
| `includeIssues` | Back up issues and their comment threads as JSON. Provider mode. | `false` |
| `includeIssueArtifacts` | Also download files attached to issues and their comments. Requires `includeIssues`. Provider mode. | `false` |
| `includeMergeRequests` | Back up pull/merge requests and their comment threads as JSON. Provider mode. | `false` |
| `includeMergeRequestsArtifacts` | Also download files attached to pull/merge requests and their comments. Requires `includeMergeRequests`. Provider mode. | `false` |
| `includeReleases` | Back up releases (tag, notes, and asset references) as JSON. Provider mode. | `false` |
| `includeReleaseArtifacts` | Also download release asset files. Requires `includeReleases`. On GitLab this downloads attached asset-link files hosted on your instance only — auto-generated source archives are skipped (the repo mirror already captures them) and external links are recorded as references without being downloaded. Provider mode. | `false` |
| `url` | A single repository URL, or a list of repository URLs. URL mode. | *required (url)* |
| `credential` | Credential key (from `credentials`) used to authenticate. Optional for public repositories in url mode. | *required (provider)* |
| `lfs` | Back up Git LFS objects. | `true` |
| `cache` | Keep a local mirror and `git fetch` it each run for fast subsequent syncs. Set `false` to clone fresh each run and delete the local copy after a successful upload, bounding local disk to one repository at a time. | `true` |
| `enabled` | Set `false` to skip the job. | `true` |

Local mirrors for repositories that are removed from the config (or disabled) are cleaned off disk automatically on the next run.

> [!NOTE]
> Backing up GitHub gists (`includeSnippets`) requires a **classic** personal access token with the `gist` scope — fine-grained tokens cannot read gists.

> [!IMPORTANT]
> Issues, pull/merge requests, releases, and their attachments are backed up for **owned** repositories only. They are never fetched for **starred** repositories — even when `includeStarred` is enabled — nor for gists or snippets.

Issues, pull/merge requests, and releases are stored as latest-state JSON documents (issues and MRs each embed their comment thread) next to the repository's Git snapshots, under `issues/{number}.json`, `merge-requests/{number}.json`, and `releases/{tag}.json`, with an `index.json` manifest per collection and downloaded files under `{collection}/attachments/{id}/`. Each run overwrites these in place and removes documents for items that no longer exist upstream.
