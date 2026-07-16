using System.Diagnostics;
using System.Text;

namespace GitBackup.Services.Git;

public sealed class GitCliRepositoryService : IGitRepositoryService
{
    public async Task SyncBareRepositoryAsync(
        string remoteUrl,
        string localPath,
        GitCredential? credential,
        bool force,
        bool includeLfs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var shouldClone = !await IsBareRepositoryAsync(localPath, cancellationToken);

        if (force && Directory.Exists(localPath))
        {
            Directory.Delete(localPath, recursive: true);
            shouldClone = true;
        }

        if (shouldClone)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await CloneMirrorAsync(remoteUrl, localPath, credential, cancellationToken);
        }
        else
        {
            await SetRemoteUrlAsync(localPath, remoteUrl, credential, cancellationToken);
            await FetchAsync(localPath, credential, cancellationToken);
        }

        if (includeLfs)
        {
            await FetchLfsAsync(localPath, credential, cancellationToken);
        }
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
            cancellationToken,
            throwOnFailure: false);

        return result.ExitCode == 0 &&
               result.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static Task CloneMirrorAsync(string remoteUrl, string localPath, GitCredential? credential, CancellationToken cancellationToken)
    {
        return ExecuteGitAsync(
            ["clone", "--mirror", remoteUrl, localPath],
            credential,
            cancellationToken,
            throwOnFailure: true);
    }

    private static Task SetRemoteUrlAsync(string localPath, string remoteUrl, GitCredential? credential, CancellationToken cancellationToken)
    {
        return ExecuteGitAsync(
            ["-C", localPath, "remote", "set-url", "origin", remoteUrl],
            credential,
            cancellationToken,
            throwOnFailure: true);
    }

    private static Task FetchAsync(string localPath, GitCredential? credential, CancellationToken cancellationToken)
    {
        return ExecuteGitAsync(
            ["-C", localPath, "fetch", "--all", "--prune"],
            credential,
            cancellationToken,
            throwOnFailure: true);
    }

    private static Task FetchLfsAsync(string localPath, GitCredential? credential, CancellationToken cancellationToken)
    {
        return ExecuteGitAsync(
            ["-C", localPath, "lfs", "fetch", "--all"],
            credential,
            cancellationToken,
            throwOnFailure: true);
    }

    private static async Task<CommandResult> ExecuteGitAsync(
        IReadOnlyList<string> arguments,
        GitCredential? credential,
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
            processStartInfo.ArgumentList.Add("-c");
            processStartInfo.ArgumentList.Add($"http.extraheader=Authorization: Basic {CreateBasicHeader(credential)}");
            processStartInfo.ArgumentList.Add("-c");
            processStartInfo.ArgumentList.Add("credential.helper=");
        }

        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = processStartInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

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

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
