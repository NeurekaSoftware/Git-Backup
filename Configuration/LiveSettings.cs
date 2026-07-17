using System.Security.Cryptography;
using GitBackup.Runtime;

namespace GitBackup.Configuration;

public sealed class LiveSettings : IDisposable
{
    // Polling, not FileSystemWatcher, because the settings file is bind-mounted into the container. A
    // single-file bind mount pins the container to the original inode, so a host editor that saves by
    // writing a temp file and renaming it over the original raises no inotify event and never changes the
    // content the container sees. Re-reading the file on an interval detects the change wherever the mount
    // does surface it (a directory mount, or an in-place edit), on any host and any editor.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly SettingsLoader _loader;
    private readonly object _sync = new();
    private readonly string _settingsPath;
    private Settings _current;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private byte[]? _lastContentHash;
    private bool _disposed;

    public LiveSettings(string settingsPath, Settings initialSettings, SettingsLoader? loader = null)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            throw new ArgumentException("settings path is required.", nameof(settingsPath));
        }

        _settingsPath = System.IO.Path.GetFullPath(settingsPath);
        _current = initialSettings ?? throw new ArgumentNullException(nameof(initialSettings));
        _loader = loader ?? new SettingsLoader();
    }

    public string SettingsPath => _settingsPath;

    public Settings Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public void Start()
    {
        ThrowIfDisposed();

        if (_pollTask is not null)
        {
            return;
        }

        // Seed the baseline from the file the initial settings were loaded from, so the first poll only
        // reloads when the content has actually changed since startup rather than on the very first tick.
        _lastContentHash = TryComputeContentHash();

        _pollCancellation = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollForChangesAsync(_pollCancellation.Token));

        AppLogger.Info("Watching settings file for changes. settingsPath={SettingsPath}.", _settingsPath);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _pollCancellation?.Cancel();

        try
        {
            _pollTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The poll loop only ends via cancellation; nothing here needs to surface.
        }

        _pollCancellation?.Dispose();
        _pollCancellation = null;
        _pollTask = null;
    }

    private async Task PollForChangesAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                CheckForChange();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }

    private void CheckForChange()
    {
        var currentHash = TryComputeContentHash();

        // A transient read miss (the file briefly gone during a save) leaves the baseline untouched so the
        // next tick can retry; the existing settings keep running in the meantime.
        if (currentHash is null)
        {
            AppLogger.Debug("Settings file was not readable this poll; keeping current settings. settingsPath={SettingsPath}.", _settingsPath);
            return;
        }

        if (_lastContentHash is not null && currentHash.AsSpan().SequenceEqual(_lastContentHash))
        {
            return;
        }

        _lastContentHash = currentHash;
        AppLogger.Info("Settings file changed on disk. Reloading. settingsPath={SettingsPath}.", _settingsPath);
        Reload();
    }

    private byte[]? TryComputeContentHash()
    {
        try
        {
            using var stream = new FileStream(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            return SHA256.HashData(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Reload()
    {
        var result = _loader.Load(_settingsPath);
        if (!result.IsSuccess)
        {
            AppLogger.Error("Settings reload failed. settingsPath={SettingsPath}. Existing settings will be kept.", _settingsPath);
            foreach (var error in result.Errors)
            {
                AppLogger.Error("Settings reload validation error. error={ValidationError}.", error);
            }

            return;
        }

        var reloadedSettings = result.Settings!;
        if (AppLogger.TryParseLogLevel(reloadedSettings.Logging.LogLevel, out var parsedLevel))
        {
            AppLogger.SetMinimumLevel(parsedLevel);
        }

        lock (_sync)
        {
            _current = reloadedSettings;
        }

        AppLogger.Info("Settings reloaded successfully. settingsPath={SettingsPath}.", _settingsPath);
        AppLogger.Debug("Current log level from settings. logLevel={LogLevel}.", reloadedSettings.Logging.LogLevel);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LiveSettings));
        }
    }
}
