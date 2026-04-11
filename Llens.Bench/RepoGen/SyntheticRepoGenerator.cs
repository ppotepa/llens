using System.Diagnostics;
using System.Text.Json;

namespace Llens.Bench.RepoGen;

public static class SyntheticRepoGenerator
{
    public static async Task<int> GenerateAsync(SyntheticRepoGenOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.OutputPath))
            throw new ArgumentException("OutputPath is required.");
        if (options.CommitCount < 1)
            throw new ArgumentOutOfRangeException(nameof(options.CommitCount), "CommitCount must be >= 1.");
        if (options.FileTarget < 1)
            throw new ArgumentOutOfRangeException(nameof(options.FileTarget), "FileTarget must be >= 1.");

        var root = Path.GetFullPath(options.OutputPath);
        PrepareOutputDirectory(root, options.Force);

        var git = "git";
        EnsureGitAvailable(git, root);
        RunGit(git, root, "init --quiet");
        EnsureSafeDirectory(git, root);
        RunGit(git, root, "config user.name \"Llens Bench Bot\"");
        RunGit(git, root, "config user.email \"bench@llens.local\"");

        var rng = new Random(options.Seed);
        var tracked = new List<TrackedFile>(capacity: Math.Max(options.FileTarget, 128));
        var createdCount = 0;
        var renameCount = 0;
        var start = new DateTimeOffset(2025, 01, 01, 9, 0, 0, TimeSpan.Zero);

        for (var commitNo = 1; commitNo <= options.CommitCount; commitNo++)
        {
            ct.ThrowIfCancellationRequested();
            var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (createdCount < options.FileTarget)
            {
                var rel = BuildNewFilePath(createdCount + 1);
                var abs = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                File.WriteAllText(abs, BuildInitialContent(rel, createdCount + 1));
                tracked.Add(new TrackedFile(rel));
                createdCount++;
                changedPaths.Add(rel);
            }

            var changes = Math.Min(1, tracked.Count);
            for (var c = 0; c < changes; c++)
            {
                var idx = (commitNo * 37 + c * 17 + options.Seed) % tracked.Count;
                var tf = tracked[idx];
                var abs = Path.Combine(root, tf.RelativePath);
                AppendMutation(abs, tf.RelativePath, commitNo);
                changedPaths.Add(tf.RelativePath);
            }

            if (commitNo % 50 == 0 && tracked.Count >= 8)
            {
                var idx = (commitNo * 13 + options.Seed) % tracked.Count;
                var current = tracked[idx];
                var renamed = BuildRenamedPath(current.RelativePath, commitNo);
                var fromAbs = Path.Combine(root, current.RelativePath);
                var toAbs = Path.Combine(root, renamed);
                Directory.CreateDirectory(Path.GetDirectoryName(toAbs)!);
                if (File.Exists(fromAbs))
                {
                    File.Move(fromAbs, toAbs, overwrite: true);
                    tracked[idx] = current with { RelativePath = renamed };
                    renameCount++;
                    changedPaths.Add(current.RelativePath);
                    changedPaths.Add(renamed);
                }
            }

            RunGit(git, root, BuildAddArguments(changedPaths));
            var message = BuildCommitMessage(commitNo, createdCount, rng);
            var when = start.AddMinutes(commitNo * 17);
            var env = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_DATE"] = when.ToString("o"),
                ["GIT_COMMITTER_DATE"] = when.ToString("o")
            };
            RunGit(git, root, $"commit --no-gpg-sign --quiet -m \"{Escape(message)}\"", env);
        }

        var head = RunGitCapture(git, root, "rev-parse --short HEAD");
        var commitCountActual = int.TryParse(RunGitCapture(git, root, "rev-list --count HEAD"), out var parsed)
            ? parsed
            : options.CommitCount;

        var manifest = new
        {
            fixture = "synthetic-history",
            seed = options.Seed,
            commitTarget = options.CommitCount,
            commitCount = commitCountActual,
            fileTarget = options.FileTarget,
            currentFileCount = tracked.Count,
            renameCount,
            head,
            generatedAtUtc = DateTimeOffset.UtcNow,
            files = tracked.Select(t => t.RelativePath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
        };

        var manifestPath = Path.Combine(root, "llens-bench-fixture.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"Generated fixture repo at: {root}");
        Console.WriteLine($"Commits: {commitCountActual}, Files: {tracked.Count}, Head: {head}");
        Console.WriteLine($"Manifest: {manifestPath}");
        await Task.CompletedTask;
        return 0;
    }

    private static void PrepareOutputDirectory(string root, bool force)
    {
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            return;
        }

        var hasEntries = Directory.EnumerateFileSystemEntries(root).Any();
        if (!hasEntries) return;
        if (!force)
            throw new InvalidOperationException($"Output path '{root}' is not empty. Use --force to overwrite.");

        SafeDeleteDirectory(root);
        Directory.CreateDirectory(root);
    }

    private static void SafeDeleteDirectory(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
            catch
            {
                // best effort
            }
        }

        var rootAttrs = File.GetAttributes(root);
        if ((rootAttrs & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(root, rootAttrs & ~FileAttributes.ReadOnly);

        Directory.Delete(root, recursive: true);
    }

    private static string BuildNewFilePath(int index)
    {
        var bucket = (index - 1) % 5;
        var module = $"m{((index - 1) % 10) + 1:D2}";
        return bucket switch
        {
            0 => Path.Combine("src", "csharp", module, $"feature_{index:D3}.cs"),
            1 => Path.Combine("src", "rust", module, $"feature_{index:D3}.rs"),
            2 => Path.Combine("src", "web", module, $"feature_{index:D3}.ts"),
            3 => Path.Combine("docs", module, $"note_{index:D3}.md"),
            _ => Path.Combine("configs", module, $"cfg_{index:D3}.json"),
        };
    }

    private static string BuildInitialContent(string relativePath, int index)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return
$@"namespace Synthetic.M{index:D3};

public static class Feature{index:D3}
{{
    public static string Run() => ""feature-{index:D3}"";
}}
";
        }

        if (normalized.EndsWith(".rs", StringComparison.OrdinalIgnoreCase))
        {
            return
$@"pub fn feature_{index:D3}() -> &'static str {{
    ""feature-{index:D3}""
}}
";
        }

        if (normalized.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            return
$@"export function feature{index:D3}(): string {{
  return ""feature-{index:D3}"";
}}
";
        }

        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return
$@"# Note {index:D3}

This document tracks synthetic feature `{index:D3}`.
";
        }

        return
$@"{{
  ""id"": {index},
  ""name"": ""feature-{index:D3}"",
  ""enabled"": true
}}
";
    }

    private static void AppendMutation(string absolutePath, string relativePath, int commitNo)
    {
        var ext = Path.GetExtension(relativePath);
        var line = ext.ToLowerInvariant() switch
        {
            ".cs" => $"// commit-{commitNo:D3}: mutation",
            ".rs" => $"// commit-{commitNo:D3}: mutation",
            ".ts" => $"// commit-{commitNo:D3}: mutation",
            ".md" => $"- commit-{commitNo:D3}: mutation",
            ".json" => $"  ,\"commit_{commitNo:D3}\": \"mutation\"",
            _ => $"# commit-{commitNo:D3}: mutation"
        };

        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            var text = File.ReadAllText(absolutePath);
            if (text.Contains($"\"commit_{commitNo:D3}\"", StringComparison.Ordinal))
                return;
            var insertAt = text.LastIndexOf('}');
            if (insertAt > 0)
            {
                var before = text[..insertAt].TrimEnd();
                var suffixComma = before.EndsWith("{", StringComparison.Ordinal) ? "" : ",";
                var mutated = $"{before}{suffixComma}\n  \"commit_{commitNo:D3}\": \"mutation\"\n}}\n";
                File.WriteAllText(absolutePath, mutated);
                return;
            }
        }

        File.AppendAllText(absolutePath, $"{Environment.NewLine}{line}{Environment.NewLine}");
    }

    private static string BuildRenamedPath(string currentPath, int commitNo)
    {
        var dir = Path.GetDirectoryName(currentPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(currentPath);
        var ext = Path.GetExtension(currentPath);
        return Path.Combine(dir, $"{name}_r{commitNo:D3}{ext}");
    }

    private static string BuildCommitMessage(int commitNo, int createdCount, Random rng)
    {
        var tag = (commitNo % 10) switch
        {
            0 => "refactor",
            1 => "feat",
            2 => "fix",
            3 => "feat",
            4 => "perf",
            5 => "chore",
            6 => "test",
            7 => "docs",
            8 => "feat",
            _ => "fix"
        };
        var scope = $"module-{((commitNo - 1) % 10) + 1:D2}";
        var n = rng.Next(100, 999);
        return $"{tag}({scope}): synthetic change {commitNo:D3} ({n}) files={createdCount}";
    }

    private static void EnsureGitAvailable(string git, string workdir)
    {
        try
        {
            RunGit(git, workdir, "--version");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Git is required to generate synthetic fixtures.", ex);
        }
    }

    private static void EnsureSafeDirectory(string git, string root)
    {
        try
        {
            var normalized = root.Replace('\\', '/');
            RunGit(git, root, $"config --global --add safe.directory \"{Escape(normalized)}\"");
        }
        catch
        {
            // Best effort: if this fails, regular git commands may still work in normal environments.
        }
    }

    private static void RunGit(string git, string workdir, string arguments, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = git,
            Arguments = arguments,
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (env is not null)
        {
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start: git {arguments}");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed ({p.ExitCode})\n{stdout}\n{stderr}");
    }

    private static string RunGitCapture(string git, string workdir, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = git,
            Arguments = arguments,
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start: git {arguments}");
        var stdout = p.StandardOutput.ReadToEnd().Trim();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed ({p.ExitCode}) {stderr}");
        return stdout;
    }

    private static string Escape(string input)
        => input.Replace("\"", "\\\"");

    private static string BuildAddArguments(IEnumerable<string> changedPaths)
    {
        var paths = changedPaths
            .Select(p => p.Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => $"\"{Escape(p)}\"")
            .ToArray();
        if (paths.Length == 0)
            return "add -A";
        return $"add -A -- {string.Join(" ", paths)}";
    }

    private sealed record TrackedFile(string RelativePath);
}
