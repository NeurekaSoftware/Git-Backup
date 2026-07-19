//! Secret resolution ← `SecretResolver`.
//!
//! A secret may be given literally, as a `${ENV_VAR}` placeholder, or through a companion `*File` key
//! naming a file to read (a Docker/Kubernetes secret mount). Keeps long-lived tokens out of the
//! settings file while leaving a literal value working.

use regex::Regex;
use std::fs;
use std::sync::LazyLock;

// Anchored: only a value that is *entirely* a placeholder is substituted, so a literal secret that
// merely contains "${" is passed through untouched.
static ENV_PLACEHOLDER: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^\$\{([A-Za-z_][A-Za-z0-9_]*)\}$").unwrap());

/// Resolves a secret from `file_path` when given, otherwise from `value` (expanding a `${ENV_VAR}`
/// placeholder). Returns `None` and pushes an error against `config_path` when the source is unusable —
/// an unset variable or unreadable file is a misconfiguration worth failing on.
pub fn resolve(
    value: Option<&str>,
    file_path: Option<&str>,
    config_path: &str,
    errors: &mut Vec<String>,
) -> Option<String> {
    let value = value.filter(|v| !v.trim().is_empty());
    let file_path = file_path.filter(|v| !v.trim().is_empty());

    if let Some(file_path) = file_path {
        if value.is_some() {
            errors.push(format!(
                "{config_path} and {config_path}File are both set. Use one or the other."
            ));
            return None;
        }

        return match fs::read_to_string(file_path) {
            Ok(contents) => Some(contents.trim().to_string()),
            Err(error) => {
                errors.push(format!(
                    "{config_path}File '{file_path}' could not be read: {error}"
                ));
                None
            }
        };
    }

    let value = value?;

    let trimmed = value.trim();
    let Some(captures) = ENV_PLACEHOLDER.captures(trimmed) else {
        return Some(value.to_string());
    };

    let variable = &captures[1];
    match std::env::var(variable) {
        Ok(resolved) if !resolved.trim().is_empty() => Some(resolved),
        _ => {
            errors.push(format!(
                "{config_path} references environment variable '{variable}', which is not set."
            ));
            None
        }
    }
}
