//! Config load/validation parity tests ← behavior of `SettingsLoader` / `SecretResolver` /
//! `LiveSettings`. Error messages are asserted verbatim against the .NET originals.

use gitbackup::config::{self, SettingsLoadResult};
use std::io::Write;
use std::path::PathBuf;
use std::sync::atomic::{AtomicUsize, Ordering};

static COUNTER: AtomicUsize = AtomicUsize::new(0);

/// Writes `yaml` to a unique temp file and loads it, mirroring the real file-based path.
fn load_yaml(yaml: &str) -> (SettingsLoadResult, PathBuf) {
    let id = COUNTER.fetch_add(1, Ordering::Relaxed);
    let path = std::env::temp_dir().join(format!("gitbackup-cfg-{}-{id}.yaml", std::process::id()));
    let mut file = std::fs::File::create(&path).unwrap();
    file.write_all(yaml.as_bytes()).unwrap();
    file.flush().unwrap();
    (config::load(&path), path)
}

fn errors_of(yaml: &str) -> Vec<String> {
    let (result, path) = load_yaml(yaml);
    let _ = std::fs::remove_file(path);
    assert!(!result.is_success(), "expected load to fail:\n{yaml}");
    result.errors
}

fn expect_error(yaml: &str, message: &str) {
    let errors = errors_of(yaml);
    assert!(
        errors.iter().any(|e| e == message),
        "expected error {message:?}, got {errors:?}"
    );
}

const README_CONFIG: &str = r#"
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
"#;

#[test]
fn parses_readme_config_with_defaults() {
    let (result, path) = load_yaml(README_CONFIG);
    let _ = std::fs::remove_file(path);
    assert!(result.is_success(), "errors: {:?}", result.errors);
    let s = result.settings.unwrap();

    assert_eq!(s.logging.log_level.as_deref(), Some("info"));
    assert_eq!(
        s.storage.endpoint.as_deref(),
        Some("https://accountid.r2.cloudflarestorage.com")
    );
    assert_eq!(s.storage.region.as_deref(), Some("auto"));
    assert_eq!(s.storage.bucket.as_deref(), Some("git-backup"));
    assert_eq!(s.storage.payload_signature_mode.as_deref(), Some("full"));
    assert_eq!(s.storage.retention, Some(30));
    assert_eq!(s.storage.retention_minimum, Some(1));

    // Credentials are looked up case-insensitively.
    assert!(s.has_credential("GitHub"));
    assert_eq!(
        s.credential("gitlab").unwrap().api_key.as_deref(),
        Some("gitlabToken")
    );

    assert_eq!(s.repositories.len(), 4);
    let github = s.repositories[0].as_ref().unwrap();
    assert_eq!(github.mode.as_deref(), Some("provider"));
    assert_eq!(github.provider.as_deref(), Some("github"));
    assert_eq!(github.include_starred, Some(true));
    assert_eq!(github.include_snippets, Some(true));
    // Defaults applied where unset.
    assert_eq!(github.enabled, Some(true));
    assert_eq!(github.lfs, Some(true));
    assert_eq!(github.cache, Some(true));
    assert_eq!(github.include_issues, Some(false));

    let url_job = s.repositories[3].as_ref().unwrap();
    assert_eq!(url_job.mode.as_deref(), Some("url"));
    assert_eq!(url_job.urls.as_ref().unwrap().len(), 2);

    // Normalized global defaults.
    assert_eq!(s.storage.force_path_style, Some(false));
    assert_eq!(s.concurrency.repositories, Some(1));
    assert_eq!(s.concurrency.metadata, Some(1));
}

#[test]
fn url_accepts_scalar_or_sequence() {
    let scalar = r#"
storage: { endpoint: "https://s3.example.com", region: r, bucket: b, accessKeyId: a, secretAccessKey: s }
schedule: { repositories: { cron: "0 0 * * *" } }
repositories:
  - mode: url
    url: https://example.com/one
"#;
    let (result, path) = load_yaml(scalar);
    let _ = std::fs::remove_file(path);
    assert!(result.is_success(), "errors: {:?}", result.errors);
    let urls = result.settings.unwrap().repositories[0]
        .as_ref()
        .unwrap()
        .urls
        .clone()
        .unwrap();
    assert_eq!(urls, vec!["https://example.com/one".to_string()]);
}

#[test]
fn rejects_unknown_key() {
    // deny_unknown_fields surfaces a typo as an error rather than silently ignoring it.
    let yaml = "storage:\n  endpont: https://s3.example.com\n";
    let errors = errors_of(yaml);
    assert!(
        errors.iter().any(|e| e.contains("YAML parse error")),
        "got {errors:?}"
    );
}

#[test]
fn rejects_invalid_log_level() {
    expect_error(
        "logging:\n  logLevel: verbse\n",
        "logging.logLevel 'verbse' is invalid. Supported values: debug, info, warn, error.",
    );
}

#[test]
fn rejects_invalid_payload_signature_mode() {
    expect_error(
        "storage:\n  payloadSignatureMode: bogus\n",
        "storage.payloadSignatureMode 'bogus' is invalid. Supported values: full, streaming, unsigned.",
    );
}

#[test]
fn rejects_deprecated_keys() {
    expect_error(
        "backups: []\n",
        "backups is no longer supported. Use repositories entries with mode: provider.",
    );
    expect_error(
        "mirrors: []\n",
        "mirrors is no longer supported. Use repositories entries with mode: url.",
    );
    expect_error(
        "schedule:\n  backups:\n    cron: x\n",
        "schedule.backups is no longer supported. Use schedule.repositories.cron.",
    );
}

#[test]
fn storage_required_fields_and_scheme_rules() {
    expect_error(
        "logging:\n  logLevel: info\n",
        "storage.endpoint is required.",
    );
    expect_error(
        "storage:\n  endpoint: ftp://x\n",
        "storage.endpoint must be an absolute http or https URL.",
    );
    expect_error(
        "storage:\n  endpoint: http://s3.example.com\n",
        "storage.endpoint must use https for a non-loopback host.",
    );
}

#[test]
fn plain_http_loopback_endpoint_is_allowed() {
    let yaml = r#"
storage: { endpoint: "http://localhost:9000", region: r, bucket: b, accessKeyId: a, secretAccessKey: s }
schedule: { repositories: { cron: "0 0 * * *" } }
"#;
    let (result, path) = load_yaml(yaml);
    let _ = std::fs::remove_file(path);
    assert!(
        result.is_success(),
        "loopback http should be allowed: {:?}",
        result.errors
    );
}

#[test]
fn provider_mode_validation_messages() {
    let base = "storage: { endpoint: \"https://s3.example.com\", region: r, bucket: b, accessKeyId: a, secretAccessKey: s }\nschedule: { repositories: { cron: \"0 0 * * *\" } }\n";

    expect_error(
        &format!("{base}repositories:\n  - mode: provider\n    credential: github\ncredentials: {{ github: {{ apiKey: t }} }}\n"),
        "repositories[0].provider is required when mode is provider.",
    );
    expect_error(
        &format!("{base}repositories:\n  - mode: provider\n    provider: bitbucket\n    credential: github\ncredentials: {{ github: {{ apiKey: t }} }}\n"),
        "repositories[0].provider 'bitbucket' is not supported. Supported values: github, gitlab, forgejo.",
    );
    expect_error(
        &format!("{base}repositories:\n  - mode: provider\n    provider: github\n    credential: missing\ncredentials: {{ github: {{ apiKey: t }} }}\n"),
        "repositories[0].credential references unknown credential 'missing'.",
    );
    expect_error(
        &format!("{base}repositories:\n  - mode: provider\n    provider: github\n    credential: github\n    includeIssueArtifacts: true\ncredentials: {{ github: {{ apiKey: t }} }}\n"),
        "repositories[0].includeIssueArtifacts requires includeIssues.",
    );
}

#[test]
fn url_mode_disallows_provider_only_options() {
    let base = "storage: { endpoint: \"https://s3.example.com\", region: r, bucket: b, accessKeyId: a, secretAccessKey: s }\nschedule: { repositories: { cron: \"0 0 * * *\" } }\n";
    expect_error(
        &format!("{base}repositories:\n  - mode: url\n    url: https://example.com/x\n    provider: github\n"),
        "repositories[0].provider is not allowed when mode is url.",
    );
    expect_error(
        &format!("{base}repositories:\n  - mode: url\n    url: https://example.com/x\n    includeStarred: true\n"),
        "repositories[0].includeStarred is not allowed when mode is url.",
    );
    expect_error(
        &format!("{base}repositories:\n  - mode: url\n"),
        "repositories[0].url is required when mode is url.",
    );
}

#[test]
fn rejects_unsupported_mode_and_invalid_cron() {
    let base = "storage: { endpoint: \"https://s3.example.com\", region: r, bucket: b, accessKeyId: a, secretAccessKey: s }\n";
    expect_error(
        &format!("{base}schedule: {{ repositories: {{ cron: \"0 0 * * *\" }} }}\nrepositories:\n  - mode: mirror\n    url: https://x.example.com/y\n"),
        "repositories[0].mode 'mirror' is not supported. Supported values: provider, url.",
    );
    expect_error(
        &format!("{base}schedule: {{ repositories: {{ cron: \"not a cron\" }} }}\n"),
        "schedule.repositories.cron is invalid: must be a valid 5-field or 6-field cron expression.",
    );
}

#[test]
fn secret_env_placeholder_resolves_and_reports_unset() {
    // Unique var names avoid cross-test env races.
    let var = format!("GITBACKUP_TEST_SECRET_{}", std::process::id());
    std::env::set_var(&var, "resolved-key");
    let yaml = format!(
        "storage:\n  endpoint: https://s3.example.com\n  region: r\n  bucket: b\n  accessKeyId: \"${{{var}}}\"\n  secretAccessKey: s\nschedule: {{ repositories: {{ cron: \"0 0 * * *\" }} }}\n"
    );
    let (result, path) = load_yaml(&yaml);
    let _ = std::fs::remove_file(path);
    assert!(result.is_success(), "errors: {:?}", result.errors);
    assert_eq!(
        result.settings.unwrap().storage.access_key_id.as_deref(),
        Some("resolved-key")
    );
    std::env::remove_var(&var);

    expect_error(
        "storage:\n  endpoint: https://s3.example.com\n  region: r\n  bucket: b\n  accessKeyId: \"${GITBACKUP_DEFINITELY_UNSET_VAR}\"\n  secretAccessKey: s\nschedule: { repositories: { cron: \"0 0 * * *\" } }\n",
        "storage.accessKeyId references environment variable 'GITBACKUP_DEFINITELY_UNSET_VAR', which is not set.",
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn live_settings_hot_reloads_on_change() {
    let id = COUNTER.fetch_add(1, Ordering::Relaxed);
    let path =
        std::env::temp_dir().join(format!("gitbackup-live-{}-{id}.yaml", std::process::id()));

    let base = |level: &str| {
        format!(
            "logging:\n  logLevel: {level}\nstorage:\n  endpoint: https://s3.example.com\n  region: r\n  bucket: b\n  accessKeyId: a\n  secretAccessKey: s\nschedule:\n  repositories:\n    cron: \"0 0 * * *\"\n"
        )
    };

    std::fs::write(&path, base("info")).unwrap();
    let initial = config::load(&path);
    assert!(initial.is_success(), "errors: {:?}", initial.errors);

    let live = config::LiveSettings::new(&path, initial.settings.unwrap());
    live.start();
    assert_eq!(live.current().logging.log_level.as_deref(), Some("info"));

    // Change the file; the 2s poll should pick it up.
    std::fs::write(&path, base("debug")).unwrap();

    let mut reloaded = false;
    for _ in 0..30 {
        tokio::time::sleep(std::time::Duration::from_millis(250)).await;
        if live.current().logging.log_level.as_deref() == Some("debug") {
            reloaded = true;
            break;
        }
    }
    drop(live);
    let _ = std::fs::remove_file(path);
    assert!(reloaded, "settings did not hot-reload within the timeout");
}
