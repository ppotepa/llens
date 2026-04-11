using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Llens.Scanning;

/// <summary>
/// Uses <c>git ls-files</c> to enumerate files, giving full gitignore compliance
/// for free — including nested .gitignore files, global ignores, and .git/info/exclude.
/// Falls back to directory enumeration if the path is not inside a git repo.
/// </summary>
public class GitAwareFileScanner(ILogger<GitAwareFileScanner> logger) : IFileScanner
{
    public async IAsyncEnumerable<string> GetFilesAsync(
        string repoPath,
        IReadOnlySet<string> extensions,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsGitRepo(repoPath))
        {
            logger.LogDebug("{Path} is not a git repo, falling back to directory scan", repoPath);
            foreach (var file in FallbackScan(repoPath, extensions))
                yield return file;
            yield break;
        }

        // --cached   = tracked files
        // --others   = untracked files
        // --exclude-standard = apply .gitignore / .git/info/exclude / global excludes
        var result = await RunGitAsync(repoPath, "ls-files --cached --others --exclude-standard", ct);
        if (!result.Success)
        {
            logger.LogWarning("git ls-files failed in {Path}: {Error}", repoPath, result.Error);
            foreach (var file in FallbackScan(repoPath, extensions))
                yield return file;
            yield break;
        }

        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fullPath = Path.GetFullPath(Path.Combine(repoPath, line));
            if (extensions.Contains(Path.GetExtension(fullPath)))
                yield return fullPath;
        }
    }

    public async Task<bool> ShouldIndexAsync(
        string repoPath,
        string filePath,
        IReadOnlySet<string> extensions,
        CancellationToken ct = default)
    {
        if (!extensions.Contains(Path.GetExtension(filePath)))
            return false;

        if (!IsGitRepo(repoPath))
            return File.Exists(filePath);

        // exit 0 = ignored, exit 1 = not ignored
        var result = await RunGitAsync(repoPath, $"check-ignore -q \"{filePath}\"", ct);
        return !result.Success; // not ignored = should index
    }

    private static bool IsGitRepo(string path)
        => Directory.Exists(Path.Combine(path, ".git"))
        || RunGitSync(path, "rev-parse --git-dir");

    private static IEnumerable<string> FallbackScan(string repoPath, IReadOnlySet<string> extensions)
        => Directory
            .EnumerateFiles(repoPath, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f)));

    private static async Task<(bool Success, string Output, string Error)> RunGitAsync(
        string workingDir, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode == 0, output, error);
    }

    private static bool RunGitSync(string workingDir, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(psi)!;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch { return false; }
    }
}
