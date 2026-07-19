//! Settings loading + validation ← `SettingsLoader`.
//!
//! Pipeline (order is load-bearing): deprecated-key rejection on the raw document → typed
//! deserialization (unknown keys rejected) → raw-enum rejection (before defaults hide a typo) →
//! normalize → secret resolution (before the required-value checks) → validation. Every error message
//! is reproduced verbatim so existing configs fail and pass identically.

use super::enums::{modes, payload_signature, providers};
use super::model::{RepositoryJobConfig, Settings};
use super::secrets;
use crate::paths::git_url;
use crate::runtime::logger;
use crate::scheduling::cron;
use std::path::Path;

/// Outcome of a load ← `SettingsLoadResult`.
pub struct SettingsLoadResult {
    pub settings: Option<Settings>,
    pub errors: Vec<String>,
}

impl SettingsLoadResult {
    fn success(settings: Settings) -> Self {
        Self {
            settings: Some(settings),
            errors: Vec::new(),
        }
    }

    fn failure(errors: Vec<String>) -> Self {
        Self {
            settings: None,
            errors,
        }
    }

    pub fn is_success(&self) -> bool {
        self.settings.is_some() && self.errors.is_empty()
    }
}

pub fn load(path: &Path) -> SettingsLoadResult {
    if path.as_os_str().is_empty() {
        return SettingsLoadResult::failure(vec!["settings path is required.".to_string()]);
    }

    if !path.exists() {
        return SettingsLoadResult::failure(vec![format!(
            "settings file not found: '{}'",
            path.display()
        )]);
    }

    let yaml = match std::fs::read_to_string(path) {
        Ok(contents) => contents,
        Err(error) => {
            return SettingsLoadResult::failure(vec![format!(
                "failed to load settings '{}': {error}",
                path.display()
            )]);
        }
    };

    // Checked against the raw document before deserialization: a file still using a deprecated key gets
    // the migration message rather than an unrecognized-key error.
    let deprecated_key_errors = validate_deprecated_keys(&yaml);
    if !deprecated_key_errors.is_empty() {
        return SettingsLoadResult::failure(deprecated_key_errors);
    }

    let mut settings = if yaml.trim().is_empty() {
        // An empty document is the default configuration, matching `Deserialize(...) ?? new Settings()`.
        Settings::default()
    } else {
        match serde_yaml_ng::from_str::<Settings>(&yaml) {
            Ok(settings) => settings,
            Err(error) => {
                return SettingsLoadResult::failure(vec![format!(
                    "YAML parse error in '{}': {error}",
                    path.display()
                )]);
            }
        }
    };

    // Reject a present-but-unrecognized enum value before Normalize coerces it to a default.
    let raw_value_errors = validate_raw_enums(&settings);
    if !raw_value_errors.is_empty() {
        return SettingsLoadResult::failure(raw_value_errors);
    }

    normalize(&mut settings);

    // Resolve secrets before validation, so the required-value checks see the value that will be used.
    let mut secret_errors = Vec::new();
    resolve_secrets(&mut settings, &mut secret_errors);
    if !secret_errors.is_empty() {
        return SettingsLoadResult::failure(secret_errors);
    }

    let errors = validate(&settings);
    if errors.is_empty() {
        SettingsLoadResult::success(settings)
    } else {
        SettingsLoadResult::failure(errors)
    }
}

fn resolve_secrets(settings: &mut Settings, errors: &mut Vec<String>) {
    settings.storage.access_key_id = secrets::resolve(
        settings.storage.access_key_id.as_deref(),
        settings.storage.access_key_id_file.as_deref(),
        "storage.accessKeyId",
        errors,
    );
    settings.storage.secret_access_key = secrets::resolve(
        settings.storage.secret_access_key.as_deref(),
        settings.storage.secret_access_key_file.as_deref(),
        "storage.secretAccessKey",
        errors,
    );

    for (name, credential) in settings.credentials.iter_mut() {
        credential.api_key = secrets::resolve(
            credential.api_key.as_deref(),
            credential.api_key_file.as_deref(),
            &format!("credentials.{name}.apiKey"),
            errors,
        );
    }
}

fn normalize(settings: &mut Settings) {
    settings.logging.log_level = Some(normalize_log_level(settings.logging.log_level.as_deref()));

    settings.storage.force_path_style.get_or_insert(false);
    settings.storage.payload_signature_mode = Some(payload_signature::normalize(
        settings.storage.payload_signature_mode.as_deref(),
    ));
    settings.storage.retention_minimum.get_or_insert(1);

    settings.concurrency.repositories.get_or_insert(1);
    settings.concurrency.metadata.get_or_insert(1);

    for repository in settings.repositories.iter_mut().flatten() {
        repository.enabled.get_or_insert(true);
        repository.lfs.get_or_insert(true);
        repository.cache.get_or_insert(true);
        repository.include_starred.get_or_insert(false);
        repository.include_snippets.get_or_insert(false);
        repository.include_issues.get_or_insert(false);
        repository.include_issue_artifacts.get_or_insert(false);
        repository.include_merge_requests.get_or_insert(false);
        repository
            .include_merge_requests_artifacts
            .get_or_insert(false);
        repository.include_releases.get_or_insert(false);
        repository.include_release_artifacts.get_or_insert(false);
        repository.mode = repository.mode.as_deref().map(|m| m.trim().to_lowercase());
        repository.provider = repository
            .provider
            .as_deref()
            .map(|p| p.trim().to_lowercase());
        repository.urls = repository.urls.take().map(|urls| {
            urls.into_iter()
                .map(|url| url.trim().to_string())
                .filter(|url| !url.is_empty())
                .collect()
        });
    }
}

fn normalize_log_level(configured: Option<&str>) -> String {
    match logger::try_parse_log_level(configured) {
        Some(level) => logger::to_config_value(level).to_string(),
        None => logger::DEFAULT_LOG_LEVEL.to_string(),
    }
}

fn validate(settings: &Settings) -> Vec<String> {
    let mut errors = Vec::new();
    validate_storage(settings, &mut errors);
    validate_repositories(settings, &mut errors);
    validate_schedule(settings, &mut errors);
    validate_concurrency(settings, &mut errors);
    errors
}

fn validate_raw_enums(settings: &Settings) -> Vec<String> {
    let mut errors = Vec::new();

    if let Some(log_level) = non_blank(settings.logging.log_level.as_deref()) {
        if logger::try_parse_log_level(Some(log_level)).is_none() {
            errors.push(format!(
                "logging.logLevel '{log_level}' is invalid. Supported values: {}.",
                logger::SUPPORTED_LOG_LEVELS.join(", ")
            ));
        }
    }

    if let Some(mode) = non_blank(settings.storage.payload_signature_mode.as_deref()) {
        if !payload_signature::is_supported(mode) {
            errors.push(format!(
                "storage.payloadSignatureMode '{mode}' is invalid. Supported values: {}.",
                payload_signature::SUPPORTED.join(", ")
            ));
        }
    }

    errors
}

fn validate_concurrency(settings: &Settings, errors: &mut Vec<String>) {
    if settings.concurrency.repositories.unwrap_or(1) < 1 {
        errors.push("concurrency.repositories must be 1 or greater.".to_string());
    }
    if settings.concurrency.metadata.unwrap_or(1) < 1 {
        errors.push("concurrency.metadata must be 1 or greater.".to_string());
    }
}

fn validate_storage(settings: &Settings, errors: &mut Vec<String>) {
    match non_blank(settings.storage.endpoint.as_deref()) {
        None => errors.push("storage.endpoint is required.".to_string()),
        Some(endpoint) => match git_url::try_create_http_url(Some(endpoint)) {
            None => {
                errors.push("storage.endpoint must be an absolute http or https URL.".to_string())
            }
            Some(url) if url.scheme() == "http" && !git_url::is_loopback(&url) => {
                // Never put credentials or backup bytes on the wire in the clear; loopback stays allowed.
                errors.push("storage.endpoint must use https for a non-loopback host.".to_string());
            }
            Some(_) => {}
        },
    }

    if non_blank(settings.storage.region.as_deref()).is_none() {
        errors.push("storage.region is required.".to_string());
    }
    if non_blank(settings.storage.access_key_id.as_deref()).is_none() {
        errors.push("storage.accessKeyId is required.".to_string());
    }
    if non_blank(settings.storage.secret_access_key.as_deref()).is_none() {
        errors.push("storage.secretAccessKey is required.".to_string());
    }
    if non_blank(settings.storage.bucket.as_deref()).is_none() {
        errors.push("storage.bucket is required.".to_string());
    }
    if settings.storage.retention_minimum.unwrap_or(1) < 0 {
        errors.push("storage.retentionMinimum must be 0 or greater.".to_string());
    }
}

fn validate_repositories(settings: &Settings, errors: &mut Vec<String>) {
    for (i, repository) in settings.repositories.iter().enumerate() {
        let Some(repository) = repository else {
            errors.push(format!("repositories[{i}] is required."));
            continue;
        };

        let Some(mode) = non_blank(repository.mode.as_deref()) else {
            errors.push(format!("repositories[{i}].mode is required."));
            continue;
        };

        if mode.eq_ignore_ascii_case(modes::PROVIDER) {
            validate_provider_repository(settings, repository, i, errors);
        } else if mode.eq_ignore_ascii_case(modes::URL) {
            validate_url_repository(settings, repository, i, errors);
        } else {
            errors.push(format!(
                "repositories[{i}].mode '{mode}' is not supported. Supported values: {}.",
                modes::SUPPORTED.join(", ")
            ));
        }
    }
}

fn validate_provider_repository(
    settings: &Settings,
    repository: &RepositoryJobConfig,
    index: usize,
    errors: &mut Vec<String>,
) {
    match non_blank(repository.provider.as_deref()) {
        None => errors.push(format!(
            "repositories[{index}].provider is required when mode is provider."
        )),
        Some(provider) if !providers::is_supported(provider) => errors.push(format!(
            "repositories[{index}].provider '{provider}' is not supported. Supported values: {}.",
            providers::SUPPORTED.join(", ")
        )),
        Some(_) => {}
    }

    match non_blank(repository.credential.as_deref()) {
        None => errors.push(format!(
            "repositories[{index}].credential is required when mode is provider."
        )),
        Some(credential) if !settings.has_credential(credential) => errors.push(format!(
            "repositories[{index}].credential references unknown credential '{credential}'."
        )),
        Some(_) => {}
    }

    if repository
        .urls
        .as_ref()
        .is_some_and(|urls| !urls.is_empty())
    {
        errors.push(format!(
            "repositories[{index}].url is not allowed when mode is provider."
        ));
    }

    if let Some(base_url) = non_blank(repository.base_url.as_deref()) {
        if git_url::try_create_http_url(Some(base_url)).is_none() {
            errors.push(format!(
                "repositories[{index}].baseUrl must be an absolute http or https URL."
            ));
        }
    }

    if repository.include_issue_artifacts == Some(true) && repository.include_issues != Some(true) {
        errors.push(format!(
            "repositories[{index}].includeIssueArtifacts requires includeIssues."
        ));
    }
    if repository.include_merge_requests_artifacts == Some(true)
        && repository.include_merge_requests != Some(true)
    {
        errors.push(format!(
            "repositories[{index}].includeMergeRequestsArtifacts requires includeMergeRequests."
        ));
    }
    if repository.include_release_artifacts == Some(true)
        && repository.include_releases != Some(true)
    {
        errors.push(format!(
            "repositories[{index}].includeReleaseArtifacts requires includeReleases."
        ));
    }
}

fn validate_url_repository(
    settings: &Settings,
    repository: &RepositoryJobConfig,
    index: usize,
    errors: &mut Vec<String>,
) {
    match repository.urls.as_ref() {
        Some(urls) if !urls.is_empty() => {
            for (j, url) in urls.iter().enumerate() {
                if git_url::try_create_http_url(Some(url)).is_none() {
                    errors.push(format!(
                        "repositories[{index}].url[{j}] must be an absolute http or https URL."
                    ));
                }
            }
        }
        _ => errors.push(format!(
            "repositories[{index}].url is required when mode is url."
        )),
    }

    if non_blank(repository.provider.as_deref()).is_some() {
        errors.push(format!(
            "repositories[{index}].provider is not allowed when mode is url."
        ));
    }

    if non_blank(repository.base_url.as_deref()).is_some() {
        errors.push(format!(
            "repositories[{index}].baseUrl is not allowed when mode is url."
        ));
    }

    // Every include* flag is provider-only. One table drives the checks so a new flag needs one row.
    let url_disallowed_flags = [
        ("includeStarred", repository.include_starred),
        ("includeSnippets", repository.include_snippets),
        ("includeIssues", repository.include_issues),
        ("includeIssueArtifacts", repository.include_issue_artifacts),
        ("includeMergeRequests", repository.include_merge_requests),
        (
            "includeMergeRequestsArtifacts",
            repository.include_merge_requests_artifacts,
        ),
        ("includeReleases", repository.include_releases),
        (
            "includeReleaseArtifacts",
            repository.include_release_artifacts,
        ),
    ];
    for (name, value) in url_disallowed_flags {
        if value == Some(true) {
            errors.push(format!(
                "repositories[{index}].{name} is not allowed when mode is url."
            ));
        }
    }

    let Some(credential) = non_blank(repository.credential.as_deref()) else {
        return;
    };
    if !settings.has_credential(credential) {
        errors.push(format!(
            "repositories[{index}].credential references unknown credential '{credential}'."
        ));
    }
}

fn validate_schedule(settings: &Settings, errors: &mut Vec<String>) {
    if let Err(parse_error) = cron::try_parse(settings.schedule.repositories.cron.as_deref()) {
        errors.push(format!(
            "schedule.repositories.cron is invalid: {parse_error}"
        ));
    }
}

/// Trims and returns the value only when it is non-empty ← the pervasive `IsNullOrWhiteSpace` guard.
fn non_blank(value: Option<&str>) -> Option<&str> {
    value.map(str::trim).filter(|v| !v.is_empty())
}

fn validate_deprecated_keys(yaml: &str) -> Vec<String> {
    let mut errors = Vec::new();
    if yaml.trim().is_empty() {
        return errors;
    }

    // A parse failure here is left to the main deserialization path to report.
    let Ok(root) = serde_yaml_ng::from_str::<serde_yaml_ng::Value>(yaml) else {
        return errors;
    };
    let Some(root) = root.as_mapping() else {
        return errors;
    };

    if mapping_contains_key(root, "backups") {
        errors.push(
            "backups is no longer supported. Use repositories entries with mode: provider."
                .to_string(),
        );
    }
    if mapping_contains_key(root, "mirrors") {
        errors.push(
            "mirrors is no longer supported. Use repositories entries with mode: url.".to_string(),
        );
    }

    if let Some(schedule) = mapping_get_ci(root, "schedule").and_then(|node| node.as_mapping()) {
        if mapping_contains_key(schedule, "backups") {
            errors.push(
                "schedule.backups is no longer supported. Use schedule.repositories.cron."
                    .to_string(),
            );
        }
        if mapping_contains_key(schedule, "mirrors") {
            errors.push(
                "schedule.mirrors is no longer supported. Use schedule.repositories.cron."
                    .to_string(),
            );
        }
    }

    errors
}

fn mapping_contains_key(mapping: &serde_yaml_ng::Mapping, key: &str) -> bool {
    mapping
        .keys()
        .filter_map(|node| node.as_str())
        .any(|candidate| candidate.eq_ignore_ascii_case(key))
}

fn mapping_get_ci<'a>(
    mapping: &'a serde_yaml_ng::Mapping,
    key: &str,
) -> Option<&'a serde_yaml_ng::Value> {
    mapping
        .iter()
        .find(|(node, _)| {
            node.as_str()
                .is_some_and(|candidate| candidate.eq_ignore_ascii_case(key))
        })
        .map(|(_, value)| value)
}
