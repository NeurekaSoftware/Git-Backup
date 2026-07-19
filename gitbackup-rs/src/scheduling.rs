//! Scheduling ← `Services/Scheduling/`.
//!
//! Cron parsing (5- and 6-field) and the forever-loop runner: next occurrence in local time, 1s-slice
//! sleeps that re-read the live cron for mid-wait rescheduling, failures swallowed. Ported in P9.
