using GitBackup.Configuration.Models;
using GitBackup.Configuration.Yaml;
using GitBackup.Runtime;
using GitBackup.Services.Scheduling;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GitBackup.Configuration;

public sealed class SettingsLoader
{
    private static readonly HashSet<string> SupportedRepositoryProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "github",
        "gitlab",
        "forgejo"
    };

    private static readonly HashSet<string> SupportedPayloadSignatureModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "full",
        "streaming",
        "unsigned"
    };

    private readonly IDeserializer _deserializer;

    public SettingsLoader()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new ScalarOrSequenceConverter())
            .IgnoreUnmatchedProperties()
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

        Settings settings;
        string yaml;

        try
        {
            yaml = File.ReadAllText(path);
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

        var deprecatedKeyErrors = ValidateDeprecatedKeys(yaml);
        if (deprecatedKeyErrors.Count > 0)
        {
            return SettingsLoadResult.Failure(deprecatedKeyErrors);
        }

        Normalize(settings);
        var errors = Validate(settings);

        return errors.Count == 0
            ? SettingsLoadResult.Success(settings)
            : SettingsLoadResult.Failure(errors);
    }

    private static void Normalize(Settings settings)
    {
        settings.Logging ??= new LoggingConfig();
        settings.Logging.LogLevel = NormalizeLogLevel(settings.Logging.LogLevel);
        settings.Storage ??= new StorageConfig();
        settings.Storage.ForcePathStyle ??= false;
        settings.Storage.PayloadSignatureMode = NormalizePayloadSignatureMode(settings.Storage.PayloadSignatureMode);
        settings.Storage.RetentionMinimum ??= 1;
        settings.Credentials ??= new Dictionary<string, CredentialConfig>(StringComparer.OrdinalIgnoreCase);
        settings.Credentials = new Dictionary<string, CredentialConfig>(settings.Credentials, StringComparer.OrdinalIgnoreCase);
        settings.Repositories ??= [];
        settings.Schedule ??= new ScheduleConfig();
        settings.Schedule.Repositories ??= new JobScheduleConfig();

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
        ValidateLogging(settings, errors);
        ValidateStorage(settings, errors);
        ValidateRepositories(settings, errors);
        ValidateSchedule(settings, errors);
        return errors;
    }

    private static void ValidateLogging(Settings settings, List<string> errors)
    {
        if (settings.Logging is null)
        {
            errors.Add("logging is required.");
            return;
        }

        if (!AppLogger.TryParseLogLevel(settings.Logging.LogLevel, out _))
        {
            var supported = string.Join(", ", AppLogger.SupportedLogLevels);
            errors.Add($"logging.logLevel '{settings.Logging.LogLevel}' is invalid. Supported values: {supported}.");
        }
    }

    private static void ValidateStorage(Settings settings, List<string> errors)
    {
        if (settings.Storage is null)
        {
            errors.Add("storage is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.Storage.Endpoint))
        {
            errors.Add("storage.endpoint is required.");
        }
        else if (!IsValidHttpUrl(settings.Storage.Endpoint))
        {
            errors.Add("storage.endpoint must be an absolute http or https URL.");
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

        if (!SupportedPayloadSignatureModes.Contains(settings.Storage.PayloadSignatureMode!))
        {
            errors.Add(
                $"storage.payloadSignatureMode '{settings.Storage.PayloadSignatureMode}' is invalid. Supported values: full, streaming, unsigned.");
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

            errors.Add($"repositories[{i}].mode '{repository.Mode}' is not supported. Supported values: provider, url.");
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
        else if (!SupportedRepositoryProviders.Contains(repository.Provider))
        {
            errors.Add($"repositories[{index}].provider '{repository.Provider}' is not supported. Supported values: github, gitlab, forgejo.");
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

        if (repository.IncludeStarred == true)
        {
            errors.Add($"repositories[{index}].includeStarred is not allowed when mode is url.");
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
        if (settings.Schedule is null)
        {
            errors.Add("schedule is required.");
            return;
        }

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

    private static string NormalizePayloadSignatureMode(string? configuredValue)
    {
        return configuredValue?.Trim().ToLowerInvariant() switch
        {
            "streaming" => "streaming",
            "unsigned" => "unsigned",
            _ => "full"
        };
    }

    private static bool IsValidHttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                   || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
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
