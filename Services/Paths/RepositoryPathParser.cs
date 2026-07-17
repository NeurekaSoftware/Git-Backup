namespace GitBackup.Services.Paths;

public static class RepositoryPathParser
{
    public static RepositoryPathInfo Parse(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid repository URL '{repositoryUrl}'.");
        }

        if (!GitRepositoryUrl.IsHttpOrHttps(uri))
        {
            throw new InvalidOperationException($"Unsupported repository URL scheme in '{repositoryUrl}'. Only http and https are supported.");
        }

        var pathSegments = GitRepositoryUrl.SplitUnescapedSegments(uri);

        if (pathSegments.Length < 2)
        {
            throw new InvalidOperationException($"Repository URL '{repositoryUrl}' does not contain owner and repository segments.");
        }

        var owner = NormalizeSegment(pathSegments[0]);
        var repositoryName = NormalizeSegment(GitRepositoryUrl.TrimGitSuffix(pathSegments[^1]));

        var groupSegments = pathSegments.Skip(1).Take(pathSegments.Length - 2).ToList();
        var group = groupSegments.Count > 0
            ? NormalizeSegment(groupSegments[0])
            : null;

        var secondaryGroup = groupSegments.Count > 1
            ? NormalizeSegment(string.Join('-', groupSegments.Skip(1)))
            : null;

        return new RepositoryPathInfo
        {
            FullDomain = NormalizeSegment(uri.Host),
            Owner = owner,
            Group = group,
            SecondaryGroup = secondaryGroup,
            RepositoryName = repositoryName
        };
    }

    private static string NormalizeSegment(string value)
    {
        return GitRepositoryUrl.NormalizeStorageSegment(value, "unknown", lowercase: true);
    }
}
