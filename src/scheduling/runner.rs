//! Forever-loop scheduler ← `ScheduledJobRunner`.
//!
//! One loop drives the repositories job: resolve the live cron, compute the next occurrence in local
//! time, then sleep in 1-second slices — re-reading the live cron each tick so a hot-reloaded schedule
//! reschedules mid-wait — run the job, then retention. A job failure is logged and swallowed so one bad
//! run never stops the schedule; cancellation exits cleanly.

use super::cron;
use crate::config::LiveSettings;
use crate::repositories::{RepositoryRetentionService, RepositorySyncService};
use crate::runtime::{duration, logger};
use chrono::Utc;
use std::sync::Arc;
use std::time::{Duration, Instant};
use tokio_util::sync::CancellationToken;

const JOB_NAME: &str = "repositories";

pub struct ScheduledJobRunner {
    live: Arc<LiveSettings>,
    sync: Arc<RepositorySyncService>,
    retention: Arc<RepositoryRetentionService>,
}

enum DelayResult {
    TargetReached,
    RescheduleRequested,
    Cancelled,
}

impl ScheduledJobRunner {
    pub fn new(
        live: Arc<LiveSettings>,
        sync: Arc<RepositorySyncService>,
        retention: Arc<RepositoryRetentionService>,
    ) -> Self {
        Self {
            live,
            sync,
            retention,
        }
    }

    pub async fn run_forever(&self, cancel: &CancellationToken) {
        tracing::info!("Starting scheduled repository job loop.");
        self.run_scheduled_loop(cancel).await;
    }

    async fn run_scheduled_loop(&self, cancel: &CancellationToken) {
        let mut last_applied: Option<String> = None;
        let mut last_invalid: Option<String> = None;

        while !cancel.is_cancelled() {
            let cron_expression = self.current_cron();

            let schedule = match cron::try_parse(cron_expression.as_deref()) {
                Ok(schedule) => {
                    last_invalid = None;
                    if last_applied.as_deref() != cron_expression.as_deref() {
                        tracing::info!(
                            "{JOB_NAME}: active schedule is '{}'.",
                            cron_expression.as_deref().unwrap_or_default()
                        );
                        last_applied = cron_expression.clone();
                    }
                    schedule
                }
                Err(parse_error) => {
                    if last_invalid != cron_expression {
                        tracing::warn!(
                            "{JOB_NAME}: schedule '{}' is invalid ({parse_error}). Waiting for configuration reload.",
                            cron_expression.as_deref().unwrap_or_default()
                        );
                        last_invalid = cron_expression.clone();
                    }
                    if sleep_or_cancel(Duration::from_secs(1), cancel)
                        .await
                        .is_err()
                    {
                        return;
                    }
                    continue;
                }
            };

            let now = Utc::now();
            let Some(next) = cron::next_occurrence(&schedule, &logger::local_timezone()) else {
                tracing::error!(
                    "{JOB_NAME}: schedule '{}' has no next occurrence. Stopping this job loop.",
                    cron_expression.as_deref().unwrap_or_default()
                );
                return;
            };
            let seconds_until = (next - now).num_seconds().max(0);
            tracing::info!(
                "{JOB_NAME}: next run at {} (in {}).",
                logger::format_timestamp(next),
                duration::format_short(seconds_until)
            );

            match self
                .delay_until(next, cron_expression.as_deref(), cancel)
                .await
            {
                DelayResult::Cancelled => return,
                DelayResult::RescheduleRequested => {
                    tracing::info!(
                        "{JOB_NAME}: schedule changed from '{}' to '{}'. Recomputing next run.",
                        cron_expression.as_deref().unwrap_or_default(),
                        self.current_cron().as_deref().unwrap_or_default()
                    );
                    continue;
                }
                DelayResult::TargetReached => {}
            }

            if !self.try_run_with_timing(cancel).await {
                return;
            }
            if !cancel.is_cancelled() {
                self.run_retention(cancel).await;
            }
        }
    }

    fn current_cron(&self) -> Option<String> {
        self.live.current().schedule.repositories.cron.clone()
    }

    /// Sleeps until `target`, in 1s slices, returning early if the live cron changed or we were cancelled.
    async fn delay_until(
        &self,
        target: chrono::DateTime<Utc>,
        scheduled_cron: Option<&str>,
        cancel: &CancellationToken,
    ) -> DelayResult {
        while !cancel.is_cancelled() {
            if self.current_cron().as_deref() != scheduled_cron {
                return DelayResult::RescheduleRequested;
            }

            let remaining = target - Utc::now();
            if remaining <= chrono::Duration::zero() {
                return DelayResult::TargetReached;
            }

            let slice = remaining.min(chrono::Duration::seconds(1));
            let slice = slice.to_std().unwrap_or(Duration::from_secs(1));
            if sleep_or_cancel(slice, cancel).await.is_err() {
                return DelayResult::Cancelled;
            }
        }
        DelayResult::Cancelled
    }

    /// Runs the sync job in its timing envelope. Returns false only when cancelled (the loop should exit);
    /// a genuine failure is logged and swallowed so the schedule survives.
    async fn try_run_with_timing(&self, cancel: &CancellationToken) -> bool {
        let started = Instant::now();
        tracing::debug!("{JOB_NAME}: run started.");

        let settings = self.live.current();
        match self.sync.run(&settings, cancel).await {
            _ if cancel.is_cancelled() => false,
            Ok(()) => {
                tracing::info!(
                    "{JOB_NAME}: run completed in {:.3} seconds.",
                    started.elapsed().as_secs_f64()
                );
                true
            }
            Err(error) => {
                tracing::error!(
                    "{JOB_NAME}: run failed after {:.3} seconds. error={error}.",
                    started.elapsed().as_secs_f64()
                );
                true
            }
        }
    }

    async fn run_retention(&self, cancel: &CancellationToken) {
        // A single job loop drives retention sequentially, so no lock is needed here.
        let settings = self.live.current();
        if let Err(error) = self.retention.run(&settings, cancel).await {
            if cancel.is_cancelled() {
                tracing::warn!("Retention cancelled because shutdown was requested.");
            } else {
                tracing::error!("Retention failed after the {JOB_NAME} job run. error={error}.");
            }
        }
    }
}

/// Sleeps for `delay`, returning `Err(())` if cancelled first.
async fn sleep_or_cancel(delay: Duration, cancel: &CancellationToken) -> Result<(), ()> {
    tokio::select! {
        _ = cancel.cancelled() => Err(()),
        _ = tokio::time::sleep(delay) => Ok(()),
    }
}
