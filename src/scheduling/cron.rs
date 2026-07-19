//! Cron parsing ← `CronScheduleParser`.
//!
//! Accepts both 5-field (standard) and 6-field (leading seconds) expressions, matching the original's
//! `CronFormat.Standard` / `CronFormat.IncludeSeconds` fallthrough.

use croner::Cron;

/// Parses a cron expression, returning the compiled `Cron` or a parity error message. An empty
/// expression and an unparseable one produce the same messages the original emitted.
pub fn try_parse(expression: Option<&str>) -> Result<Cron, String> {
    let expression = expression.map(str::trim).filter(|e| !e.is_empty());
    let Some(expression) = expression else {
        return Err("cron expression is required.".to_string());
    };

    Cron::new(expression)
        .with_seconds_optional()
        .parse()
        .map_err(|_| "must be a valid 5-field or 6-field cron expression.".to_string())
}

/// The next occurrence strictly after now, evaluated in `tz` (local time), as a UTC instant. `None`
/// when the schedule can never fire again.
pub fn next_occurrence(cron: &Cron, tz: &chrono_tz::Tz) -> Option<chrono::DateTime<chrono::Utc>> {
    let now_local = chrono::Utc::now().with_timezone(tz);
    cron.find_next_occurrence(&now_local, false)
        .ok()
        .map(|occurrence| occurrence.with_timezone(&chrono::Utc))
}
