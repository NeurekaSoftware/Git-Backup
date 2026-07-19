//! Scheduling ← `Services/Scheduling/`.
//!
//! Cron parsing (5- and 6-field) and the forever-loop runner. The runner lands in phase P9; `cron` is
//! brought forward here because config validation parses the schedule expression.

pub mod cron;
