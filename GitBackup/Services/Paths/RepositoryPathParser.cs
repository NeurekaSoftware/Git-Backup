using System.Text.RegularExpressions;

namespace GitBackup.Services.Paths;

public static class RepositoryPathParser
{
    private static readonly Regex InvalidSegmentCharacters = new("[^a-zA-Z0-9._-]+", RegexOptions.Compiled);

    public static RepositoryPathInfo Parse(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid repository URL '{repositoryUrl}'.");
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported repository URL scheme in '{repositoryUrl}'. Only http and https are supported.");
        }

        var pathSegments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .ToList();

        if (pathSegments.Count < 2)
        {
            throw new InvalidOperationException($"Repository URL '{repositoryUrl}' does not contain owner and repository segments.");
        }

        var owner = NormalizeSegment(pathSegments[0]);
        var repositoryName = NormalizeSegment(TrimGitSuffix(pathSegments[^1]));

        var groupSegments = pathSegments.Skip(1).Take(pathSegments.Count - 2).ToList();
        var group = groupSegments.Count > 0
            ? NormalizeSegment(groupSegments[0])
            : null;

        var secondaryGroup = groupSegments.Count > 1
            ? NormalizeSegment(string.Join('-', groupSegments.Skip(1)))
            : null;

        return new RepositoryPathInfo
        {
            BaseDomain = NormalizeSegment(GetBaseDomain(uri.Host)),
            FullDomain = NormalizeSegment(uri.Host),
            Owner = owner,
            Group = group,
            SecondaryGroup = secondaryGroup,
            RepositoryName = repositoryName
        };
    }

    private static string GetBaseDomain(string host)
    {
        var labels = host
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(label => label.ToLowerInvariant())
            .ToArray();

        if (labels.Length <= 2)
        {
            return string.Join('.', labels);
        }

        return string.Join('.', labels[^2], labels[^1]);
    }

    private static string TrimGitSuffix(string repositoryName)
    {
        return repositoryName.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repositoryName[..^4]
            : repositoryName;
    }

    private static string NormalizeSegment(string value)
    {
        var normalized = InvalidSegmentCharacters.Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }
}
