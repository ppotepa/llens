using System.Diagnostics;
using Llens.Api;
using Llens.Models;

namespace Llens.Application.JsCheck;

public sealed class JsCheckService : IJsCheckService
{
    public async Task<JsCheckOutcome> RunAsync(Project project, CompactJsCheckRequest request, CancellationToken ct)
    {
        var root = project.Config.ResolvedPath;
        var maxFiles = Math.Clamp(request.MaxFiles <= 0 ? 120 : request.MaxFiles, 1, 2000);
        var timeoutMs = Math.Clamp(request.TimeoutMs <= 0 ? 12000 : request.TimeoutMs, 1000, 120000);
        List<string> files;

        if (!string.IsNullOrWhiteSpace(request.Path))
        {
            var full = EnsureWithinProject(root, request.Path!);
            if (full is null)
                return new JsCheckOutcome(false, ErrorKind: JsCheckErrorKind.BadRequest, ErrorMessage: "Path is outside project root.");

            if (File.Exists(full))
            {
                files = IsJavaScriptFile(full) ? [full] : [];
            }
            else if (Directory.Exists(full))
            {
                files = EnumerateJavaScriptFiles(full, project.Config, maxFiles);
            }
            else
            {
                return new JsCheckOutcome(false, ErrorKind: JsCheckErrorKind.NotFound, ErrorMessage: "Path not found.");
            }
        }
        else
        {
            files = EnumerateJavaScriptFiles(root, project.Config, maxFiles);
        }

        var issues = new List<CompactJsCheckIssue>();
        foreach (var file in files)
        {
            try
            {
                var result = await RunProcessAsync("node", $"--check {QuoteArg(file)}", root, timeoutMs, ct);
                if (result.ExitCode == 0) continue;
                var msg = (string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr).Trim();
                issues.Add(new CompactJsCheckIssue(file, result.ExitCode, Truncate(msg, 1200)));
            }
            catch (Exception ex)
            {
                return new JsCheckOutcome(false, ErrorKind: JsCheckErrorKind.BadRequest, ErrorMessage: $"node runtime is not available: {ex.Message}");
            }
        }

        return new JsCheckOutcome(true, new CompactJsCheckResponse(project.Name, files.Count, issues.Count == 0, issues));
    }

    private static bool ShouldExclude(RepoConfig config, string path)
        => config.ExcludePaths.Any(x => path.Contains($"{Path.DirectorySeparatorChar}{x}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith($"{Path.DirectorySeparatorChar}{x}", StringComparison.OrdinalIgnoreCase));

    private static string? EnsureWithinProject(string projectRoot, string inputPath)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(inputPath) ? inputPath : Path.Combine(projectRoot, inputPath));
        var rootBase = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
        var rootPrefix = rootBase + Path.DirectorySeparatorChar;
        return full.Equals(rootBase, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    private static bool IsJavaScriptFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cjs", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> EnumerateJavaScriptFiles(string root, RepoConfig config, int maxFiles)
    {
        var results = new List<string>(capacity: Math.Min(maxFiles, 512));
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (results.Count >= maxFiles) break;
            if (ShouldExclude(config, file)) continue;
            if (!IsJavaScriptFile(file)) continue;
            results.Add(file);
        }
        return results;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(string file, string args, string cwd, int timeoutMs, CancellationToken ct)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        p.Start();
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        await p.WaitForExitAsync(cts.Token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (p.ExitCode, stdout, stderr);
    }

    private static string QuoteArg(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "\"\"";
        if (value.IndexOfAny([' ', '\t', '\n', '\r', '"']) < 0) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string Truncate(string s, int n)
        => s.Length <= n ? s : s[..n];
}
