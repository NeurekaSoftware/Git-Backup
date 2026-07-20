namespace GitBackup.Services.Git;

// Signals that a git remote could not be accessed for an expected, per-repository reason: it is
// private, was removed, or the supplied credentials do not grant access. Callers treat this as a
// skippable warning for a single repository rather than a run-level failure, unlike a generic git
// error (corruption, network outage, …) which still surfaces as an error.
public sealed class GitRemoteInaccessibleException : Exception
{
    public GitRemoteInaccessibleException(string message)
        : base(message)
    {
    }
}
