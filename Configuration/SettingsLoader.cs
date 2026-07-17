using GitBackup.Configuration.Models;
using GitBackup.Configuration.Yaml;
using GitBackup.Runtime;
using GitBackup.Services.Paths;
using GitBackup.Services.Scheduling;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GitBackup.Configuration;

public sealed class SettingsLoader
{
    private readonly IDeserializer _deserializer;

    public SettingsLoader()
    {
        // Unrecognized keys are rejected rather than dropped, for the same reason ValidateRawEnums
        // rejects unrecognized values: a typo like `includeIsues: true` would otherwise be accepted in
        // silence and simply never back that data up.
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new ScalarOrSequenceConverter())
            .Build();
    }

    public SettingsLoadResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return SettingsLoadResult.Failure(["settings path is required."]);
        }

        if (!File.Exists(path))
        {
            return SettingsLoadResult.Failure([$"settings file not found: '{path}'"]);
        }

        string yaml;

        try
        {
            yaml = File.ReadAllText(path);
        }
        catch (Exception exception)
        {
            return SettingsLoadResult.Failure([$"failed to load settings '{path}': {exception.Message}"]);
        }

        // Checked against the raw document before deserialization: a settings file still using a
        // deprecated key gets the migration message rather than an unrecognized-key error.
        var deprecatedKeyErrors = ValidateDeprecatedKeys(yaml);
        if (deprecatedKeyErrors.Count > 0)
        {
            return SettingsLoadResult.Failure(deprecatedKeyErrors);
        }

        Settings settings;

        try
        {
            settings = _deserializer.Deserialize<Settings>(yaml) ?? new Settings();
        }
        catch (YamlException exception)
        {
            return SettingsLoadResult.Failure([$"YAML parse error in '{path}': {exception.Message}"]);
        }
        catch (Exception exception)
        {
            return SettingsLoadResult.Failure([$"failed to load settings '{path}': {exception.Message}"]);
        }

        // Reject a present-but-unrecognized enum value before Normalize coerces it to a default —
        // otherwise a typo like `logLevel: verbse` would be silently accepted as `info`.
        var rawValueErrors = ValidateRawEnums(settings);
        if (rawValueErrors.Count > 0)
        {
            return SettingsLoadResult.Failure(rawValueErrors);
        }

        Normalize(settings);

        // Resolve secrets before validation, so the required-value checks below see what will actually
        // be used rather than the placeholder standing in for it.
        var secretErrors = new List<string>();
        ResolveSecrets(settings, secretErrors);
        if (secretErrors.Count > 0)
        {
            return SettingsLoadResult.Failure(secretErrors);
        }

        var errors = Validate(settings);

        return errors.Count == 0
            ? SettingsLoadResult.Success(settings)
            : SettingsLoadResult.Failure(errors);
    }

    /// <summary>
    /// Replaces each configured secret with its resolved value, so a token can be supplied through an
    /// environment variable or a secret file instead of being written into the settings file.
    /// </summary>
    private static void ResolveSecrets(Settings settings, List<string> errors)
    {
        settings.Storage.AccessKeyId = SecretResolver.Resolve(
            settings.Storage.AccessKeyId, settings.Storage.AccessKeyIdFile, "storage.accessKeyId", errors);
        settings.Storage.SecretAccessKey = SecretResolver.Resolve(
            settings.Storage.SecretAccessKey, settings.Storage.SecretAccessKeyFile, "storage.secretAccessKey", errors);

        foreach (var (name, credential) in settings.Credentials)
        {
            credential.ApiKey = SecretResolver.Resolve(
                credential.ApiKey, credential.ApiKeyFile, $"credentials.{name}.apiKey", errors);
        }
    }

    private static void Normalize(Settings settings)
    {
        settings.Logging ??= new LoggingConfig();
        settings.Logging.LogLevel = NormalizeLogLevel(settings.Logging.LogLevel);
        settings.Storage ??= new StorageConfig();
        settings.Storage.ForcePathStyle ??= false;
        settings.Storage.PayloadSignatureMode = PayloadSignatureModes.Normalize(settings.Storage.PayloadSignatureMode);
        settings.Storage.RetentionMinimum ??= 1;
        settings.Credentials ??= new Dictionary<string, CredentialConfig>(StringComparer.OrdinalIgnoreCase);
        settings.Credentials = new Dictionary<string, CredentialConfig>(settings.Credentials, StringComparer.OrdinalIgnoreCase);
        settings.Repositories ??= [];
        settings.Schedule ??= new ScheduleConfig();
        settings.Schedule.Repositories ??= new JobScheduleConfig();
        settings.Concurrency ??= new ConcurrencyConfig();
        settings.Concurrency.Repositories ??= 1;
        settings.Concurrency.Metadata ??= 1;

        foreach (var repository in settings.Repositories)
        {
            if (repository is null)
            {
                continue;
            }

            repository.Enabled ??= true;
            repository.Lfs ??= true;
            repository.Cache ??= true;
            repository.IncludeStarred ??= false;
            repository.IncludeSnippets ??= false;
            repository.IncludeIssues ??= false;
            repository.IncludeIssueArtifacts ??= false;
            repository.IncludeMergeRequests ??= false;
            repository.IncludeMergeRequestsArtifacts ??= false;
            repository.IncludeReleases ??= false;
            repository.IncludeReleaseArtifacts ??= false;
            repository.Mode = repository.Mode?.Trim().ToLowerInvariant();
            repository.Provider = repository.Provider?.Trim().ToLowerInvariant();
            repository.Urls = repository.Urls?
                .Select(url => url?.Trim())
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url!)
                .ToList();
        }
    }

    private static List<string> Validate(Settings settings)
    {
        var errors = new List<string>();
        ValidateStorage(settings, errors);
        ValidateRepositories(settings, errors);
        ValidateSchedule(settings, errors);
        ValidateConcurrency(settings, errors);
        return errors;
    }

    // Validates the enum-like fields against the raw, pre-Normalize values. Both are optional (a
    // null/blank value falls back to a default in Normalize), so only a value that is present and
    // unrecognized is reported — Normalize would otherwise coerce it to a default and hide the mistake.
    private static List<string> ValidateRawEnums(Settings settings)
    {
        var errors = new List<string>();

        var logLevel = settings.Logging?.LogLevel;
        if (!string.IsNullOrWhiteSpace(logLevel) && !AppLogger.TryParseLogLevel(logLevel, out _))
        {
            errors.Add($"logging.logLevel '{logLevel}' is invalid. Supported values: {string.Join(", ", AppLogger.SupportedLogLevels)}.");
        }

        var payloadSignatureMode = settings.Storage?.PayloadSignatureMode;
        if (!string.IsNullOrWhiteSpace(payloadSignatureMode) && !PayloadSignatureModes.Supported.Contains(payloadSignatureMode))
        {
            errors.Add($"storage.payloadSignatureMode '{payloadSignatureMode}' is invalid. Supported values: {string.Join(", ", PayloadSignatureModes.Supported)}.");
        }

        return errors;
    }

    private static void ValidateConcurrency(Settings settings, List<string> errors)
    {
        if (settings.Concurrency.Repositories < 1)
        {
            errors.Add("concurrency.repositories must be 1 or greater.");
        }

        if (settings.Concurrency.Metadata < 1)
        {
            errors.Add("concurrency.metadata must be 1 or greater.");
        }
    }

    private static void ValidateStorage(Settings settings, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(settings.Storage.Endpoint))
        {
            errors.Add("storage.endpoint is required.");
        }
        else if (!GitRepositoryUrl.TryCreateHttpUrl(settings.Storage.Endpoint, out var storageEndpoint))
        {
            errors.Add("storage.endpoint must be an absolute http or https URL.");
        }
        else if (storageEndpoint.Scheme == Uri.UriSchemeHttp && !storageEndpoint.IsLoopback)
        {
            // Same rule the git transport already enforces: never put credentials or backup data on the
            // wire in the clear. Plain http would expose the access key id and every archived byte to
            // anyone on-path, and under payloadSignatureMode: unsigned nothing would detect a change to
            // an uploaded object either. Loopback stays allowed for local testing.
            errors.Add("storage.endpoint must use https for a non-loopback host.");
        }

        if (string.IsNullOrWhiteSpace(settings.Storage.Region))
        {
            errors.Add("storage.region is required.");
        }

        if (string.IsNullOrWhiteSpace(settings.Storage.AccessKeyId))
        {
            errors.Add("storage.accessKeyId is required.");
        }

        if (string.IsNullOrWhiteSpace(settings.Storage.SecretAccessKey))
        {
            errors.Add("storage.secretAccessKey is required.");
        }

        if (string.IsNullOrWhiteSpace(settings.Storage.Bucket))
        {
            errors.Add("storage.bucket is required.");
        }

        if (settings.Storage.RetentionMinimum is < 0)
        {
            errors.Add("storage.retentionMinimum must be 0 or greater.");
        }
    }

    private static void ValidateRepositories(Settings settings, List<string> errors)
    {
        for (var i = 0; i < settings.Repositories.Count; i++)
        {
            var repository = settings.Repositories[i];
            if (repository is null)
            {
                errors.Add($"repositories[{i}] is required.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(repository.Mode))
            {
                errors.Add($"repositories[{i}].mode is required.");
                continue;
            }

            if (string.Equals(repository.Mode, RepositoryJobModes.Provider, StringComparison.OrdinalIgnoreCase))
            {
                ValidateProviderRepository(settings, repository, i, errors);
                continue;
            }

            if (string.Equals(repository.Mode, RepositoryJobModes.Url, StringComparison.OrdinalIgnoreCase))
            {
                ValidateUrlRepository(settings, repository, i, errors);
                continue;
            }

            errors.Add($"repositories[{i}].mode '{repository.Mode}' is not supported. Supported values: {string.Join(", ", RepositoryJobModes.Supported)}.");
        }
    }

    private static void ValidateProviderRepository(
        Settings settings,
        RepositoryJobConfig repository,
        int index,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(repository.Provider))
        {
            errors.Add($"repositories[{index}].provider is required when mode is provider.");
        }
        else if (!RepositoryProviders.Supported.Contains(repository.Provider))
        {
            errors.Add($"repositories[{index}].provider '{repository.Provider}' is not supported. Supported values: {string.Join(", ", RepositoryProviders.Supported)}.");
        }

        if (string.IsNullOrWhiteSpace(repository.Credential))
        {
            errors.Add($"repositories[{index}].credential is required when mode is provider.");
        }
        else if (!settings.Credentials.ContainsKey(repository.Credential))
        {
            errors.Add($"repositories[{index}].credential references unknown credential '{repository.Credential}'.");
        }

        if (repository.Urls is { Count: > 0 })
        {
            errors.Add($"repositories[{index}].url is not allowed when mode is provider.");
        }

        if (!string.IsNullOrWhiteSpace(repository.BaseUrl) && !IsValidHttpUrl(repository.BaseUrl))
        {
            errors.Add($"repositories[{index}].baseUrl must be an absolute http or https URL.");
        }

        if (repository.IncludeIssueArtifacts == true && repository.IncludeIssues != true)
        {
            errors.Add($"repositories[{index}].includeIssueArtifacts requires includeIssues.");
        }

        if (repository.IncludeMergeRequestsArtifacts == true && repository.IncludeMergeRequests != true)
        {
            errors.Add($"repositories[{index}].includeMergeRequestsArtifacts requires includeMergeRequests.");
        }

        if (repository.IncludeReleaseArtifacts == true && repository.IncludeReleases != true)
        {
            errors.Add($"repositories[{index}].includeReleaseArtifacts requires includeReleases.");
        }
    }

    private static void ValidateUrlRepository(
        Settings settings,
        RepositoryJobConfig repository,
        int index,
        List<string> errors)
    {
        if (repository.Urls is not { Count: > 0 })
        {
            errors.Add($"repositories[{index}].url is required when mode is url.");
        }
        else
        {
            for (var j = 0; j < repository.Urls.Count; j++)
            {
                if (!IsValidHttpUrl(repository.Urls[j]))
                {
                    errors.Add($"repositories[{index}].url[{j}] must be an absolute http or https URL.");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(repository.Provider))
        {
            errors.Add($"repositories[{index}].provider is not allowed when mode is url.");
        }

        if (!string.IsNullOrWhiteSpace(repository.BaseUrl))
        {
            errors.Add($"repositories[{index}].baseUrl is not allowed when mode is url.");
        }

        // Every include* flag is provider-only, so none are allowed in url mode. Drive the checks from
        // one table so a new flag needs a single row here rather than another copied block.
        var urlDisallowedFlags = new (string Name, bool? Value)[]
        {
            ("includeStarred", repository.IncludeStarred),
            ("includeSnippets", repository.IncludeSnippets),
            ("includeIssues", repository.IncludeIssues),
            ("includeIssueArtifacts", repository.IncludeIssueArtifacts),
            ("includeMergeRequests", repository.IncludeMergeRequests),
            ("includeMergeRequestsArtifacts", repository.IncludeMergeRequestsArtifacts),
            ("includeReleases", repository.IncludeReleases),
            ("includeReleaseArtifacts", repository.IncludeReleaseArtifacts)
        };

        foreach (var (name, value) in urlDisallowedFlags)
        {
            if (value == true)
            {
                errors.Add($"repositories[{index}].{name} is not allowed when mode is url.");
            }
        }

        if (string.IsNullOrWhiteSpace(repository.Credential))
        {
            return;
        }

        if (!settings.Credentials.ContainsKey(repository.Credential))
        {
            errors.Add($"repositories[{index}].credential references unknown credential '{repository.Credential}'.");
        }
    }

    private static void ValidateSchedule(Settings settings, List<string> errors)
    {
        ValidateCron(settings.Schedule.Repositories.Cron, "schedule.repositories.cron", errors);
    }

    private static void ValidateCron(string? cronExpression, string fieldName, List<string> errors)
    {
        if (!CronScheduleParser.TryParse(cronExpression, out _, out var parseError))
        {
            errors.Add($"{fieldName} is invalid: {parseError}");
        }
    }

    private static string NormalizeLogLevel(string? configuredValue)
    {
        return AppLogger.TryParseLogLevel(configuredValue, out var level)
            ? AppLogger.ToConfigValue(level)
            : AppLogger.DefaultLogLevel;
    }

    private static bool IsValidHttpUrl(string? value)
    {
        return GitRepositoryUrl.TryCreateHttpUrl(value, out _);
    }

    private static List<string> ValidateDeprecatedKeys(string yaml)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return errors;
        }

        YamlMappingNode? root;

        try
        {
            using var reader = new StringReader(yaml);
            var stream = new YamlStream();
            stream.Load(reader);

            root = stream.Documents.Count > 0
                ? stream.Documents[0].RootNode as YamlMappingNode
                : null;
        }
        catch
        {
            // Yaml parse errors are handled by the main deserialization path.
            return errors;
        }

        if (root is null)
        {
            return errors;
        }

        if (ContainsKey(root, "backups"))
        {
            errors.Add("backups is no longer supported. Use repositories entries with mode: provider.");
        }

        if (ContainsKey(root, "mirrors"))
        {
            errors.Add("mirrors is no longer supported. Use repositories entries with mode: url.");
        }

        if (!TryGetMappingChild(root, "schedule", out var scheduleNode))
        {
            return errors;
        }

        if (ContainsKey(scheduleNode, "backups"))
        {
            errors.Add("schedule.backups is no longer supported. Use schedule.repositories.cron.");
        }

        if (ContainsKey(scheduleNode, "mirrors"))
        {
            errors.Add("schedule.mirrors is no longer supported. Use schedule.repositories.cron.");
        }

        return errors;
    }

    private static bool ContainsKey(YamlMappingNode mapping, string key)
    {
        foreach (var node in mapping.Children.Keys)
        {
            if (node is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetMappingChild(YamlMappingNode mapping, string key, out YamlMappingNode child)
    {
        foreach (var item in mapping.Children)
        {
            if (item.Key is not YamlScalarNode scalar ||
                !string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (item.Value is YamlMappingNode typedChild)
            {
                child = typedChild;
                return true;
            }

            break;
        }

        child = null!;
        return false;
    }
}
