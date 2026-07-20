namespace GitBackup.Services.Paths;

public sealed class RepositoryPathInfo
{
    public required string FullDomain { get; init; }

    public required string Owner { get; init; }

    public string? Group { get; init; }

    public string? SecondaryGroup { get; init; }

    public required string RepositoryName { get; init; }

    public IEnumerable<string> Hierarchy
    {
        get
        {
            yield return Owner;

            if (!string.IsNullOrWhiteSpace(Group))
            {
                yield return Group;
            }

            if (!string.IsNullOrWhiteSpace(SecondaryGroup))
            {
                yield return SecondaryGroup;
            }

            yield return RepositoryName;
        }
    }
}
