using Cronos;
using GitBackup.Configuration;
using GitBackup.Runtime;
using GitBackup.Services.Repositories;

namespace GitBackup.Services.Scheduling;

public sealed class ScheduledJobRunner : IDisposable
{
    private readonly Func<Settings> _getSettings;
    private readonly RepositorySyncService _repositorySyncService;
    private readonly RepositoryRetentionService _retentionService;
    private readonly SemaphoreSlim _retentionLock = new(1, 1);

    public ScheduledJobRunner(
        Func<Settings> getSettings,
        RepositorySyncService repositorySyncService,
        RepositoryRetentionService retentionService)
    {
        _getSettings = getSettings;
        _repositorySyncService = repositorySyncService;
        _retentionService = retentionService;
    }

    public async Task RunForeverAsync(CancellationToken cancellationToken)
    {
        AppLogger.Info("Starting scheduled repository job loop.");

        await RunScheduledLoopAsync(
            jobName: "repositories",
            getCronExpression: () => _getSettings().Schedule.Repositories.Cron,
            runJob: token => _repositorySyncService.RunAsync(_getSettings(), token),
            cancellationToken);
    }

    private async Task RunScheduledLoopAsync(
        string jobName,
        Func<string?> getCronExpression,
        Func<CancellationToken, Task> runJob,
        CancellationToken cancellationToken)
    {
        var scheduleLog = new ScheduleLogState();

        while (!cancellationToken.IsCancellationRequested)
        {
            var cronExpression = getCronExpression();

            var schedule = await ResolveScheduleAsync(jobName, cronExpression, scheduleLog, cancellationToken);
            if (schedule is null)
            {
                continue;
            }

            var nextOccurrence = ResolveNextOccurrence(jobName, schedule, cronExpression);
            if (nextOccurrence is null)
            {
                return;
            }

            var waitResult = await DelayUntilUtcAsync(
                jobName,
                nextOccurrence.Value,
                cronExpression,
                getCronExpression,
                cancellationToken);

            if (waitResult == DelayUntilUtcResult.Cancelled)
            {
                return;
            }

            if (waitResult == DelayUntilUtcResult.RescheduleRequested)
            {
                AppLogger.Info(
                    "{JobName}: schedule changed from '{PreviousCronExpression}' to '{CurrentCronExpression}'. Recomputing next run.",
                    jobName,
                    cronExpression,
                    getCronExpression());
                continue;
            }

            if (!await TryRunWithTimingAsync(jobName, runJob, cancellationToken))
            {
                return;
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await RunRetentionAsync(jobName, cancellationToken);
            }
        }
    }

    // Carries what the loop has already reported, so an unchanged or still-invalid schedule does not
    // repeat the same line on every pass. Nothing outside the logging decisions reads it.
    private sealed class ScheduleLogState
    {
        public string? LastAppliedCron { get; set; }

        public string? LastInvalidCron { get; set; }
    }

    /// <summary>
    /// Parses the configured schedule, reporting a change or an invalid expression only the first time
    /// each is seen. Returns null when the expression is unusable, having waited a beat so the caller can
    /// simply retry until the settings are fixed.
    /// </summary>
    private static async Task<CronExpression?> ResolveScheduleAsync(
        string jobName,
        string? cronExpression,
        ScheduleLogState log,
        CancellationToken cancellationToken)
    {
        AppLogger.Debug("{JobName}: evaluating cron expression '{CronExpression}'.", jobName, cronExpression);

        if (!CronScheduleParser.TryParse(cronExpression, out var schedule, out var parseError) || schedule is null)
        {
            if (!string.Equals(log.LastInvalidCron, cronExpression, StringComparison.Ordinal))
            {
                AppLogger.Warn(
                    "{JobName}: schedule '{CronExpression}' is invalid ({ParseError}). Waiting for configuration reload.",
                    jobName,
                    cronExpression,
                    parseError);
                log.LastInvalidCron = cronExpression;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return null;
        }

        log.LastInvalidCron = null;
        if (!string.Equals(log.LastAppliedCron, cronExpression, StringComparison.Ordinal))
        {
            AppLogger.Info("{JobName}: active schedule is '{CronExpression}'.", jobName, cronExpression);
            log.LastAppliedCron = cronExpression;
        }

        return schedule;
    }

    /// <summary>
    /// Reports when the job runs next, or null when the schedule can never fire again and the loop
    /// should stop.
    /// </summary>
    private static DateTimeOffset? ResolveNextOccurrence(string jobName, CronExpression schedule, string? cronExpression)
    {
        var now = DateTimeOffset.UtcNow;
        var nextOccurrence = schedule.GetNextOccurrence(now.AddMilliseconds(1), TimeZoneInfo.Local);

        if (nextOccurrence is null)
        {
            AppLogger.Error(
                "{JobName}: schedule '{CronExpression}' has no next occurrence. Stopping this job loop.",
                jobName,
                cronExpression);
            return null;
        }

        var secondsUntilNextRun = Math.Max(0L, (long)Math.Ceiling((nextOccurrence.Value - now).TotalSeconds));
        AppLogger.Info(
            "{JobName}: next run at {NextRunTimestamp} (in {NextRunDelay}).",
            jobName,
            AppLogger.FormatTimestamp(nextOccurrence.Value),
            DurationFormatter.FormatShort(secondsUntilNextRun));

        return nextOccurrence;
    }

    /// <summary>
    /// Runs the job inside its timing envelope. A failure is reported and swallowed so one bad run never
    /// stops the schedule; false means the run was cancelled and the loop should exit.
    /// </summary>
    private static async Task<bool> TryRunWithTimingAsync(
        string jobName,
        Func<CancellationToken, Task> runJob,
        CancellationToken cancellationToken)
    {
        var runStartedAt = DateTimeOffset.UtcNow;
        // The job service logs its own richer "started" line (with counts); keep only the timing
        // envelope here to avoid a duplicate start marker for the same event.
        AppLogger.Debug("{JobName}: run started.", jobName);

        try
        {
            await runJob(cancellationToken);
            AppLogger.Info(
                "{JobName}: run completed in {DurationSeconds:0.###} seconds.",
                jobName,
                (DateTimeOffset.UtcNow - runStartedAt).TotalSeconds);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            AppLogger.Error(
                exception,
                "{JobName}: run failed after {DurationSeconds:0.###} seconds. error={ErrorMessage}.",
                jobName,
                (DateTimeOffset.UtcNow - runStartedAt).TotalSeconds,
                exception.Message);
            return true;
        }
    }

    private static async Task<DelayUntilUtcResult> DelayUntilUtcAsync(
        string jobName,
        DateTimeOffset target,
        string? scheduledCronExpression,
        Func<string?> getCronExpression,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var currentCronExpression = getCronExpression();
            if (!string.Equals(currentCronExpression, scheduledCronExpression, StringComparison.Ordinal))
            {
                AppLogger.Debug(
                    "{JobName}: detected schedule change while waiting (old='{OldCronExpression}', new='{NewCronExpression}').",
                    jobName,
                    scheduledCronExpression,
                    currentCronExpression);
                return DelayUntilUtcResult.RescheduleRequested;
            }

            var remaining = target - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return DelayUntilUtcResult.TargetReached;
            }

            var delay = remaining > TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : remaining;

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return DelayUntilUtcResult.Cancelled;
            }
        }

        return DelayUntilUtcResult.Cancelled;
    }

    private async Task RunRetentionAsync(string triggeredBy, CancellationToken cancellationToken)
    {
        await _retentionLock.WaitAsync(cancellationToken);

        try
        {
            // RepositoryRetentionService logs its own "started"/"completed" lines with detail, so this
            // envelope only handles the failure/cancel cases below.
            await _retentionService.RunAsync(_getSettings(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Exit cleanly on shutdown.
            AppLogger.Warn("Retention cancelled because shutdown was requested.");
        }
        catch (Exception exception)
        {
            AppLogger.Error(
                exception,
                "Retention failed after the {TriggeredByJob} job run. error={ErrorMessage}.",
                triggeredBy,
                exception.Message);
        }
        finally
        {
            _retentionLock.Release();
        }
    }

    public void Dispose()
    {
        _retentionLock.Dispose();
    }

    private enum DelayUntilUtcResult
    {
        TargetReached,
        RescheduleRequested,
        Cancelled
    }
}
