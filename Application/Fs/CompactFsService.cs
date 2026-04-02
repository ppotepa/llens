using System.Diagnostics;
using Llens.Api;
using Llens.Models;
using Llens.Shared;

namespace Llens.Application.Fs;

public sealed class CompactFsService : ICompactFsService
{
    public CompactFsTreeOutcome Tree(Project project, CompactFsTreeRequest request)
    {
        var projectRoot = project.Config.ResolvedPath;
        var root = string.IsNullOrWhiteSpace(request.Path)
            ? projectRoot
            : ProjectPathHelper.EnsureWithinProject(projectRoot, request.Path!);
        if (root is null)
            return new CompactFsTreeOutcome(false, ErrorKind: FsErrorKind.BadRequest, ErrorMessage: "Path is outside project root.");
        if (!Directory.Exists(root))
            return new CompactFsTreeOutcome(false, ErrorKind: FsErrorKind.NotFound, ErrorMessage: "Directory not found.");
        var maxDepth = Math.Clamp(request.MaxDepth <= 0 ? 3 : request.MaxDepth, 1, 10);
        var maxEntries = Math.Clamp(request.MaxEntries <= 0 ? 600 : request.MaxEntries, 1, 20000);
        var entries = new List<CompactFsEntry>();

        Walk(root, 0);
        return new CompactFsTreeOutcome(true, new CompactFsTreeResponse(project.Name, root, entries.Count, entries));

        void Walk(string dir, int depth)
        {
            if (depth > maxDepth || entries.Count >= maxEntries) return;
            IEnumerable<string> dirs;
            IEnumerable<string> files;
            try
            {
                dirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir);
            }
            catch
            {
                return;
            }

            foreach (var d in dirs)
            {
                if (ShouldExclude(project.Config, d)) continue;
                entries.Add(new CompactFsEntry("d", d, depth));
                if (entries.Count >= maxEntries) return;
                Walk(d, depth + 1);
                if (entries.Count >= maxEntries) return;
            }

            foreach (var f in files)
            {
                if (ShouldExclude(project.Config, f)) continue;
                entries.Add(new CompactFsEntry("f", f, depth));
                if (entries.Count >= maxEntries) return;
            }
        }
    }

    public async Task<CompactFsReadRangeOutcome> ReadRangeAsync(Project project, CompactFsReadRangeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return new CompactFsReadRangeOutcome(false, ErrorKind: FsErrorKind.BadRequest, ErrorMessage: "'path' is required.");

        var full = ProjectPathHelper.EnsureWithinProject(project.Config.ResolvedPath, request.Path);
        if (full is null)
            return new CompactFsReadRangeOutcome(false, ErrorKind: FsErrorKind.BadRequest, ErrorMessage: "Path is outside project root.");
        if (!File.Exists(full))
            return new CompactFsReadRangeOutcome(false, ErrorKind: FsErrorKind.NotFound, ErrorMessage: "File not found.");

        var from = Math.Max(1, request.From <= 0 ? 1 : request.From);
        var to = Math.Max(from, request.To <= 0 ? from + 50 : request.To);
        to = Math.Min(to, from + 1000);
        var lines = await File.ReadAllLinesAsync(full, ct);
        if (lines.Length == 0)
            return new CompactFsReadRangeOutcome(true, new CompactFsReadRangeResponse(full, from, to, []));

        var actualTo = Math.Min(to, lines.Length);
        var outLines = new List<CompactLine>(capacity: actualTo - from + 1);
        for (var i = from; i <= actualTo; i++)
            outLines.Add(new CompactLine(i, lines[i - 1]));

        return new CompactFsReadRangeOutcome(true, new CompactFsReadRangeResponse(full, from, actualTo, outLines));
    }

    public CompactFsWriteFileOutcome WriteFile(Project project, CompactFsWriteFileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return new CompactFsWriteFileOutcome(false, ErrorKind: FsErrorKind.BadRequest, ErrorMessage: "'path' is required.");

        var full = ProjectPathHelper.EnsureWithinProject(project.Config.ResolvedPath, request.Path);
        if (full is null)
            return new CompactFsWriteFileOutcome(false, ErrorKind: FsErrorKind.BadRequest, ErrorMessage: "Path is outside project root.");

        if (!request.Overwrite && File.Exists(full))
            return new CompactFsWriteFileOutcome(false, ErrorKind: FsErrorKind.Conflict, ErrorMessage: "File already exists and overwrite=false.");

        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            if (!request.CreateDirs)
                return new CompactFsWriteFileOutcome(false, ErrorKind: FsErrorKind.BadRequest, ErrorMessage: "Target directory does not exist and createDirs=false.");
            Directory.CreateDirectory(dir);
        }

        var content = request.Content ?? "";
        if (request.EnsureTrailingNewline && !content.EndsWith('\n'))
            content += '\n';

        File.WriteAllText(full, content);
        var bytes = new FileInfo(full).Length;
        return new CompactFsWriteFileOutcome(true, new CompactFsWriteFileResponse(project.Name, full, content.Length, bytes, File.Exists(full)));
    }

    public async Task<CompactFsEditOutcome> EditAsync(Project project, CompactFsEditRequest request, CancellationToken ct)
    {
        if (request.Operations is null || request.Operations.Count == 0)
            return new CompactFsEditOutcome(false, ErrorKind: FsErrorKind.BadRequest, ErrorMessage: "'operations' is required.");

        var dryRun = request.DryRun;
        var results = new List<CompactFsEditOpResult>();

        foreach (var op in request.Operations.Take(200))
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(op.Path))
            {
                results.Add(new CompactFsEditOpResult(op.Path ?? "", op.Type ?? "unknown", false, "invalid op: path is required", 0, 0, 0));
                continue;
            }

            var full = ProjectPathHelper.EnsureWithinProject(project.Config.ResolvedPath, op.Path);
            if (full is null || !File.Exists(full))
            {
                results.Add(new CompactFsEditOpResult(op.Path, op.Type ?? "unknown", false, "file not found or outside project root", 0, 0, 0));
                continue;
            }

            var lines = (await File.ReadAllLinesAsync(full, ct)).ToList();
            var type = (op.Type ?? "replace_range").Trim().ToLowerInvariant();

            try
            {
                var result = type switch
                {
                    "replace_range" => ApplyReplaceRange(lines, op),
                    "insert_before" => ApplyInsertBefore(lines, op),
                    "insert_after" => ApplyInsertAfter(lines, op),
                    "delete_range" => ApplyDeleteRange(lines, op),
                    "replace_snippet" => ApplyReplaceSnippet(lines, op),
                    _ => new CompactFsEditOpResult(op.Path, type, false, $"unsupported op type '{type}'", 0, 0, 0)
                };

                if (result.Ok && !dryRun)
                    await File.WriteAllTextAsync(full, string.Join('\n', lines) + '\n', ct);

                results.Add(result);
            }
            catch (Exception ex)
            {
                results.Add(new CompactFsEditOpResult(op.Path, type, false, ex.Message, 0, 0, 0));
            }
        }

        var applied = results.Count(r => r.Ok);
        var failed = results.Count - applied;
        return new CompactFsEditOutcome(true, new CompactFsEditResponse(project.Name, dryRun, applied, failed, results));
    }

    public async Task<CompactFsDiffOutcome> DiffAsync(Project project, CompactFsDiffRequest request, CancellationToken ct)
    {
        var root = project.Config.ResolvedPath;
        var gitRoot = FindGitRoot(root);
        if (gitRoot is null)
            return new CompactFsDiffOutcome(true, new CompactFsDiffResponse(project.Name, false, request.Path, request.Staged, 0, ""));

        var args = new List<string> { "-C", gitRoot, "diff" };
        if (request.Staged) args.Add("--cached");
        if (!string.IsNullOrWhiteSpace(request.Path))
        {
            var full = ProjectPathHelper.EnsureWithinProject(root, request.Path!);
            if (full is null)
                return new CompactFsDiffOutcome(false, ErrorKind: FsErrorKind.BadRequest, ErrorMessage: "Path is outside project root.");
            var rel = Path.GetRelativePath(gitRoot, full);
            args.Add("--");
            args.Add(rel);
        }
        else
        {
            var relProjectRoot = Path.GetRelativePath(gitRoot, root);
            args.Add("--");
            args.Add(relProjectRoot);
        }

        var argLine = string.Join(" ", args.Select(QuoteArg));
        var result = await RunProcessAsync("git", argLine, gitRoot, Math.Clamp(request.TimeoutMs <= 0 ? 15000 : request.TimeoutMs, 2000, 120000), ct);
        var text = (result.Stdout ?? "") + (string.IsNullOrWhiteSpace(result.Stderr) ? "" : ("\n" + result.Stderr));

        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(request.Path))
        {
            var full = ProjectPathHelper.EnsureWithinProject(root, request.Path!);
            if (full is not null)
            {
                var rel = Path.GetRelativePath(gitRoot, full);
                var status = await RunProcessAsync("git", string.Join(" ", new[] { "-C", gitRoot, "status", "--porcelain", "--", rel }.Select(QuoteArg)), gitRoot, 8000, ct);
                var line = (status.Stdout ?? "").Trim();
                if (line.StartsWith("??", StringComparison.Ordinal))
                    text = $"UNTRACKED {rel}";
            }
        }

        var maxChars = Math.Clamp(request.MaxChars <= 0 ? 24000 : request.MaxChars, 800, 200000);
        var body = text.Length <= maxChars ? text : text[..maxChars];
        var lines = body.Length == 0 ? 0 : body.Count(c => c == '\n') + 1;
        return new CompactFsDiffOutcome(true, new CompactFsDiffResponse(project.Name, true, request.Path, request.Staged, lines, body));
    }

    public async Task<CompactGitStatusOutcome> GitStatusAsync(Project project, CompactGitStatusRequest request, CancellationToken ct)
    {
        var root = project.Config.ResolvedPath;
        var gitRoot = FindGitRoot(root);
        if (gitRoot is null)
            return new CompactGitStatusOutcome(true, new CompactGitStatusResponse(project.Name, false, request.Path, "compact", 0, [], []));

        var mode = NormalizeStatusMode(request.Mode);
        var maxEntries = Math.Clamp(request.MaxEntries <= 0 ? 500 : request.MaxEntries, 1, 20000);
        var includeUntracked = request.IncludeUntracked;

        var args = new List<string> { "-C", gitRoot, "status", "--porcelain=v1" };
        args.Add(includeUntracked ? "--untracked-files=all" : "--untracked-files=no");

        if (!string.IsNullOrWhiteSpace(request.Path))
        {
            var full = ProjectPathHelper.EnsureWithinProject(root, request.Path!);
            if (full is null)
                return new CompactGitStatusOutcome(false, ErrorKind: FsErrorKind.BadRequest, ErrorMessage: "Path is outside project root.");
            var rel = Path.GetRelativePath(gitRoot, full);
            args.Add("--");
            args.Add(rel);
        }
        else
        {
            args.Add("--");
            args.Add(Path.GetRelativePath(gitRoot, root));
        }

        var argLine = string.Join(" ", args.Select(QuoteArg));
        var result = await RunProcessAsync("git", argLine, gitRoot, Math.Clamp(request.TimeoutMs <= 0 ? 15000 : request.TimeoutMs, 2000, 120000), ct);
        var lines = (result.Stdout ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Take(maxEntries)
            .ToList();

        var entries = new List<CompactGitStatusEntry>(capacity: lines.Count);
        var compact = new List<string>(capacity: lines.Count);
        foreach (var line in lines)
        {
            if (line.Length < 3) continue;
            var xy = line[..2];
            var rawPath = line[3..].Trim();

            string? from = null;
            var path = rawPath;
            var arrow = rawPath.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow > 0)
            {
                from = rawPath[..arrow];
                path = rawPath[(arrow + 4)..];
            }

            var staged = xy[0] != ' ' && xy[0] != '?';
            var unstaged = xy[1] != ' ' && xy[1] != '?';
            var untracked = xy == "??";
            entries.Add(new CompactGitStatusEntry(path, xy, staged, unstaged, untracked, from));

            if (from is not null)
                compact.Add($"{xy} {from} -> {path}");
            else
                compact.Add($"{xy} {path}");
        }

        if (mode == "compact")
            return new CompactGitStatusOutcome(true, new CompactGitStatusResponse(project.Name, true, request.Path, mode, compact.Count, [], compact));

        return new CompactGitStatusOutcome(true, new CompactGitStatusResponse(project.Name, true, request.Path, mode, entries.Count, entries, compact));
    }

    private static bool ShouldExclude(RepoConfig config, string path)
        => config.ExcludePaths.Any(x => path.Contains($"{Path.DirectorySeparatorChar}{x}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith($"{Path.DirectorySeparatorChar}{x}", StringComparison.OrdinalIgnoreCase));

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

    private static string? FindGitRoot(string startPath)
    {
        var dir = Directory.Exists(startPath) ? Path.GetFullPath(startPath) : Path.GetDirectoryName(Path.GetFullPath(startPath));
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static string NormalizeStatusMode(string? mode)
        => (mode ?? "compact").Trim().ToLowerInvariant() switch
        {
            "full" => "full",
            _ => "compact"
        };

    private static CompactFsEditOpResult ApplyReplaceRange(List<string> lines, CompactFsEditOp op)
    {
        var start = op.StartLine ?? 0;
        var end = op.EndLine ?? 0;
        if (start <= 0 || end <= 0 || end < start)
            return new CompactFsEditOpResult(op.Path, "replace_range", false, "startLine/endLine are required and must be valid", 0, 0, 0);

        var maxLine = lines.Count == 0 ? 1 : lines.Count;
        if (start > maxLine + 1 || end > maxLine + 1)
            return new CompactFsEditOpResult(op.Path, "replace_range", false, "line range is outside file", 0, 0, 0);

        var from = Math.Max(1, start);
        var to = Math.Max(from, end);
        from = Math.Min(from, lines.Count + 1);
        to = Math.Min(to, lines.Count);

        var replacement = SplitLines(op.Content ?? "");
        var removeCount = to >= from && lines.Count > 0 ? (to - from + 1) : 0;
        if (removeCount > 0)
            lines.RemoveRange(from - 1, removeCount);
        lines.InsertRange(from - 1, replacement);

        var changed = Math.Max(removeCount, replacement.Count);
        return new CompactFsEditOpResult(op.Path, "replace_range", true, "applied", changed, from, Math.Max(from, from + replacement.Count - 1));
    }

    private static CompactFsEditOpResult ApplyInsertBefore(List<string> lines, CompactFsEditOp op)
    {
        var line = op.StartLine ?? 0;
        if (line <= 0)
            return new CompactFsEditOpResult(op.Path, "insert_before", false, "startLine is required", 0, 0, 0);

        var idx = Math.Min(Math.Max(0, line - 1), lines.Count);
        var insert = SplitLines(op.Content ?? "");
        lines.InsertRange(idx, insert);
        var from = idx + 1;
        return new CompactFsEditOpResult(op.Path, "insert_before", true, "applied", insert.Count, from, Math.Max(from, from + insert.Count - 1));
    }

    private static CompactFsEditOpResult ApplyInsertAfter(List<string> lines, CompactFsEditOp op)
    {
        var line = op.StartLine ?? 0;
        if (line < 0)
            return new CompactFsEditOpResult(op.Path, "insert_after", false, "startLine must be >= 0", 0, 0, 0);

        var idx = Math.Min(Math.Max(0, line), lines.Count);
        var insert = SplitLines(op.Content ?? "");
        lines.InsertRange(idx, insert);
        var from = idx + 1;
        return new CompactFsEditOpResult(op.Path, "insert_after", true, "applied", insert.Count, from, Math.Max(from, from + insert.Count - 1));
    }

    private static CompactFsEditOpResult ApplyDeleteRange(List<string> lines, CompactFsEditOp op)
    {
        var start = op.StartLine ?? 0;
        var end = op.EndLine ?? 0;
        if (start <= 0 || end <= 0 || end < start)
            return new CompactFsEditOpResult(op.Path, "delete_range", false, "startLine/endLine are required and must be valid", 0, 0, 0);
        if (start > lines.Count)
            return new CompactFsEditOpResult(op.Path, "delete_range", false, "startLine is outside file", 0, 0, 0);

        var from = Math.Max(1, start);
        var to = Math.Min(lines.Count, end);
        var count = to - from + 1;
        lines.RemoveRange(from - 1, count);
        return new CompactFsEditOpResult(op.Path, "delete_range", true, "applied", count, from, to);
    }

    private static CompactFsEditOpResult ApplyReplaceSnippet(List<string> lines, CompactFsEditOp op)
    {
        if (string.IsNullOrEmpty(op.Find))
            return new CompactFsEditOpResult(op.Path, "replace_snippet", false, "find is required", 0, 0, 0);

        var text = string.Join('\n', lines);
        var find = op.Find!;
        var replace = op.Content ?? "";
        var occurrence = op.Occurrence ?? 1;

        if (occurrence <= 0)
        {
            var countAll = CountOccurrences(text, find);
            if (countAll == 0)
                return new CompactFsEditOpResult(op.Path, "replace_snippet", false, "snippet not found", 0, 0, 0);

            text = text.Replace(find, replace, StringComparison.Ordinal);
        }
        else
        {
            var idx = IndexOfOccurrence(text, find, occurrence);
            if (idx < 0)
                return new CompactFsEditOpResult(op.Path, "replace_snippet", false, "snippet occurrence not found", 0, 0, 0);

            text = text[..idx] + replace + text[(idx + find.Length)..];
        }

        lines.Clear();
        lines.AddRange(text.Split('\n'));
        return new CompactFsEditOpResult(op.Path, "replace_snippet", true, "applied", 1, 0, 0);
    }

    private static List<string> SplitLines(string content)
        => content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

    private static int CountOccurrences(string source, string value)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(source)) return 0;
        var count = 0;
        var i = 0;
        while (true)
        {
            i = source.IndexOf(value, i, StringComparison.Ordinal);
            if (i < 0) break;
            count++;
            i += Math.Max(1, value.Length);
        }
        return count;
    }

    private static int IndexOfOccurrence(string source, string value, int occurrence)
    {
        if (occurrence <= 0) return -1;
        var found = 0;
        var i = 0;
        while (true)
        {
            i = source.IndexOf(value, i, StringComparison.Ordinal);
            if (i < 0) return -1;
            found++;
            if (found == occurrence) return i;
            i += Math.Max(1, value.Length);
        }
    }
}
