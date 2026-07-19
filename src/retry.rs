//! Shared retry-delay computation ← `RetryDelay`.
//!
//! Honors a parsed `Retry-After` value (clamped to `max_delay` only when `cap_retry_after_to_max` is
//! set), otherwise a `2^(attempt-1)` backoff capped at `max_delay`, with up to 1s of jitter.

use rand::Rng;
use std::time::Duration;

pub fn resolve(
    retry_after: Option<Duration>,
    attempt: u32,
    max_delay: Duration,
    cap_retry_after_to_max: bool,
    jitter: bool,
) -> Duration {
    if let Some(delta) = retry_after {
        if delta > Duration::ZERO {
            return if cap_retry_after_to_max && delta > max_delay {
                max_delay
            } else {
                delta
            };
        }
    }

    let exponent = attempt.saturating_sub(1) as i32;
    let backoff_seconds = max_delay.as_secs_f64().min(2f64.powi(exponent));
    let backoff = Duration::from_secs_f64(backoff_seconds);

    if jitter {
        backoff + Duration::from_millis(rand::thread_rng().gen_range(0..1000))
    } else {
        backoff
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exponential_backoff_without_jitter() {
        let max = Duration::from_secs(30);
        assert_eq!(resolve(None, 1, max, true, false), Duration::from_secs(1));
        assert_eq!(resolve(None, 2, max, true, false), Duration::from_secs(2));
        assert_eq!(resolve(None, 3, max, true, false), Duration::from_secs(4));
        // Capped at max_delay.
        assert_eq!(resolve(None, 10, max, true, false), max);
    }

    #[test]
    fn retry_after_is_honored_and_capped_only_when_requested() {
        let max = Duration::from_secs(30);
        let long = Duration::from_secs(120);
        // Uncapped: the rate-limit's own Retry-After wins outright.
        assert_eq!(resolve(Some(long), 1, max, false, false), long);
        // Capped: a 5xx hint is clamped to max.
        assert_eq!(resolve(Some(long), 1, max, true, false), max);
        // A short Retry-After passes through either way.
        let short = Duration::from_secs(3);
        assert_eq!(resolve(Some(short), 1, max, true, false), short);
    }
}
