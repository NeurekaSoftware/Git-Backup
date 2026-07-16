using GitBackup.Runtime;

namespace GitBackup.Configuration;

public sealed class LiveSettings : IDisposable
{
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(300);

    private readonly SettingsLoader _loader;
    private readonly object _sync = new();
    private readonly string _settingsPath;
    private Settings _current;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounceTokenSource;
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

        if (_watcher is not null)
        {
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Environment.CurrentDirectory;
        }

        var fileName = System.IO.Path.GetFileName(_settingsPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException($"Unable to watch settings path '{_settingsPath}'.");
        }

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnSettingsFileChanged;
        _watcher.Created += OnSettingsFileChanged;
        _watcher.Deleted += OnSettingsFileChanged;
        _watcher.Renamed += OnSettingsFileRenamed;

        AppLogger.Info("Settings file watcher started. settingsPath={SettingsPath}", _settingsPath);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_sync)
        {
            _reloadDebounceTokenSource?.Cancel();
            _reloadDebounceTokenSource?.Dispose();
            _reloadDebounceTokenSource = null;
        }

        if (_watcher is not null)
        {
            _watcher.Changed -= OnSettingsFileChanged;
            _watcher.Created -= OnSettingsFileChanged;
            _watcher.Deleted -= OnSettingsFileChanged;
            _watcher.Renamed -= OnSettingsFileRenamed;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void OnSettingsFileChanged(object sender, FileSystemEventArgs args)
    {
        AppLogger.Debug(
            "Settings file event detected. changeType={ChangeType}, path={Path}.",
            args.ChangeType,
            args.FullPath);
        TriggerReload();
    }

    private void OnSettingsFileRenamed(object sender, RenamedEventArgs args)
    {
        AppLogger.Debug(
            "Settings file rename detected. oldPath={OldPath}, newPath={NewPath}.",
            args.OldFullPath,
            args.FullPath);
        TriggerReload();
    }

    private void TriggerReload()
    {
        if (_disposed)
        {
            return;
        }

        CancellationTokenSource debounceTokenSource;

        lock (_sync)
        {
            _reloadDebounceTokenSource?.Cancel();
            _reloadDebounceTokenSource?.Dispose();

            _reloadDebounceTokenSource = new CancellationTokenSource();
            debounceTokenSource = _reloadDebounceTokenSource;
        }

        _ = Task.Run(() => ReloadAfterDebounceAsync(debounceTokenSource.Token));
        AppLogger.Debug("Settings reload scheduled after debounce.");
    }

    private async Task ReloadAfterDebounceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ReloadDebounce, cancellationToken);
            AppLogger.Info("Reloading settings from disk.");
            Reload();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore superseded reload tasks.
            AppLogger.Debug("Superseded settings reload request was canceled.");
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
                AppLogger.Error("Settings reload validation error: {ValidationError}", error);
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

        AppLogger.Info("Settings reloaded successfully. settingsPath={SettingsPath}", _settingsPath);
        AppLogger.Debug("Current log level from settings: {LogLevel}", reloadedSettings.Logging.LogLevel);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LiveSettings));
        }
    }
}
