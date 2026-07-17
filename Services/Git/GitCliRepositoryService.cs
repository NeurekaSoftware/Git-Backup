using System.Diagnostics;
using System.Text;
using GitBackup.Runtime;
using GitBackup.Services.Paths;

namespace GitBackup.Services.Git;

public sealed class GitCliRepositoryService : IGitRepositoryService
{
    public async Task SyncBareRepositoryAsync(
        string remoteUrl,
        string localPath,
        GitCredential? credential,
        bool cache,
        bool includeLfs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Only http/https clone URLs are ever handled. Rejecting anything else here (the single choke
        // point for every clone/fetch) keeps a provider-supplied URL from reaching a git transport
        // helper such as `ext::` or `file://`, which would run commands or read local files.
        if (!IsSupportedTransport(remoteUrl))
        {
            throw new InvalidOperationException(
                $"Unsupported repository URL '{remoteUrl}'. Only http and https clone URLs are allowed.");
        }

        // Never put a credential on the wire in the clear. A plaintext-http remote to a non-loopback
        // host would send the Authorization header base64-but-unencrypted, so anyone on-path could
        // recover the token; refuse rather than leak it. Loopback http stays allowed for local testing.
        if (credential is not null && IsPlaintextHttpToRemoteHost(remoteUrl))
        {
            throw new InvalidOperationException(
                $"Refusing to send credentials to '{remoteUrl}' over plaintext http. Use https, or remove the credential for this remote.");
        }

        var hasMirror = await IsBareRepositoryAsync(localPath, cancellationToken);

        if (cache && hasMirror)
        {
            // Update the existing mirror. The --mirror refspec (+refs/*:refs/*) force-updates
            // rewritten branches and --prune drops refs deleted upstream, so the mirror tracks the
            // remote exactly. If the fetch fails (corruption, unrelated history, …), self-heal by
            // re-cloning from scratch so a cached repository can never get permanently stuck.
            try
            {
                await SetRemoteUrlAsync(localPath, remoteUrl, credential, cancellationToken);
                await FetchAsync(localPath, remoteUrl, credential, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A shutdown is not a corrupt mirror. Re-cloning here would delete the cached mirror and
                // then fail on the same cancelled token, leaving nothing behind.
                throw;
            }
            catch (Exception exception)
            {
                AppLogger.Warn(
                    "Incremental mirror fetch failed; re-cloning from scratch. localPath={LocalPath}, error={ErrorMessage}.",
                    localPath,
                    exception.Message);
                await FreshCloneAsync(remoteUrl, localPath, credential, cancellationToken);
            }
        }
        else
        {
            await FreshCloneAsync(remoteUrl, localPath, credential, cancellationToken);
        }

        if (includeLfs)
        {
            await FetchLfsAsync(localPath, remoteUrl, credential, cancellationToken);
        }
    }

    private static async Task FreshCloneAsync(string remoteUrl, string localPath, GitCredential? credential, CancellationToken cancellationToken)
    {
        if (Directory.Exists(localPath))
        {
            Directory.Delete(localPath, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await CloneMirrorAsync(remoteUrl, localPath, credential, cancellationToken);
    }

    private static async Task<bool> IsBareRepositoryAsync(string localPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(localPath))
        {
            return false;
        }

        var result = await ExecuteGitAsync(
            ["-C", localPath, "rev-parse", "--is-bare-repository"],
            credential: null,
            credentialScopeUrl: null,
            cancellationToken,
            throwOnFailure: false);

        return result.ExitCode == 0 &&
               result.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static Task CloneMirrorAsync(string remoteUrl, string localPath, GitCredential? credential, CancellationToken cancellationToken)
    {
        return ExecuteGitAsync(
            ["clone", "--mirror", "--", remoteUrl, localPath],
            credential,
            credentialScopeUrl: remoteUrl,
            cancellationToken,
            throwOnFailure: true);
    }

    private static Task SetRemoteUrlAsync(string localPath, string remoteUrl, GitCredential? credential, CancellationToken cancellationToken)
    {
        return ExecuteGitAsync(
            ["-C", localPath, "remote", "set-url", "origin", remoteUrl],
            credential,
            credentialScopeUrl: remoteUrl,
            cancellationToken,
            throwOnFailure: true);
    }

    private static Task FetchAsync(string localPath, string remoteUrl, GitCredential? credential, CancellationToken cancellationToken)
    {
        return ExecuteGitAsync(
            ["-C", localPath, "fetch", "--all", "--prune"],
            credential,
            credentialScopeUrl: remoteUrl,
            cancellationToken,
            throwOnFailure: true);
    }

    private static async Task FetchLfsAsync(string localPath, string remoteUrl, GitCredential? credential, CancellationToken cancellationToken)
    {
        var result = await ExecuteGitAsync(
            ["-C", localPath, "lfs", "fetch", "--all"],
            credential,
            credentialScopeUrl: remoteUrl,
            cancellationToken,
            throwOnFailure: false);

        if (result.ExitCode == 0)
        {
            return;
        }

        // A remote can have Git LFS turned off entirely, in which case the batch API declines and git-lfs
        // exits non-zero. That is an expected state, not a backup failure — the repository simply has no
        // LFS objects to mirror — so record it as skipped and let the rest of the snapshot proceed.
        if (IsLfsDisabledOnRemote(result.StandardError))
        {
            AppLogger.Info(
                "Skipped Git LFS fetch because it is disabled on the remote. repository={RepositoryUrl}.",
                remoteUrl);
            return;
        }

        throw new InvalidOperationException(
            $"git lfs fetch failed (exit={result.ExitCode}). stdout: {result.StandardOutput}. stderr: {result.StandardError}");
    }

    // git-lfs answers with "Git LFS is disabled for this repository." (GitHub, GitLab, and other
    // S3-fronted hosts alike) when a remote has LFS switched off. Match that signal so a disabled remote
    // is skipped while a genuine transfer failure still aborts the sync.
    private static bool IsLfsDisabledOnRemote(string standardError)
    {
        return standardError.Contains("Git LFS is disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CommandResult> ExecuteGitAsync(
        IReadOnlyList<string> arguments,
        GitCredential? credential,
        string? credentialScopeUrl,
        CancellationToken cancellationToken,
        bool throwOnFailure)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        processStartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        if (credential is not null)
        {
            // Inject the auth header (and disable credential helpers) via git's environment-based
            // config rather than `-c` arguments, so the token never appears in the git process's
            // world-readable /proc/<pid>/cmdline. GIT_CONFIG_* is honored by git and its HTTP/LFS
            // subprocesses just like `-c`.
            //
            // Scope the header to the remote's own origin (scheme://host[:port]/) instead of setting it
            // globally: git longest-prefix matches http.<url>.* config, so the token is sent only to the
            // repository's host and never to a different host reached via an HTTP redirect or a
            // repository-supplied LFS endpoint (.lfsconfig).
            var credentialScope = BuildCredentialScope(credentialScopeUrl);
            processStartInfo.Environment["GIT_CONFIG_COUNT"] = "2";
            processStartInfo.Environment["GIT_CONFIG_KEY_0"] = $"http.{credentialScope}.extraheader";
            processStartInfo.Environment["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {CreateBasicHeader(credential)}";
            processStartInfo.Environment["GIT_CONFIG_KEY_1"] = "credential.helper";
            processStartInfo.Environment["GIT_CONFIG_VALUE_1"] = string.Empty;
        }

        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = processStartInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Don't leave a detached git clone/fetch/LFS transfer running after the run is cancelled.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort: the process may already have exited.
            }

            throw;
        }

        var result = new CommandResult(
            process.ExitCode,
            await outputTask,
            await errorTask);

        if (throwOnFailure && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git command failed (exit={result.ExitCode}). stdout: {result.StandardOutput}. stderr: {result.StandardError}");
        }

        return result;
    }

    private static string CreateBasicHeader(GitCredential credential)
    {
        var raw = $"{credential.Username}:{credential.Password}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    // Derives the scheme://host[:port]/ origin used to scope the auth header to the remote's own host.
    // Callers validate the transport (IsSupportedTransport) before a credential is ever attached, so a
    // parse failure here is defensive only — it yields a scope that simply matches no request, which
    // fails closed (the header is withheld) rather than leaking the token.
    private static string BuildCredentialScope(string? credentialScopeUrl)
    {
        return Uri.TryCreate(credentialScopeUrl, UriKind.Absolute, out var uri)
            ? $"{uri.GetLeftPart(UriPartial.Authority)}/"
            : credentialScopeUrl ?? string.Empty;
    }

    private static bool IsSupportedTransport(string remoteUrl)
    {
        return GitRepositoryUrl.TryCreateHttpUrl(remoteUrl, out _);
    }

    private static bool IsPlaintextHttpToRemoteHost(string remoteUrl)
    {
        return Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
               && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               && !uri.IsLoopback;
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
