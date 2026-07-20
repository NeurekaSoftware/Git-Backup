using System.Text.RegularExpressions;

namespace GitBackup.Configuration;

/// <summary>
/// Resolves a configured secret so it never has to be written into the settings file in cleartext.
/// A secret may be given literally, as a <c>${ENV_VAR}</c> placeholder, or through a companion
/// <c>*File</c> key naming a file to read — a Docker or Kubernetes secret mount.
/// </summary>
/// <remarks>
/// Anyone who can read the settings file recovers every forge token and the storage keys at once, and
/// long-lived cleartext secrets make rotation manual and easy to skip. The indirection keeps them out
/// of the file while leaving a literal value working, so existing configurations are unaffected.
/// </remarks>
public static class SecretResolver
{
    // Deliberately anchored: only a value that is *entirely* a placeholder is substituted, so a literal
    // secret that merely happens to contain "${" is passed through untouched rather than mangled.
    private static readonly Regex EnvPlaceholder =
        new(@"^\$\{([A-Za-z_][A-Za-z0-9_]*)\}$", RegexOptions.Compiled);

    /// <summary>
    /// Resolves a secret from <paramref name="filePath"/> when given, otherwise from
    /// <paramref name="value"/> (expanding a <c>${ENV_VAR}</c> placeholder). Returns null and records an
    /// error against <paramref name="configPath"/> when the source is unusable — an unset variable or an
    /// unreadable file is a misconfiguration worth failing on, not worth starting up without.
    /// </summary>
    public static string? Resolve(string? value, string? filePath, string configPath, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{configPath} and {configPath}File are both set. Use one or the other.");
                return null;
            }

            try
            {
                return File.ReadAllText(filePath).Trim();
            }
            catch (Exception exception)
            {
                errors.Add($"{configPath}File '{filePath}' could not be read: {exception.Message}");
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var match = EnvPlaceholder.Match(value.Trim());
        if (!match.Success)
        {
            return value;
        }

        var variable = match.Groups[1].Value;
        var resolved = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            errors.Add($"{configPath} references environment variable '{variable}', which is not set.");
            return null;
        }

        return resolved;
    }
}
