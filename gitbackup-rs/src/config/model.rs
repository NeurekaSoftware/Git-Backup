//! Settings model ← `Configuration/` POCOs. Field names are snake_case; `rename_all = "camelCase"`
//! maps them onto the YAML keys, with `url` the sole explicit override. `deny_unknown_fields` makes a
//! typo'd key an error rather than a silently-ignored setting.

use super::one_or_many;
use serde::Deserialize;
use std::collections::HashMap;

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Settings {
    #[serde(default)]
    pub logging: LoggingConfig,
    #[serde(default)]
    pub storage: StorageConfig,
    #[serde(default)]
    pub credentials: HashMap<String, CredentialConfig>,
    /// Elements are nullable: a blank list item deserializes to `None` and is reported as required.
    #[serde(default)]
    pub repositories: Vec<Option<RepositoryJobConfig>>,
    #[serde(default)]
    pub schedule: ScheduleConfig,
    #[serde(default)]
    pub concurrency: ConcurrencyConfig,
}

impl Settings {
    /// Case-insensitive credential lookup ← the `OrdinalIgnoreCase` credentials dictionary.
    pub fn credential(&self, name: &str) -> Option<&CredentialConfig> {
        self.credentials
            .iter()
            .find(|(key, _)| key.eq_ignore_ascii_case(name))
            .map(|(_, value)| value)
    }

    pub fn has_credential(&self, name: &str) -> bool {
        self.credential(name).is_some()
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct LoggingConfig {
    pub log_level: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct StorageConfig {
    pub endpoint: Option<String>,
    pub region: Option<String>,
    pub access_key_id: Option<String>,
    pub access_key_id_file: Option<String>,
    pub secret_access_key: Option<String>,
    pub secret_access_key_file: Option<String>,
    pub force_path_style: Option<bool>,
    pub payload_signature_mode: Option<String>,
    pub bucket: Option<String>,
    pub retention: Option<i64>,
    pub retention_minimum: Option<i64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CredentialConfig {
    pub username: Option<String>,
    pub api_key: Option<String>,
    pub api_key_file: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RepositoryJobConfig {
    pub mode: Option<String>,
    pub provider: Option<String>,
    #[serde(
        rename = "url",
        default,
        deserialize_with = "one_or_many::string_or_seq_opt"
    )]
    pub urls: Option<Vec<String>>,
    pub credential: Option<String>,
    pub base_url: Option<String>,
    pub lfs: Option<bool>,
    pub cache: Option<bool>,
    pub enabled: Option<bool>,
    pub include_starred: Option<bool>,
    pub include_snippets: Option<bool>,
    pub include_issues: Option<bool>,
    pub include_issue_artifacts: Option<bool>,
    pub include_merge_requests: Option<bool>,
    pub include_merge_requests_artifacts: Option<bool>,
    pub include_releases: Option<bool>,
    pub include_release_artifacts: Option<bool>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ScheduleConfig {
    #[serde(default)]
    pub repositories: JobScheduleConfig,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct JobScheduleConfig {
    pub cron: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ConcurrencyConfig {
    pub repositories: Option<i64>,
    pub metadata: Option<i64>,
}
