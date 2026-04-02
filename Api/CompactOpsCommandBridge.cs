using System.Diagnostics;
using System.Text.RegularExpressions;
using Llens.Models;
using Llens.Shared;

namespace Llens.Api;

internal static class CompactOpsCommandBridge
{
    public static async Task<CompactTestRunResponse> RunTestAsync(Project project, CompactTestRunRequest request, CancellationToken ct)
    {
        var root = project.Config.ResolvedPath;
        var timeoutMs = Math.Clamp(request.TimeoutMs <= 0 ? 120000 : request.TimeoutMs, 5000, 900000);
        var target = request.Target?.Trim().ToLowerInvariant() ?? "auto";
        var filter = request.Filter?.Trim();
        var command = ResolveTestCommand(root, target, filter)
            ?? throw new InvalidOperationException("Could not determine test command for project.");

        var result = await RunProcessAsync(command.File, command.Args, root, timeoutMs, ct);
        var diagnostics = ParseDiagnostics(result.Stdout + "\n" + result.Stderr, 400);
        var output = MergeProcessOutput(result.Stdout, result.Stderr, Math.Clamp(request.MaxChars <= 0 ? 20000 : request.MaxChars, 1000, 250000));
        return new CompactTestRunResponse(project.Name, command.Kind, result.ExitCode, diagnostics.Count, diagnostics, output);
    }

    public static async Task<CompactFormatResponse> RunFormatAsync(Project project, CompactFormatRequest request, CancellationToken ct)
    {
        var root = project.Config.ResolvedPath;
        var timeoutMs = Math.Clamp(request.TimeoutMs <= 0 ? 120000 : request.TimeoutMs, 5000, 900000);
        var target = request.Target?.Trim().ToLowerInvariant() ?? "auto";
        var command = ResolveFormatCommand(root, target, request.CheckOnly, request.Path)
            ?? throw new InvalidOperationException("Could not determine format command for project.");

        var result = await RunProcessAsync(command.File, command.Args, root, timeoutMs, ct);
        var output = MergeProcessOutput(result.Stdout, result.Stderr, Math.Clamp(request.MaxChars <= 0 ? 12000 : request.MaxChars, 1000, 200000));
        return new CompactFormatResponse(project.Name, command.Kind, request.CheckOnly, result.ExitCode, output);
    }

    public static async Task<CompactLintResponse> RunLintAsync(Project project, CompactLintRequest request, CancellationToken ct)
    {
        var root = project.Config.ResolvedPath;
        var timeoutMs = Math.Clamp(request.TimeoutMs <= 0 ? 120000 : request.TimeoutMs, 5000, 900000);
        var target = request.Target?.Trim().ToLowerInvariant() ?? "auto";
        var command = ResolveLintCommand(root, target, request.Path)
            ?? throw new InvalidOperationException("Could not determine lint command for project.");

        var result = await RunProcessAsync(command.File, command.Args, root, timeoutMs, ct);
        var diagnostics = ParseDiagnostics(result.Stdout + "\n" + result.Stderr, 500);
        var output = MergeProcessOutput(result.Stdout, result.Stderr, Math.Clamp(request.MaxChars <= 0 ? 16000 : request.MaxChars, 1000, 250000));
        return new CompactLintResponse(project.Name, command.Kind, result.ExitCode, diagnostics.Count, diagnostics, output);
    }

    public static async Task<CompactTypecheckResponse> RunTypecheckAsync(Project project, CompactTypecheckRequest request, CancellationToken ct)
    {
        var root = project.Config.ResolvedPath;
        var timeoutMs = Math.Clamp(request.TimeoutMs <= 0 ? 120000 : request.TimeoutMs, 5000, 900000);
        var target = request.Target?.Trim().ToLowerInvariant() ?? "auto";
        var command = ResolveTypecheckCommand(root, target)
            ?? throw new InvalidOperationException("Could not determine typecheck command for project.");

        var result = await RunProcessAsync(command.File, command.Args, root, timeoutMs, ct);
        var diagnostics = ParseDiagnostics(result.Stdout + "\n" + result.Stderr, 500);
        var output = MergeProcessOutput(result.Stdout, result.Stderr, Math.Clamp(request.MaxChars <= 0 ? 16000 : request.MaxChars, 1000, 250000));
        return new CompactTypecheckResponse(project.Name, command.Kind, result.ExitCode, diagnostics.Count, diagnostics, output);
    }

    public static (string File, string Args, string Kind)? ResolveTestCommand(string root, string target, string? filter)
    {
        static string BuildDotnetTestArgs(string? value)
        {
            var args = "test -v minimal --nologo";
            if (!string.IsNullOrWhiteSpace(value))
                args += " --filter " + QuoteArg(value);
            return args;
        }

        static string BuildCargoTestArgs(string? value)
        {
            var args = "test";
            if (!string.IsNullOrWhiteSpace(value))
                args += " " + QuoteArg(value);
            return args;
        }

        static string BuildNodeTestArgs(string? value)
        {
            var args = "--test";
            if (!string.IsNullOrWhiteSpace(value))
                args += " " + QuoteArg(value);
            return args;
        }

        return target switch
        {
            "dotnet" => ("dotnet", BuildDotnetTestArgs(filter), "dotnet"),
            "cargo" => ("cargo", BuildCargoTestArgs(filter), "cargo"),
            "node" => ("node", BuildNodeTestArgs(filter), "node"),
            _ => File.Exists(Path.Combine(root, "Cargo.toml"))
                ? ("cargo", BuildCargoTestArgs(filter), "cargo")
                : File.Exists(Path.Combine(root, "package.json"))
                    ? ("node", BuildNodeTestArgs(filter), "node")
                    : HasDotnetProject(root)
                        ? ("dotnet", BuildDotnetTestArgs(filter), "dotnet")
                        : null
        };
    }

    public static (string File, string Args, string Kind)? ResolveFormatCommand(string root, string target, bool checkOnly, string? path)
    {
        var fullPath = string.IsNullOrWhiteSpace(path) ? null : ProjectPathHelper.EnsureWithinProject(root, path!);
        var relativePath = fullPath is null ? null : Path.GetRelativePath(root, fullPath);

        static string BuildDotnetFormatArgs(bool verifyNoChanges, string? includePath)
        {
            var args = "format --verbosity minimal";
            if (verifyNoChanges) args += " --verify-no-changes";
            if (!string.IsNullOrWhiteSpace(includePath))
                args += " --include " + QuoteArg(includePath);
            return args;
        }

        static string BuildCargoFmtArgs(bool check)
        {
            var args = "fmt --all";
            if (check) args += " -- --check";
            return args;
        }

        static string BuildNodeFormatArgs(bool check, string? includePath)
        {
            var targetPath = string.IsNullOrWhiteSpace(includePath) ? "." : includePath;
            return check
                ? "exec prettier -- --check " + QuoteArg(targetPath)
                : "exec prettier -- --write " + QuoteArg(targetPath);
        }

        return target switch
        {
            "dotnet" => ("dotnet", BuildDotnetFormatArgs(checkOnly, relativePath), "dotnet"),
            "cargo" => ("cargo", BuildCargoFmtArgs(checkOnly), "cargo"),
            "node" => ("npm", BuildNodeFormatArgs(checkOnly, relativePath), "node"),
            _ => File.Exists(Path.Combine(root, "Cargo.toml"))
                ? ("cargo", BuildCargoFmtArgs(checkOnly), "cargo")
                : File.Exists(Path.Combine(root, "package.json"))
                    ? ("npm", BuildNodeFormatArgs(checkOnly, relativePath), "node")
                    : HasDotnetProject(root)
                        ? ("dotnet", BuildDotnetFormatArgs(checkOnly, relativePath), "dotnet")
                        : null
        };
    }

    public static (string File, string Args, string Kind)? ResolveLintCommand(string root, string target, string? path)
    {
        var fullPath = string.IsNullOrWhiteSpace(path) ? null : ProjectPathHelper.EnsureWithinProject(root, path!);
        var relativePath = fullPath is null ? null : Path.GetRelativePath(root, fullPath);

        static string BuildDotnetLintArgs()
            => "build -v minimal -p:RunAnalyzers=true";

        static string BuildCargoLintArgs()
            => "clippy --all-targets --all-features -- -D warnings";

        static string BuildNodeLintArgs(string? includePath)
        {
            var targetPath = string.IsNullOrWhiteSpace(includePath) ? "." : includePath;
            return "exec eslint -- " + QuoteArg(targetPath);
        }

        return target switch
        {
            "dotnet" => ("dotnet", BuildDotnetLintArgs(), "dotnet"),
            "cargo" => ("cargo", BuildCargoLintArgs(), "cargo"),
            "node" => ("npm", BuildNodeLintArgs(relativePath), "node"),
            _ => File.Exists(Path.Combine(root, "Cargo.toml"))
                ? ("cargo", BuildCargoLintArgs(), "cargo")
                : File.Exists(Path.Combine(root, "package.json"))
                    ? ("npm", BuildNodeLintArgs(relativePath), "node")
                    : HasDotnetProject(root)
                        ? ("dotnet", BuildDotnetLintArgs(), "dotnet")
                        : null
        };
    }

    public static (string File, string Args, string Kind)? ResolveTypecheckCommand(string root, string target)
    {
        static string BuildDotnetArgs()
            => "build -v minimal -p:RunAnalyzers=false";

        static string BuildCargoArgs()
            => "check";

        static string BuildNodeArgs()
            => "exec tsc -- --noEmit";

        return target switch
        {
            "dotnet" => ("dotnet", BuildDotnetArgs(), "dotnet"),
            "cargo" => ("cargo", BuildCargoArgs(), "cargo"),
            "node" => ("npm", BuildNodeArgs(), "node"),
            _ => File.Exists(Path.Combine(root, "Cargo.toml"))
                ? ("cargo", BuildCargoArgs(), "cargo")
                : File.Exists(Path.Combine(root, "package.json"))
                    ? ("npm", BuildNodeArgs(), "node")
                    : HasDotnetProject(root)
                        ? ("dotnet", BuildDotnetArgs(), "dotnet")
                        : null
        };
    }

    private static string MergeProcessOutput(string stdout, string stderr, int maxChars)
    {
        var text = string.IsNullOrWhiteSpace(stderr)
            ? stdout ?? ""
            : string.IsNullOrWhiteSpace(stdout)
                ? stderr
                : stdout + "\n" + stderr;
        return text.Length <= maxChars ? text : text[..maxChars];
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

    private static bool HasDotnetProject(string root)
        => Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).Any()
           || Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).Any()
           || Directory.EnumerateFiles(root, "*.fsproj", SearchOption.TopDirectoryOnly).Any();

    private static string QuoteArg(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "\"\"";
        if (value.IndexOfAny([' ', '\t', '\n', '\r', '"']) < 0) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static List<CompactDiagnostic> ParseDiagnostics(string text, int max)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var outList = new List<CompactDiagnostic>();
        var rxCs = new Regex(@"^(?<path>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>error|warning)\s*(?<code>[A-Z0-9]+):\s*(?<msg>.+)$", RegexOptions.IgnoreCase);
        var rxTs = new Regex(@"^(?<path>.+?):(?<line>\d+):(?<col>\d+)\s*-\s*(?<severity>error|warning)\s*(?<code>[A-Z]+\d+):\s*(?<msg>.+)$", RegexOptions.IgnoreCase);
        var rxRustHeader = new Regex(@"^(?<severity>error|warning)(?:\[(?<code>[A-Z0-9_]+)\])?:\s*(?<msg>.+)$", RegexOptions.IgnoreCase);
        var rxRustLoc = new Regex(@"^\s*-->\s*(?<path>.+?):(?<line>\d+):(?<col>\d+)\s*$", RegexOptions.IgnoreCase);
        string? pendingSeverity = null;
        string? pendingCode = null;
        string? pendingMessage = null;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            var mCs = rxCs.Match(trimmed);
            if (mCs.Success)
            {
                outList.Add(new CompactDiagnostic(
                    Path: mCs.Groups["path"].Value,
                    Line: int.TryParse(mCs.Groups["line"].Value, out var lnCs) ? lnCs : 0,
                    Col: int.TryParse(mCs.Groups["col"].Value, out var colCs) ? colCs : 0,
                    Severity: mCs.Groups["severity"].Value.ToLowerInvariant(),
                    Code: mCs.Groups["code"].Value,
                    Message: mCs.Groups["msg"].Value.Length <= 260 ? mCs.Groups["msg"].Value : mCs.Groups["msg"].Value[..260]));
                if (outList.Count >= max) break;
                continue;
            }

            var mTs = rxTs.Match(trimmed);
            if (mTs.Success)
            {
                outList.Add(new CompactDiagnostic(
                    Path: mTs.Groups["path"].Value,
                    Line: int.TryParse(mTs.Groups["line"].Value, out var lnTs) ? lnTs : 0,
                    Col: int.TryParse(mTs.Groups["col"].Value, out var colTs) ? colTs : 0,
                    Severity: mTs.Groups["severity"].Value.ToLowerInvariant(),
                    Code: mTs.Groups["code"].Value,
                    Message: mTs.Groups["msg"].Value.Length <= 260 ? mTs.Groups["msg"].Value : mTs.Groups["msg"].Value[..260]));
                if (outList.Count >= max) break;
                continue;
            }

            var mRustHeader = rxRustHeader.Match(trimmed);
            if (mRustHeader.Success)
            {
                pendingSeverity = mRustHeader.Groups["severity"].Value.ToLowerInvariant();
                pendingCode = mRustHeader.Groups["code"].Success ? mRustHeader.Groups["code"].Value : "";
                pendingMessage = mRustHeader.Groups["msg"].Value;
                continue;
            }

            if (pendingMessage is null) continue;
            var mRustLoc = rxRustLoc.Match(trimmed);
            if (!mRustLoc.Success) continue;

            outList.Add(new CompactDiagnostic(
                Path: mRustLoc.Groups["path"].Value,
                Line: int.TryParse(mRustLoc.Groups["line"].Value, out var ln) ? ln : 0,
                Col: int.TryParse(mRustLoc.Groups["col"].Value, out var c) ? c : 0,
                Severity: pendingSeverity ?? "error",
                Code: pendingCode ?? "",
                Message: pendingMessage.Length <= 260 ? pendingMessage : pendingMessage[..260]));
            pendingSeverity = null;
            pendingCode = null;
            pendingMessage = null;
            if (outList.Count >= max) break;
        }
        return outList;
    }
}
