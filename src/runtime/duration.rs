//! Duration formatting ← `DurationFormatter`.
//!
//! Fixed unit sizes keep formatting deterministic for a raw second count (e.g. `90061` → "1d 1h 1m 1s").

struct Unit {
    seconds: i64,
    short_name: &'static str,
}

const UNITS: [Unit; 7] = [
    Unit {
        seconds: 365 * 24 * 60 * 60,
        short_name: "y",
    },
    Unit {
        seconds: 30 * 24 * 60 * 60,
        short_name: "mo",
    },
    Unit {
        seconds: 7 * 24 * 60 * 60,
        short_name: "w",
    },
    Unit {
        seconds: 24 * 60 * 60,
        short_name: "d",
    },
    Unit {
        seconds: 60 * 60,
        short_name: "h",
    },
    Unit {
        seconds: 60,
        short_name: "m",
    },
    Unit {
        seconds: 1,
        short_name: "s",
    },
];

pub fn format_short(total_seconds: i64) -> String {
    let mut remaining = total_seconds.max(0);
    let mut parts: Vec<String> = Vec::new();

    for unit in &UNITS {
        if remaining < unit.seconds {
            continue;
        }
        let value = remaining / unit.seconds;
        parts.push(format!("{value}{}", unit.short_name));
        remaining -= value * unit.seconds;
    }

    if parts.is_empty() {
        parts.push(format!("0{}", UNITS[UNITS.len() - 1].short_name));
    }

    parts.join(" ")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn zero_renders_as_seconds() {
        assert_eq!(format_short(0), "0s");
    }

    #[test]
    fn negative_clamps_to_zero() {
        assert_eq!(format_short(-5), "0s");
    }

    #[test]
    fn decomposes_into_units() {
        assert_eq!(format_short(90_061), "1d 1h 1m 1s");
        assert_eq!(format_short(60), "1m");
        assert_eq!(format_short(3600), "1h");
    }
}
