//! Logging ← `AppLogger`.
//!
//! A `tracing` subscriber with the original's console format — `[{ts} ({TZ})] [{LVL:u3}] {msg}` — a
//! DST-aware local-zone label driven by `TZ`, and a runtime-reloadable minimum level. Call sites use
//! the standard `tracing` macros; the level is steered in place (an atomic), so a settings reload never
//! races a rebuild.

use chrono::Utc;
use chrono_tz::Tz;
use std::fmt;
use std::io::Write as _;
use std::sync::atomic::{AtomicU8, Ordering};
use std::sync::OnceLock;
use tracing::{Event, Level, Subscriber};
use tracing_subscriber::filter::filter_fn;
use tracing_subscriber::fmt::format::Writer;
use tracing_subscriber::fmt::{FmtContext, FormatEvent, FormatFields};
use tracing_subscriber::layer::SubscriberExt;
use tracing_subscriber::registry::LookupSpan;
use tracing_subscriber::util::SubscriberInitExt;
use tracing_subscriber::Layer;

/// Configured log levels ← `AppLogLevel`. Numeric order is the threshold order (higher = more severe).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AppLogLevel {
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

pub const DEFAULT_LOG_LEVEL: &str = "info";

/// Insertion order matches the original's `LevelsByName`, so validation error messages list levels
/// in the same order.
pub const SUPPORTED_LOG_LEVELS: [&str; 4] = ["debug", "info", "warn", "error"];

static MIN_LEVEL: AtomicU8 = AtomicU8::new(AppLogLevel::Info as u8);

/// Parses a configured level name (case-insensitive, trimmed). `None` for an unrecognized value.
pub fn try_parse_log_level(value: Option<&str>) -> Option<AppLogLevel> {
    match value?.trim().to_ascii_lowercase().as_str() {
        "debug" => Some(AppLogLevel::Debug),
        "info" => Some(AppLogLevel::Info),
        "warn" => Some(AppLogLevel::Warn),
        "error" => Some(AppLogLevel::Error),
        _ => None,
    }
}

pub fn to_config_value(level: AppLogLevel) -> &'static str {
    match level {
        AppLogLevel::Debug => "debug",
        AppLogLevel::Info => "info",
        AppLogLevel::Warn => "warn",
        AppLogLevel::Error => "error",
    }
}

/// Sets the threshold in place — no subscriber rebuild, so concurrent writers never race.
pub fn set_min_level(level: AppLogLevel) {
    MIN_LEVEL.store(level as u8, Ordering::Relaxed);
}

/// Installs the global subscriber. Idempotent: a second call (e.g. across tests) is ignored.
pub fn init() {
    let filter = filter_fn(|metadata| level_enabled(*metadata.level()));
    let layer = tracing_subscriber::fmt::layer()
        .with_writer(std::io::stdout)
        .event_format(ConsoleFormat)
        .with_filter(filter);
    let _ = tracing_subscriber::registry().with(layer).try_init();
}

/// Flushes buffered output on shutdown ← `Log.CloseAndFlush`.
pub fn shutdown() {
    let _ = std::io::stdout().flush();
}

fn level_enabled(level: Level) -> bool {
    app_rank(level) >= MIN_LEVEL.load(Ordering::Relaxed)
}

// TRACE is folded into the debug tier (the original has no trace level).
fn app_rank(level: Level) -> u8 {
    match level {
        Level::ERROR => 3,
        Level::WARN => 2,
        Level::INFO => 1,
        Level::DEBUG | Level::TRACE => 0,
    }
}

fn level_u3(level: &Level) -> &'static str {
    match *level {
        Level::ERROR => "ERR",
        Level::WARN => "WRN",
        Level::INFO => "INF",
        Level::DEBUG => "DBG",
        Level::TRACE => "TRC",
    }
}

/// The local zone for timestamps, resolved once from `TZ` (UTC when unset/unparseable, matching the
/// container default). Used both for the timestamp conversion and the parenthesized abbreviation.
fn timezone() -> &'static Tz {
    static TZ: OnceLock<Tz> = OnceLock::new();
    TZ.get_or_init(|| {
        std::env::var("TZ")
            .ok()
            .and_then(|value| value.parse::<Tz>().ok())
            .unwrap_or(chrono_tz::UTC)
    })
}

struct ConsoleFormat;

impl<S, N> FormatEvent<S, N> for ConsoleFormat
where
    S: Subscriber + for<'a> LookupSpan<'a>,
    N: for<'a> FormatFields<'a> + 'static,
{
    fn format_event(
        &self,
        ctx: &FmtContext<'_, S, N>,
        mut writer: Writer<'_>,
        event: &Event<'_>,
    ) -> fmt::Result {
        let now = Utc::now().with_timezone(timezone());
        let metadata = event.metadata();
        write!(
            writer,
            "[{} ({})] [{}] ",
            now.format("%Y-%m-%d %H:%M:%S"),
            now.format("%Z"),
            level_u3(metadata.level()),
        )?;

        // The message and any structured fields; most call sites inline everything into the message.
        ctx.field_format().format_fields(writer.by_ref(), event)?;
        writeln!(writer)
    }
}
