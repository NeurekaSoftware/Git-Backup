using System.Runtime.InteropServices;
using GitBackup.Configuration;
using GitBackup.Configuration.Models;
using GitBackup.Runtime;
using GitBackup.Services.Git;
using GitBackup.Services.Providers;
using GitBackup.Services.Repositories;
using GitBackup.Services.Scheduling;
using GitBackup.Services.Storage;

namespace GitBackup;

class Program
{
    private const string ContainerBinPath = "/app/bin";
    private const string ContainerDataPath = "/app/data";

    static async Task<int> Main(string[] args)
    {
        try
        {
            BuildMetadata.LoadFromEnvironment();
            AppLogger.Info("Git Backup started. Version {Version} ({Commit}).", BuildMetadata.Version, BuildMetadata.Commit);

            var settingsPath = ResolveSettingsPath(args);
            AppLogger.Info("Using settings file {SettingsPath}.", settingsPath);

            var settingsLoader = new SettingsLoader();
            var settingsLoadResult = settingsLoader.Load(settingsPath);

            if (!settingsLoadResult.IsSuccess)
            {
                AppLogger.Error("Failed to load settings file {SettingsPath}.", settingsPath);
                foreach (var error in settingsLoadResult.Errors)
                {
                    AppLogger.Error("Settings validation error: {ValidationError}", error);
                }

                return 1;
            }

            var settings = settingsLoadResult.Settings!;
            ApplyLogLevel(settings.Logging.LogLevel);
            using var liveSettings = new LiveSettings(settingsPath, settings, settingsLoader);
            liveSettings.Start();

            AppLogger.Info(
                "Configuration loaded. repositories={RepositoryCount}, watcher={SettingsPath}.",
                settings.Repositories.Count,
                liveSettings.SettingsPath);

            var workingRoot = ResolveWorkingRoot();
            Directory.CreateDirectory(workingRoot);
            AppLogger.Info("Working directory ready: {WorkingRoot}", workingRoot);

            Func<StorageConfig, IObjectStorageService> objectStorageFactory =
                storage => new SimpleS3ObjectStorageService(storage);
            var gitRepositoryService = new GitCliRepositoryService();
            var providerFactory = new RepositoryProviderClientFactory(
            [
                new GitHubRepositoryProviderClient(),
                new GitLabRepositoryProviderClient(),
                new ForgejoRepositoryProviderClient()
            ]);

            var repositorySyncService = new RepositorySyncService(providerFactory, gitRepositoryService, objectStorageFactory, workingRoot);
            var retentionService = new RepositoryRetentionService(objectStorageFactory);
            var scheduledJobRunner = new ScheduledJobRunner(() => liveSettings.Current, repositorySyncService, retentionService);

            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
                AppLogger.Warn("Shutdown requested by Ctrl+C.");
            };

            // Containers are stopped with SIGTERM (docker stop, orchestrator redeploy), not Ctrl+C.
            // Handle it so an in-flight clone or upload is cancelled cleanly and logs are flushed,
            // instead of being hard-killed when the stop grace period expires.
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
                AppLogger.Warn("Shutdown requested by SIGTERM.");
            });

            AppLogger.Info("Scheduler is running. Press Ctrl+C to stop.");

            try
            {
                await scheduledJobRunner.RunForeverAsync(shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                // Graceful shutdown.
            }

            AppLogger.Info("Scheduler stopped.");
            return 0;
        }
        finally
        {
            AppLogger.Shutdown();
        }
    }

    private static void ApplyLogLevel(string? configuredLogLevel)
    {
        if (!AppLogger.TryParseLogLevel(configuredLogLevel, out var parsedLevel))
        {
            AppLogger.SetMinimumLevel(AppLogLevel.Info);
            AppLogger.Warn(
                "Invalid logging.logLevel value {ConfiguredLogLevel}. Falling back to {FallbackLevel}.",
                configuredLogLevel,
                AppLogger.DefaultLogLevel);
            return;
        }

        AppLogger.SetMinimumLevel(parsedLevel);
        AppLogger.Info("Active log level: {LogLevel}", AppLogger.ToConfigValue(parsedLevel));
    }

    private static string ResolveSettingsPath(string[] args)
    {
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            return args[0];
        }

        var defaultPathCandidates = GetDefaultSettingsPathCandidates();

        foreach (var candidate in defaultPathCandidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return defaultPathCandidates[0];
    }

    private static string ResolveWorkingRoot()
    {
        var configured = Environment.GetEnvironmentVariable("GITBACKUP_WORKING_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        // In a container, keep the git mirrors under the persisted data directory so the incremental
        // fetch cache survives restarts and image updates instead of being fully re-cloned every run.
        // Outside a container, fall back to a temp directory.
        return IsRunningInContainer()
            ? ContainerDataPath
            : Path.Combine(Path.GetTempPath(), ".git-backup");
    }

    private static string[] GetDefaultSettingsPathCandidates()
    {
        if (IsRunningInContainer())
        {
            return
            [
                Path.Combine(ContainerBinPath, "settings.yaml"),
                Path.Combine(ContainerDataPath, "settings.yaml"),
                Path.Combine(Environment.CurrentDirectory, "settings.yaml")
            ];
        }

        return
        [
            Path.Combine(Environment.CurrentDirectory, "settings.yaml")
        ];
    }

    private static bool IsRunningInContainer()
    {
        var dotnetContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        if (string.Equals(dotnetContainer, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return File.Exists("/.dockerenv");
    }
}
