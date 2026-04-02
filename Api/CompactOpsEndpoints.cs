using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Llens.Caching;
using Llens.Application.Fs;
using Llens.Cli;
using Llens.Application.JsCheck;
using Llens.Models;
using Llens.Observability;
using Llens.Shared;

namespace Llens.Api;

public static class CompactOpsEndpoints
{
    public static void MapCompactOpsRoutes(this WebApplication app)
    {
        var g = app.MapGroup("/api/compact");

        g.MapPost("/regex", async (
            CompactRegexRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            QueryTelemetry telemetry,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;

            if (string.IsNullOrWhiteSpace(request.Pattern))
                return Results.BadRequest("'pattern' is required.");

            var maxFiles = Math.Clamp(request.MaxFiles <= 0 ? 200 : request.MaxFiles, 1, 5000);
            var maxMatches = Math.Clamp(request.MaxMatches <= 0 ? 250 : request.MaxMatches, 1, 10000);
            var opts = RegexOptions.Compiled;
            if (request.IgnoreCase) opts |= RegexOptions.IgnoreCase;
            if (request.MultiLine) opts |= RegexOptions.Multiline;

            Regex regex;
            try { regex = new Regex(request.Pattern, opts, TimeSpan.FromMilliseconds(250)); }
            catch (Exception ex) { return Results.BadRequest($"Invalid regex: {ex.Message}"); }

            var files = (await cache.GetAllFilesAsync(project!.Name, ct))
                .Where(f => PathMatches(f.FilePath, request.PathPrefix))
                .Take(maxFiles)
                .ToList();

            var matches = new List<CompactRegexMatch>(capacity: Math.Min(maxMatches, 512));
            foreach (var f in files)
            {
                if (!File.Exists(f.FilePath)) continue;
                var lines = await File.ReadAllLinesAsync(f.FilePath, ct);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!regex.IsMatch(lines[i])) continue;
                    matches.Add(new CompactRegexMatch(f.FilePath, i + 1, Truncate(lines[i].Trim(), 240)));
                    if (matches.Count >= maxMatches) break;
                }
                if (matches.Count >= maxMatches) break;
            }

            telemetry.Record("/api/compact/regex", "regex", project.Name, sw.ElapsedMilliseconds, matches.Count, matches.Count == 0, false, EstimateTokens(matches));
            return Results.Ok(new CompactRegexResponse(project.Name, request.Pattern, matches.Count, matches));
        });

        g.MapPost("/rg", async (
            CompactRgRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            QueryTelemetry telemetry,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            if (string.IsNullOrWhiteSpace(request.Pattern))
                return Results.BadRequest("'pattern' is required.");

            var proj = project!;
            var mode = (request.Mode ?? "regex").Trim().ToLowerInvariant();
            if (mode is not ("regex" or "literal"))
                return Results.BadRequest("'mode' must be 'regex' or 'literal'.");

            var maxFiles = Math.Clamp(request.MaxFiles <= 0 ? 300 : request.MaxFiles, 1, 5000);
            var maxMatches = Math.Clamp(request.MaxMatches <= 0 ? 300 : request.MaxMatches, 1, 20000);
            var maxLineLength = Math.Clamp(request.MaxLineLength <= 0 ? 240 : request.MaxLineLength, 40, 4000);

            Regex? regex = null;
            if (mode == "regex")
            {
                var opts = RegexOptions.Compiled;
                if (request.IgnoreCase) opts |= RegexOptions.IgnoreCase;
                if (request.MultiLine) opts |= RegexOptions.Multiline;
                try { regex = new Regex(request.Pattern, opts, TimeSpan.FromMilliseconds(250)); }
                catch (Exception ex) { return Results.BadRequest($"Invalid regex: {ex.Message}"); }
            }

            var comparison = request.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var files = (await cache.GetAllFilesAsync(proj.Name, ct))
                .Where(f => PathMatchesProject(proj.Config.ResolvedPath, f.FilePath, request.PathPrefix))
                .Take(maxFiles)
                .ToList();

            var hits = new List<CompactRgHit>(capacity: Math.Min(maxMatches, 512));
            foreach (var f in files)
            {
                if (!File.Exists(f.FilePath)) continue;
                var lines = await File.ReadAllLinesAsync(f.FilePath, ct);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var col = 0;
                    if (mode == "regex")
                    {
                        var match = regex!.Match(line);
                        if (!match.Success) continue;
                        col = match.Index + 1;
                    }
                    else
                    {
                        var idx = line.IndexOf(request.Pattern, comparison);
                        if (idx < 0) continue;
                        col = idx + 1;
                    }

                    hits.Add(new CompactRgHit(f.FilePath, i + 1, col, Truncate(line.Trim(), maxLineLength)));
                    if (hits.Count >= maxMatches) break;
                }
                if (hits.Count >= maxMatches) break;
            }

            var response = new CompactRgResponse(proj.Name, mode, request.Pattern, hits.Count, hits);
            telemetry.Record("/api/compact/rg", mode, proj.Name, sw.ElapsedMilliseconds, hits.Count, hits.Count == 0, false, EstimateTokens(hits));

            if (string.Equals(request.Format?.Trim(), "tuple", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(CompactTupleCodec.FromRg(response));
            return Results.Ok(response);
        });

        g.MapPost("/replace-plan", async (
            CompactReplacePlanRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            QueryTelemetry telemetry,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            if (string.IsNullOrWhiteSpace(request.Find))
                return Results.BadRequest("'find' is required.");

            var maxFiles = Math.Clamp(request.MaxFiles <= 0 ? 120 : request.MaxFiles, 1, 2000);
            var maxMatches = Math.Clamp(request.MaxMatches <= 0 ? 300 : request.MaxMatches, 1, 12000);
            var mode = (request.Mode ?? "literal").Trim().ToLowerInvariant();

            var files = (await cache.GetAllFilesAsync(project!.Name, ct))
                .Where(f => PathMatches(f.FilePath, request.PathPrefix))
                .Take(maxFiles)
                .ToList();

            var changes = new List<CompactReplacePlannedFile>();
            var total = 0;

            Regex? regex = null;
            if (mode == "regex")
            {
                try { regex = new Regex(request.Find, request.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromMilliseconds(250)); }
                catch (Exception ex) { return Results.BadRequest($"Invalid regex: {ex.Message}"); }
            }

            foreach (var file in files)
            {
                if (!File.Exists(file.FilePath)) continue;
                var lines = await File.ReadAllLinesAsync(file.FilePath, ct);
                var fileMatches = new List<CompactReplacePlannedMatch>();
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    int count;
                    if (mode == "regex")
                    {
                        var mc = regex!.Matches(line);
                        count = mc.Count;
                    }
                    else
                    {
                        count = CountOccurrences(line, request.Find, request.IgnoreCase);
                    }

                    if (count <= 0) continue;
                    fileMatches.Add(new CompactReplacePlannedMatch(i + 1, count, Truncate(line.Trim(), 240)));
                    total += count;
                    if (total >= maxMatches) break;
                }
                if (fileMatches.Count > 0)
                    changes.Add(new CompactReplacePlannedFile(file.FilePath, fileMatches.Sum(m => m.Count), fileMatches.Take(24).ToList()));
                if (total >= maxMatches) break;
            }

            telemetry.Record("/api/compact/replace-plan", "replace-plan", project.Name, sw.ElapsedMilliseconds, total, total == 0, false, EstimateTokens(changes));
            return Results.Ok(new CompactReplacePlanResponse(project.Name, mode, request.Find, request.Replace ?? "", total, changes));
        });

        g.MapPost("/fs/tree", (
            CompactFsTreeRequest request,
            ProjectRegistry projects,
            ICompactFsService fsService) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            var outcome = fsService.Tree(project!, request);
            if (!outcome.Ok)
            {
                return outcome.ErrorKind switch
                {
                    FsErrorKind.BadRequest => Results.BadRequest(outcome.ErrorMessage ?? "Invalid request."),
                    FsErrorKind.NotFound => Results.NotFound(outcome.ErrorMessage ?? "Directory not found."),
                    _ => Results.BadRequest(outcome.ErrorMessage ?? "fs/tree failed.")
                };
            }
            return Results.Ok(outcome.Response);
        });

        g.MapPost("/fs/read-range", async (
            CompactFsReadRangeRequest request,
            ProjectRegistry projects,
            ICompactFsService fsService,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            var outcome = await fsService.ReadRangeAsync(project!, request, ct);
            if (!outcome.Ok)
            {
                return outcome.ErrorKind switch
                {
                    FsErrorKind.BadRequest => Results.BadRequest(outcome.ErrorMessage ?? "Invalid request."),
                    FsErrorKind.NotFound => Results.NotFound(outcome.ErrorMessage ?? "File not found."),
                    _ => Results.BadRequest(outcome.ErrorMessage ?? "read-range failed.")
                };
            }
            return Results.Ok(outcome.Response);
        });

        g.MapPost("/fs/read-many", async (
            CompactFsReadManyRequest request,
            ProjectRegistry projects,
            ICompactFsService fsService,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            if (request.Paths is null || request.Paths.Count == 0)
                return Results.BadRequest("'paths' is required.");

            var maxFiles = Math.Clamp(request.MaxFiles <= 0 ? 80 : request.MaxFiles, 1, 400);
            var from = request.From <= 0 ? 1 : request.From;
            var to = request.To <= 0 ? from + 200 : request.To;

            if (string.Equals(request.Format?.Trim(), "raw-lossless", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await BuildRawLosslessPayloadAsync(project!, request.Paths, maxFiles, ct);
                return Results.File(payload, "application/octet-stream");
            }
            if (string.Equals(request.Format?.Trim(), "raw-ordered", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await BuildRawOrderedPayloadAsync(project!, request.Paths, maxFiles, ct);
                return Results.File(payload, "application/octet-stream");
            }

            var files = new List<CompactFsReadRangeResponse>(capacity: Math.Min(request.Paths.Count, maxFiles));
            foreach (var path in request.Paths.Where(p => !string.IsNullOrWhiteSpace(p)).Take(maxFiles))
            {
                var one = new CompactFsReadRangeRequest(request.Project, path, from, to, request.Format);
                var outcome = await fsService.ReadRangeAsync(project!, one, ct);
                if (outcome.Ok && outcome.Response is not null)
                    files.Add(outcome.Response);
            }

            if (string.Equals(request.Format?.Trim(), "tuple", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(CompactTupleCodec.FromFsReadMany(request.Project, from, to, files));

            return Results.Ok(new CompactFsReadManyResponse(request.Project, files.Count, files));
        });

        g.MapPost("/fs/read-spans", async (
            CompactFsReadSpansRequest request,
            ProjectRegistry projects,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            if (request.Spans is null || request.Spans.Count == 0)
                return Results.BadRequest("'spans' is required.");

            var maxSpans = Math.Clamp(request.MaxSpans <= 0 ? 120 : request.MaxSpans, 1, 1000);
            var maxLinesPerSpan = Math.Clamp(request.MaxLinesPerSpan <= 0 ? 220 : request.MaxLinesPerSpan, 1, 1200);
            var chunks = new List<CompactFsSpanChunk>(capacity: Math.Min(maxSpans, request.Spans.Count));
            var root = project!.Config.ResolvedPath;

            foreach (var span in request.Spans.Where(s => !string.IsNullOrWhiteSpace(s.Path)).Take(maxSpans))
            {
                ct.ThrowIfCancellationRequested();
                var full = ProjectPathHelper.EnsureWithinProject(root, span.Path);
                if (full is null || !File.Exists(full))
                    continue;

                var from = Math.Max(1, span.From <= 0 ? 1 : span.From);
                var to = Math.Max(from, span.To <= 0 ? from + 80 : span.To);
                to = Math.Min(to, from + maxLinesPerSpan - 1);

                var lines = await File.ReadAllLinesAsync(full, ct);
                if (lines.Length == 0) continue;

                var actualTo = Math.Min(to, lines.Length);
                if (from > actualTo) continue;
                var text = string.Join('\n', lines[(from - 1)..actualTo]);
                chunks.Add(new CompactFsSpanChunk(full, from, actualTo, text));
            }

            if (string.Equals(request.Format?.Trim(), "tuple", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(CompactTupleCodec.FromFsReadSpans(request.Project, chunks));

            return Results.Ok(new CompactFsReadSpansResponse(request.Project, chunks.Count, chunks));
        });

        g.MapPost("/fs/write-file", (
            CompactFsWriteFileRequest request,
            ProjectRegistry projects,
            ICompactFsService fsService) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            var outcome = fsService.WriteFile(project!, request);
            if (!outcome.Ok)
            {
                return outcome.ErrorKind switch
                {
                    FsErrorKind.BadRequest => Results.BadRequest(outcome.ErrorMessage ?? "Invalid request."),
                    FsErrorKind.Conflict => Results.Conflict(outcome.ErrorMessage ?? "Conflict."),
                    _ => Results.BadRequest(outcome.ErrorMessage ?? "write-file failed.")
                };
            }
            return Results.Ok(outcome.Response);
        });

        g.MapPost("/fs/edit", async (
            CompactFsEditRequest request,
            ProjectRegistry projects,
            ICompactFsService fsService,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;

            var outcome = await fsService.EditAsync(project!, request, ct);
            if (!outcome.Ok)
            {
                return outcome.ErrorKind switch
                {
                    FsErrorKind.BadRequest => Results.BadRequest(outcome.ErrorMessage ?? "Invalid request."),
                    _ => Results.BadRequest(outcome.ErrorMessage ?? "fs/edit failed.")
                };
            }
            return Results.Ok(outcome.Response);
        });

        g.MapPost("/fs/write-patch", (
            CompactFsWritePatchRequest request,
            ProjectRegistry projects) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            if (request.Operations is null || request.Operations.Count == 0)
                return Results.BadRequest("'operations' is required.");

            var dryRun = request.DryRun;
            var results = new List<CompactPatchOpResult>();
            foreach (var op in request.Operations.Take(200))
            {
                if (string.IsNullOrWhiteSpace(op.Path) || string.IsNullOrWhiteSpace(op.Find))
                {
                    results.Add(new CompactPatchOpResult(op.Path ?? "", false, "invalid op: path/find required", 0));
                    continue;
                }

                var full = ProjectPathHelper.EnsureWithinProject(project!.Config.ResolvedPath, op.Path);
                if (full is null || !File.Exists(full))
                {
                    results.Add(new CompactPatchOpResult(op.Path, false, "file not found or outside project root", 0));
                    continue;
                }

                var content = File.ReadAllText(full);
                var updated = content;
                var changed = 0;
                if ((op.Mode ?? "literal").Equals("regex", StringComparison.OrdinalIgnoreCase))
                {
                    Regex rx;
                    try { rx = new Regex(op.Find, op.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromMilliseconds(250)); }
                    catch (Exception ex)
                    {
                        results.Add(new CompactPatchOpResult(op.Path, false, $"regex error: {ex.Message}", 0));
                        continue;
                    }
                    updated = op.ReplaceAll ? rx.Replace(content, op.Replace ?? "") : rx.Replace(content, op.Replace ?? "", 1);
                    changed = Math.Abs((updated.Length - content.Length)) > 0 ? 1 : 0;
                }
                else
                {
                    if (op.ReplaceAll)
                    {
                        changed = CountOccurrences(content, op.Find, op.IgnoreCase);
                        updated = ReplaceLiteral(content, op.Find, op.Replace ?? "", op.IgnoreCase, true);
                    }
                    else
                    {
                        changed = CountOccurrences(content, op.Find, op.IgnoreCase) > 0 ? 1 : 0;
                        updated = ReplaceLiteral(content, op.Find, op.Replace ?? "", op.IgnoreCase, false);
                    }
                }

                if (!dryRun && changed > 0)
                    File.WriteAllText(full, updated);

                results.Add(new CompactPatchOpResult(op.Path, true, dryRun ? "planned" : "applied", changed));
            }

            return Results.Ok(new CompactFsWritePatchResponse(project!.Name, dryRun, results.Sum(r => r.Changes), results));
        });

        g.MapPost("/fs/diff", async (
            CompactFsDiffRequest request,
            ProjectRegistry projects,
            ICompactFsService fsService,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            var outcome = await fsService.DiffAsync(project!, request, ct);
            if (!outcome.Ok)
            {
                return outcome.ErrorKind switch
                {
                    FsErrorKind.BadRequest => Results.BadRequest(outcome.ErrorMessage ?? "Invalid request."),
                    _ => Results.BadRequest(outcome.ErrorMessage ?? "diff failed.")
                };
            }
            return Results.Ok(outcome.Response);
        });

        g.MapPost("/git/history", async (
            CompactGitHistoryRequest request,
            ProjectRegistry projects,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            var proj = project!;
            var scopeOpt = ResolveGitScope(proj, request.Path);
            if (scopeOpt is null) return Results.Ok(new CompactGitHistoryResponse(proj.Name, false, request.Mode, []));
            var scope = scopeOpt.Value;

            var maxCommits = Math.Clamp(request.MaxCommits <= 0 ? 20 : request.MaxCommits, 1, 200);
            var mode = NormalizeGitMode(request.Mode);
            var args = new List<string> { "-C", scope.GitRoot, "log", "--date=iso", $"--pretty=format:%H%x1f%ad%x1f%an%x1f%s", "-n", maxCommits.ToString() };
            if (!string.IsNullOrWhiteSpace(request.Since)) args.Add($"--since={request.Since}");
            if (!string.IsNullOrWhiteSpace(request.Until)) args.Add($"--until={request.Until}");
            args.Add("--");
            args.Add(scope.PathSpec);

            var log = await RunProcessAsync("git", string.Join(" ", args.Select(QuoteArg)), scope.GitRoot, 30000, ct);
            var commits = new List<CompactGitCommit>();
            foreach (var line in (log.Stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('\x1f');
                if (p.Length < 4) continue;
                var commit = await BuildCommitSummaryAsync(scope.GitRoot, p[0], p[1], p[2], p[3], mode, request.Path, ct);
                commits.Add(commit);
                if (commits.Count >= maxCommits) break;
            }

            return Results.Ok(new CompactGitHistoryResponse(proj.Name, true, mode, commits));
        });

        g.MapPost("/git/commit", async (
            CompactGitCommitRequest request,
            ProjectRegistry projects,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            var proj = project!;
            if (string.IsNullOrWhiteSpace(request.CommitId))
                return Results.BadRequest("'commitId' is required.");
            var scopeOpt = ResolveGitScope(proj, request.Path);
            if (scopeOpt is null) return Results.Ok(new CompactGitCommitResponse(proj.Name, false, null));
            var scope = scopeOpt.Value;

            var meta = await RunProcessAsync("git",
                string.Join(" ", new[] { "-C", scope.GitRoot, "show", "-s", "--date=iso", "--pretty=format:%H%x1f%ad%x1f%an%x1f%s", request.CommitId }.Select(QuoteArg)),
                scope.GitRoot, 15000, ct);
            var parts = (meta.Stdout ?? "").Split('\x1f');
            if (parts.Length < 4) return Results.NotFound("Commit not found.");

            var mode = NormalizeGitMode(request.Mode);
            var commit = await BuildCommitSummaryAsync(scope.GitRoot, parts[0], parts[1], parts[2], parts[3], mode, request.Path, ct);
            return Results.Ok(new CompactGitCommitResponse(proj.Name, true, commit));
        });

        g.MapPost("/git/patch", async (
            CompactGitPatchRequest request,
            ProjectRegistry projects,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            var proj = project!;
            if (string.IsNullOrWhiteSpace(request.CommitId))
                return Results.BadRequest("'commitId' is required.");
            var scopeOpt = ResolveGitScope(proj, request.Path);
            if (scopeOpt is null) return Results.Ok(new CompactGitPatchResponse(proj.Name, false, request.CommitId, request.Path, ""));
            var scope = scopeOpt.Value;

            var args = new List<string> { "-C", scope.GitRoot, "show", request.CommitId, "--" };
            args.Add(scope.PathSpec);
            var res = await RunProcessAsync("git", string.Join(" ", args.Select(QuoteArg)), scope.GitRoot, 30000, ct);
            var text = (res.Stdout ?? "") + (string.IsNullOrWhiteSpace(res.Stderr) ? "" : ("\n" + res.Stderr));
            var max = Math.Clamp(request.MaxChars <= 0 ? 30000 : request.MaxChars, 1000, 250000);
            if (text.Length > max) text = text[..max];
            return Results.Ok(new CompactGitPatchResponse(proj.Name, true, request.CommitId, request.Path, text));
        });

        g.MapPost("/git/status", async (
            CompactGitStatusRequest request,
            ProjectRegistry projects,
            ICompactFsService fsService,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;

            var outcome = await fsService.GitStatusAsync(project!, request, ct);
            if (!outcome.Ok)
            {
                return outcome.ErrorKind switch
                {
                    FsErrorKind.BadRequest => Results.BadRequest(outcome.ErrorMessage ?? "Invalid request."),
                    _ => Results.BadRequest(outcome.ErrorMessage ?? "git/status failed.")
                };
            }

            if (string.Equals(request.Format?.Trim(), "tuple", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(CompactTupleCodec.FromGitStatus(outcome.Response!));
            return Results.Ok(outcome.Response);
        });

        g.MapPost("/deps", async (
            CompactDepsRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;

            if (request.Seed is null)
                return Results.BadRequest("'seed' is required.");

            var filePath = request.Seed.Type?.Equals("file", StringComparison.OrdinalIgnoreCase) == true
                ? CompactSearchEngine.NormalizeFileSeed(request.Seed.Path ?? request.Seed.Id)
                : null;

            var proj = project!;
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var file = await ResolveIndexedFileNodeAsync(cache, proj, filePath!, ct);
                if (file is null) return Results.NotFound("Seed file not indexed.");
                var deps = file.Imports.Take(300).Select(p => new CompactDep("import", file.FilePath, p)).ToList();
                var rev = (await cache.GetDependentsAsync(file.FilePath, proj.Name, ct)).Take(300).Select(p => new CompactDep("dependent", p.FilePath, file.FilePath)).ToList();
                return Results.Ok(new CompactDepsResponse(proj.Name, $"file:{file.FilePath}", deps.Count + rev.Count, [.. deps, .. rev]));
            }

            var symbol = await ResolveSymbolSeedAsync(cache, proj.Name, request.Seed, ct);
            if (symbol is null) return Results.NotFound("Symbol seed not indexed.");
            var refs = await cache.QueryReferencesAsync(symbol.Sid!, proj.Name, ct);
            var outDeps = refs.Take(400).Select(r => new CompactDep("reference", symbol.P!, r.InFilePath)).ToList();
            return Results.Ok(new CompactDepsResponse(proj.Name, symbol.Id, outDeps.Count, outDeps));
        });

        g.MapPost("/diagnostics", async (
            CompactDiagnosticsRequest request,
            ProjectRegistry projects,
            QueryTelemetry telemetry,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;

            var root = project!.Config.ResolvedPath;
            var timeoutMs = Math.Clamp(request.TimeoutMs <= 0 ? 60000 : request.TimeoutMs, 2000, 300000);
            var target = request.Target?.Trim().ToLowerInvariant() ?? "auto";

            var cmd = target switch
            {
                "dotnet" => ("dotnet", "build -v minimal"),
                "cargo" => ("cargo", "check"),
                _ => File.Exists(Path.Combine(root, "Cargo.toml"))
                    ? ("cargo", "check")
                    : ("dotnet", "build -v minimal")
            };

            var result = await RunProcessAsync(cmd.Item1, cmd.Item2, root, timeoutMs, ct);
            var diags = ParseDiagnostics(result.Stdout + "\n" + result.Stderr, 300);
            var response = new CompactDiagnosticsResponse(project.Name, cmd.Item1, result.ExitCode, diags.Count, diags);

            telemetry.Record("/api/compact/diagnostics", cmd.Item1, project.Name, sw.ElapsedMilliseconds, diags.Count, diags.Count == 0, false, EstimateTokens(diags));
            return Results.Ok(response);
        });

        g.MapPost("/test/map", async (
            CompactTestMapRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            var q = request.Q?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(q) && request.Seed is null)
                return Results.BadRequest("'q' or 'seed' is required.");

            var tokens = CompactSearchEngine.Tokenize(q);
            if (request.Seed?.Name is { Length: > 0 }) tokens.Add(request.Seed.Name);
            tokens = [.. tokens.Distinct(StringComparer.OrdinalIgnoreCase)];

            var files = await cache.GetAllFilesAsync(project!.Name, ct);
            var mapped = files
                .Where(f => f.FilePath.Contains("test", StringComparison.OrdinalIgnoreCase)
                            || f.FilePath.Contains("spec", StringComparison.OrdinalIgnoreCase))
                .Select(f => new
                {
                    f.FilePath,
                    Score = tokens.Sum(t => f.FilePath.Contains(t, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(request.Limit <= 0 ? 20 : request.Limit, 1, 200))
                .Select(x => new CompactTestCandidate(x.FilePath, x.Score))
                .ToList();

            return Results.Ok(new CompactTestMapResponse(project.Name, mapped.Count, mapped));
        });

        g.MapPost("/test/run", async (
            CompactTestRunRequest request,
            ProjectRegistry projects,
            QueryTelemetry telemetry,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;

            var proj = project!;
            var target = request.Target?.Trim().ToLowerInvariant() ?? "auto";
            var filter = request.Filter?.Trim();
            var command = CompactOpsCommandBridge.ResolveTestCommand(proj.Config.ResolvedPath, target, filter);
            if (command is null)
                return Results.BadRequest("Could not determine test command for project.");

            var response = await CompactOpsCommandBridge.RunTestAsync(proj, request, ct);

            telemetry.Record("/api/compact/test/run", command.Value.Kind, proj.Name, sw.ElapsedMilliseconds, response.Count, response.ExitCode != 0, false, EstimateTokens(response.Diagnostics) + EstimateTokens(response.Output));
            return Results.Ok(response);
        });

        g.MapPost("/session/plan", (
            CompactSessionPlanRequest request,
            CompactSessionStore store) =>
        {
            if (string.IsNullOrWhiteSpace(request.SessionId))
                return Results.BadRequest("'sessionId' is required.");

            if (!string.IsNullOrWhiteSpace(request.SetGoal))
                store.SetGoal(request.SessionId, request.SetGoal);
            if (request.AppendStep is { Length: > 0 })
                store.AppendStep(request.SessionId, request.AppendStep);
            if (request.SetState is { Length: > 0 })
                store.SetState(request.SessionId, request.SetState);

            return Results.Ok(store.Get(request.SessionId));
        });

        g.MapGet("/session/plan", (string sessionId, CompactSessionStore store) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return Results.BadRequest("'sessionId' is required.");
            return Results.Ok(store.Get(sessionId));
        });

        g.MapPost("/js/check", async (
            CompactJsCheckRequest request,
            ProjectRegistry projects,
            IJsCheckService jsCheckService,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;

            var outcome = await jsCheckService.RunAsync(project!, request, ct);
            if (!outcome.Ok)
            {
                return outcome.ErrorKind switch
                {
                    JsCheckErrorKind.BadRequest => Results.BadRequest(outcome.ErrorMessage ?? "Invalid request."),
                    JsCheckErrorKind.NotFound => Results.NotFound(outcome.ErrorMessage ?? "Path not found."),
                    _ => Results.BadRequest(outcome.ErrorMessage ?? "JS check failed.")
                };
            }

            return Results.Ok(outcome.Response);
        });

        g.MapPost("/format", async (
            CompactFormatRequest request,
            ProjectRegistry projects,
            QueryTelemetry telemetry,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;

            var proj = project!;
            var target = request.Target?.Trim().ToLowerInvariant() ?? "auto";
            var checkOnly = request.CheckOnly;
            var command = CompactOpsCommandBridge.ResolveFormatCommand(proj.Config.ResolvedPath, target, checkOnly, request.Path);
            if (command is null)
                return Results.BadRequest("Could not determine format command for project.");

            var response = await CompactOpsCommandBridge.RunFormatAsync(proj, request, ct);

            telemetry.Record("/api/compact/format", command.Value.Kind, proj.Name, sw.ElapsedMilliseconds, response.ExitCode, response.ExitCode != 0, false, EstimateTokens(response.Output));
            return Results.Ok(response);
        });

        g.MapPost("/lint", async (
            CompactLintRequest request,
            ProjectRegistry projects,
            QueryTelemetry telemetry,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;

            var proj = project!;
            var target = request.Target?.Trim().ToLowerInvariant() ?? "auto";
            var command = CompactOpsCommandBridge.ResolveLintCommand(proj.Config.ResolvedPath, target, request.Path);
            if (command is null)
                return Results.BadRequest("Could not determine lint command for project.");

            var response = await CompactOpsCommandBridge.RunLintAsync(proj, request, ct);

            telemetry.Record("/api/compact/lint", command.Value.Kind, proj.Name, sw.ElapsedMilliseconds, response.Count, response.ExitCode != 0, false, EstimateTokens(response.Diagnostics) + EstimateTokens(response.Output));
            return Results.Ok(response);
        });

        g.MapPost("/typecheck", async (
            CompactTypecheckRequest request,
            ProjectRegistry projects,
            QueryTelemetry telemetry,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;

            var proj = project!;
            var target = request.Target?.Trim().ToLowerInvariant() ?? "auto";
            var command = CompactOpsCommandBridge.ResolveTypecheckCommand(proj.Config.ResolvedPath, target);
            if (command is null)
                return Results.BadRequest("Could not determine typecheck command for project.");

            var response = await CompactOpsCommandBridge.RunTypecheckAsync(proj, request, ct);

            telemetry.Record("/api/compact/typecheck", command.Value.Kind, proj.Name, sw.ElapsedMilliseconds, response.Count, response.ExitCode != 0, false, EstimateTokens(response.Diagnostics) + EstimateTokens(response.Output));
            return Results.Ok(response);
        });

        g.MapPost("/reindex", async (
            CompactReindexRequest request,
            ProjectRegistry projects,
            Application.Reindex.ICompactReindexService reindexService,
            QueryTelemetry telemetry,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;

            var proj = project!;

            try
            {
                var response = await reindexService.RunAsync(proj, request, ct);
                telemetry.Record("/api/compact/reindex", response.Mode, proj.Name, sw.ElapsedMilliseconds, response.Indexed, response.Indexed == 0, false, 0);
                return Results.Ok(response);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound("Path not found.");
            }
            catch (Exception ex)
            {
                return Results.BadRequest($"reindex failed: {ex.Message}");
            }
        });

        g.MapPost("/dev/serve", (
            CompactDevServeRequest request,
            ProjectRegistry projects,
            CompactDevServerStore servers) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;

            var root = project!.Config.ResolvedPath;
            var dirPath = string.IsNullOrWhiteSpace(request.Path) ? root : ProjectPathHelper.EnsureWithinProject(root, request.Path!);
            if (dirPath is null) return Results.BadRequest("Path is outside project root.");
            if (!Directory.Exists(dirPath)) return Results.NotFound("Directory not found.");

            var host = string.IsNullOrWhiteSpace(request.Host) ? "127.0.0.1" : request.Host!.Trim();
            var port = Math.Clamp(request.Port <= 0 ? 5173 : request.Port, 1024, 65535);
            var serverId = string.IsNullOrWhiteSpace(request.ServerId)
                ? $"{project.Name}:{Path.GetRelativePath(root, dirPath)}:{host}:{port}"
                : request.ServerId.Trim();

            if (servers.TryGet(serverId, out var existing) && !existing.Process.HasExited)
            {
                if (request.ReuseIfRunning)
                    return Results.Ok(new CompactDevServeResponse(project.Name, serverId, true, existing.Process.Id, existing.Command, $"http://{host}:{port}/", false));

                servers.Stop(serverId);
            }

            Process process;
            string command;
            try
            {
                (process, command) = StartPythonServer(dirPath, host, port, preferPython3: true);
            }
            catch
            {
                try
                {
                    (process, command) = StartPythonServer(dirPath, host, port, preferPython3: false);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest($"Failed to start static server: {ex.Message}");
                }
            }

            var info = new CompactDevServerInfo(serverId, project.Name, dirPath, host, port, command, process);
            servers.Set(serverId, info);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => servers.Remove(serverId);

            return Results.Ok(new CompactDevServeResponse(project.Name, serverId, true, process.Id, command, $"http://{host}:{port}/", true));
        });

        g.MapPost("/dev/serve/stop", (
            CompactDevServeStopRequest request,
            CompactDevServerStore servers) =>
        {
            if (string.IsNullOrWhiteSpace(request.ServerId))
                return Results.BadRequest("'serverId' is required.");
            var stopped = servers.Stop(request.ServerId);
            return Results.Ok(new CompactDevServeStopResponse(request.ServerId, stopped));
        });

        g.MapPost("/quality/guard", async (
            CompactQualityGuardRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;

            var warnings = new List<string>();
            var infos = new List<string>();

            var maxTokens = request.TokenBudget <= 0 ? 1200 : request.TokenBudget;
            if (maxTokens > 2200) warnings.Add("Token budget is high; prefer compact/balanced retrieval for iteration.");
            if (request.ChangedFiles?.Length > 20) warnings.Add("Large changed file set; run scoped diagnostics and test-map first.");
            if (request.ChangedFiles is { Length: > 0 })
            {
                var files = await cache.GetAllFilesAsync(project!.Name, ct);
                var missing = request.ChangedFiles.Count(f => !files.Any(x => x.FilePath.Equals(f, StringComparison.OrdinalIgnoreCase)));
                if (missing > 0) warnings.Add($"{missing} changed file(s) are not indexed yet.");
            }

            infos.Add("Use /api/compact/resolve before context-pack for deterministic seed selection.");
            infos.Add("Use /api/compact/context-pack with previousIds to minimize repeated tokens.");
            return Results.Ok(new CompactQualityGuardResponse(project!.Name, warnings, infos));
        });

        g.MapPost("/semantic-search", async (
            CompactSemanticSearchRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            if (string.IsNullOrWhiteSpace(request.Q))
                return Results.BadRequest("'q' is required.");

            var q = request.Q.Trim();
            var tokens = CompactSearchEngine.Tokenize(q);
            var candidates = new Dictionary<string, (CodeSymbol S, int Score)>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tokens.Take(16))
            {
                var hits = await cache.QueryByNameAsync(t, project!.Name, ct);
                foreach (var s in hits.Take(200))
                {
                    var score = tokens.Sum(x =>
                        (s.Name.Contains(x, StringComparison.OrdinalIgnoreCase) ? 4 : 0)
                        + (!string.IsNullOrWhiteSpace(s.Signature) && s.Signature.Contains(x, StringComparison.OrdinalIgnoreCase) ? 2 : 0)
                        + (s.FilePath.Contains(x, StringComparison.OrdinalIgnoreCase) ? 1 : 0));
                    if (score <= 0) continue;
                    if (!candidates.TryGetValue(s.Id, out var prev) || score > prev.Score)
                        candidates[s.Id] = (s, score);
                }
            }

            var limit = Math.Clamp(request.Limit <= 0 ? 20 : request.Limit, 1, 200);
            var outItems = candidates.Values
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.S.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(x => new CompactItem($"symbol:{x.S.Id}", "s", x.S.Name, x.S.FilePath, x.S.LineStart, x.S.Kind.ToString(), x.Score))
                .ToList();

            return Results.Ok(new CompactQueryResponse
            {
                Project = project!.Name,
                Mode = "semantic",
                Q = q,
                Count = outItems.Count,
                Tokens = EstimateTokens(outItems),
                Items = outItems
            });
        });

        g.MapPost("/refactor/rename-plan", async (
            CompactRenamePlanRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            if (string.IsNullOrWhiteSpace(request.SymbolName) || string.IsNullOrWhiteSpace(request.NewName))
                return Results.BadRequest("'symbolName' and 'newName' are required.");

            var symbols = (await cache.QueryByNameAsync(request.SymbolName, project!.Name, ct))
                .Where(s => s.Name.Equals(request.SymbolName, StringComparison.OrdinalIgnoreCase))
                .Take(40)
                .ToList();

            var changes = new List<CompactRenameCandidate>();
            foreach (var s in symbols)
            {
                changes.Add(new CompactRenameCandidate(s.FilePath, s.LineStart, "definition", s.Name, request.NewName));
                var refs = await cache.QueryReferencesAsync(s.Id, project.Name, ct);
                foreach (var r in refs.Take(300))
                    changes.Add(new CompactRenameCandidate(r.InFilePath, r.Line, "reference", s.Name, request.NewName));
            }

            return Results.Ok(new CompactRenamePlanResponse(project.Name, request.SymbolName, request.NewName, changes.Count, changes));
        });

        g.MapGet("/schema", () => Results.Ok(new CompactSchemaResponse(
            Endpoints:
            [
                "/api/compact/query",
                "/api/compact/resolve",
                "/api/compact/context-pack",
                "/api/compact/graph",
                "/api/compact/references-tree",
                "/api/compact/regex",
                "/api/compact/replace-plan",
                "/api/compact/fs/tree",
                "/api/compact/fs/read-range",
                "/api/compact/fs/write-file",
                "/api/compact/fs/edit",
                "/api/compact/fs/write-patch",
                "/api/compact/fs/diff",
                "/api/compact/git/history",
                "/api/compact/git/commit",
                "/api/compact/git/patch",
                "/api/compact/git/status",
                "/api/compact/deps",
                "/api/compact/diagnostics",
                "/api/compact/test/map",
                "/api/compact/test/run",
                "/api/compact/session/plan",
                "/api/compact/js/check",
                "/api/compact/format",
                "/api/compact/lint",
                "/api/compact/typecheck",
                "/api/compact/reindex",
                "/api/compact/dev/serve",
                "/api/compact/dev/serve/stop",
                "/api/compact/quality/guard",
                "/api/compact/semantic-search",
                "/api/compact/refactor/rename-plan",
                "/api/compact/workflow/run"
            ])));

        g.MapPost("/workflow/run", async (
            CompactWorkflowRunRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            CancellationToken ct) =>
        {
            var (project, error) = ResolveProject(projects, request.Project);
            if (error is not null) return error;
            if (string.IsNullOrWhiteSpace(request.Q))
                return Results.BadRequest("'q' is required.");

            var resolveReq = new CompactResolveRequest { Project = project!.Name, Q = request.Q, Limit = Math.Clamp(request.Limit <= 0 ? 8 : request.Limit, 1, 30) };
            var resolved = await ResolveForWorkflow(cache, resolveReq, ct);

            var previousIds = request.PreviousIds ?? [];
            var packReq = new CompactContextPackRequest
            {
                Project = project.Name,
                Q = request.Q,
                Mode = "impact",
                TokenBudget = Math.Clamp(request.TokenBudget <= 0 ? 600 : request.TokenBudget, 200, 2000),
                MaxItems = Math.Clamp(request.MaxItems <= 0 ? 20 : request.MaxItems, 1, 80),
                PreviousIds = previousIds
            };

            var pack = await BuildWorkflowPack(cache, packReq, ct);
            return Results.Ok(new CompactWorkflowRunResponse(project.Name, request.Q, resolved, pack));
        });
    }

    private static async Task<CompactResolveResponse> ResolveForWorkflow(ICodeMapCache cache, CompactResolveRequest request, CancellationToken ct)
    {
        var q = request.Q.Trim();
        var limit = Math.Clamp(request.Limit <= 0 ? 12 : request.Limit, 1, 40);
        var (stage, items) = await CompactSearchEngine.ResolveBestEffortAsync(cache, request.Project, q, limit, ct);
        return new CompactResolveResponse
        {
            Project = request.Project,
            Q = q,
            Stage = stage,
            Count = items.Count,
            Tokens = CompactSearchEngine.EstimateCompactTokens(items),
            Items = items
        };
    }

    private static async Task<CompactContextPackResponse> BuildWorkflowPack(ICodeMapCache cache, CompactContextPackRequest request, CancellationToken ct)
    {
        var q = request.Q?.Trim() ?? "";
        var tokenBudget = Math.Clamp(request.TokenBudget <= 0 ? 600 : request.TokenBudget, 200, 4000);
        var maxItems = Math.Clamp(request.MaxItems <= 0 ? 30 : request.MaxItems, 5, 80);
        var previousIds = request.PreviousIds?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var mode = (request.Mode ?? "fuzzy").Trim().ToLowerInvariant();
        var items = await CompactSearchEngine.ExpandWithReferencesForImpactAsync(cache, request.Project, q, maxItems, mode, ct);

        var packed = new List<CompactItem>();
        var used = 0;
        foreach (var i in items)
        {
            if (previousIds.Contains(i.Id)) continue;
            var est = CompactSearchEngine.EstimateCompactTokens([i]);
            if (used + est > tokenBudget) break;
            used += est;
            packed.Add(i);
            if (packed.Count >= maxItems) break;
        }

        return new CompactContextPackResponse
        {
            Project = request.Project,
            Mode = request.Mode ?? "fuzzy",
            Q = q,
            TokenBudget = tokenBudget,
            Tokens = used,
            Count = packed.Count,
            Items = packed
        };
    }

    private static (Project? Project, IResult? Error) ResolveProject(ProjectRegistry projects, string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return (null, Results.BadRequest("'project' is required."));
        var project = projects.Resolve(projectName);
        if (project is null)
            return (null, Results.NotFound($"Project '{projectName}' is not registered."));
        return (project, null);
    }

    private static bool PathMatches(string path, string? prefix)
        => string.IsNullOrWhiteSpace(prefix)
           || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
           || path.Contains(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool PathMatchesProject(string projectRoot, string filePath, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return true;
        if (PathMatches(filePath, prefix)) return true;

        var normalizedPrefix = prefix.Trim().Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalizedPrefix)) return true;

        var relativePath = Path.GetRelativePath(projectRoot, filePath).Replace('\\', '/');
        return relativePath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldExclude(RepoConfig config, string path)
        => config.ExcludePaths.Any(x => path.Contains($"{Path.DirectorySeparatorChar}{x}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith($"{Path.DirectorySeparatorChar}{x}", StringComparison.OrdinalIgnoreCase));

    private static async Task<byte[]> BuildRawLosslessPayloadAsync(Project project, IEnumerable<string> requestedPaths, int maxFiles, CancellationToken ct)
    {
        using var ms = new MemoryStream(capacity: 64 * 1024);
        using var writer = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        writer.NewLine = "\n";
        writer.WriteLine("LLENS_RAW_LOSSLESS_V1");
        writer.WriteLine($"PROJECT {project.Name}");
        writer.Flush();

        var root = project.Config.ResolvedPath;
        var count = 0;
        foreach (var path in requestedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Take(maxFiles))
        {
            ct.ThrowIfCancellationRequested();
            var full = ProjectPathHelper.EnsureWithinProject(root, path);
            if (full is null || !File.Exists(full))
                continue;

            var fileBytes = await File.ReadAllBytesAsync(full, ct);
            var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            var relBytes = Encoding.UTF8.GetBytes(rel);
            var sha = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();

            writer.WriteLine($"F path-bytes={relBytes.Length} bytes={fileBytes.Length} sha256={sha}");
            writer.Flush();

            ms.Write(relBytes, 0, relBytes.Length);
            ms.WriteByte((byte)'\n');
            ms.Write(fileBytes, 0, fileBytes.Length);
            ms.WriteByte((byte)'\n');
            count++;
        }

        writer.WriteLine($"END files={count}");
        writer.Flush();
        return ms.ToArray();
    }

    private static async Task<byte[]> BuildRawOrderedPayloadAsync(Project project, IEnumerable<string> requestedPaths, int maxFiles, CancellationToken ct)
    {
        var root = project.Config.ResolvedPath;
        var blobs = new List<byte[]>(capacity: Math.Min(maxFiles, 128));
        foreach (var path in requestedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Take(maxFiles))
        {
            ct.ThrowIfCancellationRequested();
            var full = ProjectPathHelper.EnsureWithinProject(root, path);
            if (full is null || !File.Exists(full))
                continue;
            blobs.Add(await File.ReadAllBytesAsync(full, ct));
        }

        using var ms = new MemoryStream(capacity: 64 * 1024);
        using var writer = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        writer.NewLine = "\n";
        writer.WriteLine("LLENS_RAW_ORDERED_V1");
        writer.WriteLine($"PROJECT {project.Name}");
        writer.WriteLine($"FILES {blobs.Count}");
        writer.Flush();

        foreach (var blob in blobs)
        {
            writer.WriteLine($"B {blob.Length}");
            writer.Flush();
            ms.Write(blob, 0, blob.Length);
            ms.WriteByte((byte)'\n');
        }

        writer.WriteLine("END");
        writer.Flush();
        return ms.ToArray();
    }

    private static async Task<FileNode?> ResolveIndexedFileNodeAsync(ICodeMapCache cache, Project project, string seedPath, CancellationToken ct)
    {
        if (!Path.IsPathRooted(seedPath))
        {
            var preferred = Path.GetFullPath(Path.Combine(project.Config.ResolvedPath, seedPath));
            if (!ShouldExclude(project.Config, preferred))
            {
                var preferredNode = await cache.GetFileNodeAsync(preferred, ct);
                if (preferredNode is not null) return preferredNode;
            }
        }

        var rooted = ProjectPathHelper.EnsureWithinProject(project.Config.ResolvedPath, seedPath);
        if (!string.IsNullOrWhiteSpace(rooted))
        {
            if (ShouldExclude(project.Config, rooted!))
                return null;
            var direct = await cache.GetFileNodeAsync(rooted!, ct);
            if (direct is not null) return direct;
        }

        var all = (await cache.GetAllFilesAsync(project.Name, ct))
            .Where(f => !ShouldExclude(project.Config, f.FilePath))
            .ToList();
        if (all.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(rooted))
        {
            var byRooted = all.FirstOrDefault(f => string.Equals(f.FilePath, rooted, StringComparison.OrdinalIgnoreCase));
            if (byRooted is not null) return byRooted;
        }

        var normalizedSeed = ProjectPathHelper.NormalizePathForMatch(seedPath);
        var normalizedRelative = ProjectPathHelper.NormalizePathForMatch(seedPath.TrimStart('.', '/', '\\'));
        var seedFileName = Path.GetFileName(seedPath);
        var candidates = all
            .Select(f =>
            {
                var score = 0;
                var fp = ProjectPathHelper.NormalizePathForMatch(f.FilePath);
                if (!string.IsNullOrWhiteSpace(rooted))
                {
                    var rootedNorm = ProjectPathHelper.NormalizePathForMatch(rooted!);
                    if (fp == rootedNorm) score += 100;
                }
                if (fp == normalizedSeed || fp.EndsWith("/" + normalizedSeed, StringComparison.OrdinalIgnoreCase)) score += 80;
                if (fp == normalizedRelative || fp.EndsWith("/" + normalizedRelative, StringComparison.OrdinalIgnoreCase)) score += 70;
                if (!string.IsNullOrWhiteSpace(seedFileName) && Path.GetFileName(f.FilePath).Equals(seedFileName, StringComparison.OrdinalIgnoreCase)) score += 30;
                return (Node: f, Score: score, Len: fp.Length);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Len)
            .ToList();

        return candidates.FirstOrDefault().Node;
    }

    private static string Truncate(string s, int n)
        => s.Length <= n ? s : s[..n];

    private static int CountOccurrences(string source, string pattern, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(source)) return 0;
        var count = 0;
        var start = 0;
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        while (true)
        {
            var idx = source.IndexOf(pattern, start, cmp);
            if (idx < 0) break;
            count++;
            start = idx + Math.Max(1, pattern.Length);
        }
        return count;
    }

    private static string ReplaceLiteral(string source, string find, string replace, bool ignoreCase, bool all)
    {
        if (!ignoreCase)
            return all ? source.Replace(find, replace, StringComparison.Ordinal) : ReplaceFirst(source, find, replace, StringComparison.Ordinal);
        return all ? source.Replace(find, replace, StringComparison.OrdinalIgnoreCase) : ReplaceFirst(source, find, replace, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReplaceFirst(string source, string find, string replace, StringComparison cmp)
    {
        var idx = source.IndexOf(find, cmp);
        if (idx < 0) return source;
        return source[..idx] + replace + source[(idx + find.Length)..];
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

    private static (Process Process, string Command) StartPythonServer(string cwd, string host, int port, bool preferPython3)
    {
        var file = preferPython3 ? "python3" : "python";
        var args = $"-m http.server {port} --bind {host}";
        var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            }
        };
        p.Start();
        return (p, $"{file} {args}");
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

    private static string NormalizeGitMode(string? mode)
        => (mode ?? "meta").Trim().ToLowerInvariant() switch
        {
            "compact" => "compact",
            "patch" => "patch",
            _ => "meta"
        };

    private static (string GitRoot, string PathSpec)? ResolveGitScope(Project project, string? requestedPath)
    {
        var root = project.Config.ResolvedPath;
        var gitRoot = FindGitRoot(root);
        if (gitRoot is null) return null;

        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            var relRoot = Path.GetRelativePath(gitRoot, root);
            return (gitRoot, relRoot);
        }

        var full = ProjectPathHelper.EnsureWithinProject(root, requestedPath);
        if (full is null) return null;
        var rel = Path.GetRelativePath(gitRoot, full);
        return (gitRoot, rel);
    }

    private static async Task<CompactGitCommit> BuildCommitSummaryAsync(
        string gitRoot,
        string id,
        string date,
        string author,
        string message,
        string mode,
        string? requestPath,
        CancellationToken ct)
    {
        var files = new List<CompactGitFileChange>();
        var stats = await RunProcessAsync(
            "git",
            string.Join(" ", new[] { "-C", gitRoot, "show", "--name-status", "--numstat", "--format=", id }.Select(QuoteArg)),
            gitRoot, 20000, ct);

        var inserted = 0;
        var deleted = 0;
        foreach (var line in (stats.Stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains('\t'))
            {
                var p = line.Split('\t');
                if (p.Length >= 3 && int.TryParse(p[0], out var a) && int.TryParse(p[1], out var d))
                {
                    inserted += a; deleted += d;
                    continue;
                }

                if (p.Length >= 2 && p[0].Length <= 2)
                {
                    var status = p[0];
                    var path = p[^1];
                    files.Add(new CompactGitFileChange(path, status, null, null, null, []));
                }
            }
        }

        if (mode == "compact")
        {
            var args = new List<string> { "-C", gitRoot, "show", "-U0", "--format=", id };
            if (!string.IsNullOrWhiteSpace(requestPath)) { args.Add("--"); args.Add(requestPath!); }
            var diff = await RunProcessAsync("git", string.Join(" ", args.Select(QuoteArg)), gitRoot, 20000, ct);
            var hunksByFile = ParseCompactHunks(diff.Stdout ?? "");
            for (var i = 0; i < files.Count; i++)
            {
                if (hunksByFile.TryGetValue(files[i].Path, out var hunks))
                    files[i] = files[i] with { Hunks = hunks.Take(20).ToList() };
            }
        }

        return new CompactGitCommit(id, date, author, message, inserted, deleted, files);
    }

    private static Dictionary<string, List<CompactGitHunk>> ParseCompactHunks(string patch)
    {
        var map = new Dictionary<string, List<CompactGitHunk>>(StringComparer.OrdinalIgnoreCase);
        string? currentFile = null;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                currentFile = line["+++ b/".Length..].Trim();
                if (!map.ContainsKey(currentFile)) map[currentFile] = [];
                continue;
            }

            if (currentFile is null) continue;
            if (!line.StartsWith("@@", StringComparison.Ordinal)) continue;

            var h = ParseHunkHeader(line);
            if (h is not null) map[currentFile].Add(h);
        }
        return map;
    }

    private static CompactGitHunk? ParseHunkHeader(string header)
    {
        // @@ -a,b +c,d @@ optional
        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        var oldPart = parts[1].TrimStart('-');
        var newPart = parts[2].TrimStart('+');

        static (int Start, int Count) ParseRange(string s)
        {
            var p = s.Split(',');
            var start = int.TryParse(p[0], out var a) ? a : 0;
            var count = p.Length > 1 && int.TryParse(p[1], out var b) ? b : 1;
            return (start, count);
        }

        var oldR = ParseRange(oldPart);
        var newR = ParseRange(newPart);
        return new CompactGitHunk(oldR.Start, oldR.Count, newR.Start, newR.Count);
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
                    Message: Truncate(mCs.Groups["msg"].Value, 260)));
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
                    Message: Truncate(mTs.Groups["msg"].Value, 260)));
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
                Message: Truncate(pendingMessage, 260)));
            pendingSeverity = null;
            pendingCode = null;
            pendingMessage = null;
            if (outList.Count >= max) break;
        }
        return outList;
    }

    private static async Task<CompactGraphNode?> ResolveSymbolSeedAsync(ICodeMapCache cache, string project, GraphSeed seed, CancellationToken ct)
    {
        var normalizedId = CompactSearchEngine.NormalizeSymbolSeed(seed.Id);
        if (!string.IsNullOrWhiteSpace(normalizedId))
        {
            var all = await cache.QueryByNameAsync("", project, ct);
            var exact = all.FirstOrDefault(s => s.Id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return ToNode(exact);
        }

        var q = seed.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(q)) return null;
        var byName = await cache.QueryByNameAsync(q, project, ct);
        var best = byName
            .OrderByDescending(s => CompactSearchEngine.ScoreSymbol(s, q, CompactSearchEngine.Tokenize(q)))
            .ThenBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.LineStart)
            .FirstOrDefault();
        return best is null ? null : ToNode(best);
    }

    private static async Task<CompactGraphNode?> ResolveEnclosingSymbolAsync(ICodeMapCache cache, string filePath, int line, CancellationToken ct)
    {
        var symbols = (await cache.QueryByFileAsync(filePath, ct))
            .OrderBy(s => s.LineStart)
            .ToList();
        if (symbols.Count == 0) return null;
        var best = symbols
            .Where(s => line >= s.LineStart && (s.LineEnd <= 0 || line <= s.LineEnd))
            .OrderBy(s => s.LineEnd <= 0 ? int.MaxValue : (s.LineEnd - s.LineStart))
            .FirstOrDefault()
            ?? symbols.Where(s => s.LineStart <= line).OrderByDescending(s => s.LineStart).FirstOrDefault();
        return best is null ? null : ToNode(best);
    }

    private static CompactGraphNode ToNode(CodeSymbol s)
        => new($"symbol:{s.Id}", "s", s.Name, s.FilePath, 0, s.Kind.ToString(), s.Id, s.LineStart);

    private static int EstimateTokens<T>(IEnumerable<T> items)
        => Math.Max(1, items.Sum(x => x?.ToString()?.Length ?? 0) / 4);
}

public sealed class CompactSessionStore
{
    private readonly ConcurrentDictionary<string, CompactSessionPlanState> _store = new(StringComparer.OrdinalIgnoreCase);

    public CompactSessionPlanState Get(string sessionId)
        => _store.GetOrAdd(sessionId, id => new CompactSessionPlanState(id, "", "", []));

    public void SetGoal(string sessionId, string goal)
    {
        _store.AddOrUpdate(sessionId,
            _ => new CompactSessionPlanState(sessionId, goal, "", []),
            (_, prev) => prev with { Goal = goal });
    }

    public void AppendStep(string sessionId, string step)
    {
        _store.AddOrUpdate(sessionId,
            _ => new CompactSessionPlanState(sessionId, "", "", [step]),
            (_, prev) =>
            {
                var steps = prev.Steps.ToList();
                steps.Add(step);
                return prev with { Steps = steps };
            });
    }

    public void SetState(string sessionId, string state)
    {
        _store.AddOrUpdate(sessionId,
            _ => new CompactSessionPlanState(sessionId, "", state, []),
            (_, prev) => prev with { State = state });
    }
}

public sealed record CompactDevServerInfo(
    string ServerId,
    string Project,
    string Directory,
    string Host,
    int Port,
    string Command,
    Process Process);

public sealed class CompactDevServerStore
{
    private readonly ConcurrentDictionary<string, CompactDevServerInfo> _servers = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string id, CompactDevServerInfo info) => _servers[id] = info;

    public bool TryGet(string id, out CompactDevServerInfo info) => _servers.TryGetValue(id, out info!);

    public bool Remove(string id) => _servers.TryRemove(id, out _);

    public bool Stop(string id)
    {
        if (!_servers.TryRemove(id, out var info)) return false;
        try
        {
            if (!info.Process.HasExited)
                info.Process.Kill(entireProcessTree: true);
        }
        catch
        {
            return false;
        }
        return true;
    }
}

public record CompactRegexRequest(string Project, string Pattern, string? PathPrefix = null, bool IgnoreCase = true, bool MultiLine = false, int MaxFiles = 200, int MaxMatches = 250);
public record CompactRegexMatch(string P, int L, string M);
public record CompactRegexResponse(string Project, string Pattern, int Count, List<CompactRegexMatch> Matches);
public class CompactRgRequest
{
    public string Project { get; init; } = "";
    public string Pattern { get; init; } = "";
    public string? PathPrefix { get; init; }
    public string Mode { get; init; } = "regex"; // regex | literal
    public bool IgnoreCase { get; init; } = true;
    public bool MultiLine { get; init; }
    public int MaxFiles { get; init; } = 300;
    public int MaxMatches { get; init; } = 300;
    public int MaxLineLength { get; init; } = 240;
    public string? Format { get; init; } // tuple
}
public record CompactRgHit(string P, int L, int C, string T);
public record CompactRgResponse(string Project, string Mode, string Pattern, int Count, List<CompactRgHit> Hits);

public class CompactReplacePlanRequest
{
    public string Project { get; init; } = "";
    public string Find { get; init; } = "";
    public string? Replace { get; init; }
    public string Mode { get; init; } = "literal"; // literal | regex
    public bool IgnoreCase { get; init; }
    public string? PathPrefix { get; init; }
    public int MaxFiles { get; init; } = 120;
    public int MaxMatches { get; init; } = 300;
}
public record CompactReplacePlannedMatch(int Line, int Count, string Preview);
public record CompactReplacePlannedFile(string Path, int Total, List<CompactReplacePlannedMatch> Matches);
public record CompactReplacePlanResponse(string Project, string Mode, string Find, string Replace, int Total, List<CompactReplacePlannedFile> Files);

public record CompactFsTreeRequest(string Project, string? Path = null, int MaxDepth = 3, int MaxEntries = 600);
public record CompactFsEntry(string T, string P, int D);
public record CompactFsTreeResponse(string Project, string Root, int Count, List<CompactFsEntry> Entries);

public record CompactFsReadRangeRequest(string Project, string Path, int From, int To, string? Format = null);
public record CompactLine(int L, string T);
public record CompactFsReadRangeResponse(string Path, int From, int To, List<CompactLine> Lines);
public record CompactFsReadManyRequest(string Project, List<string> Paths, int From = 1, int To = 4000, int MaxFiles = 80, string? Format = null);
public record CompactFsReadManyResponse(string Project, int Count, List<CompactFsReadRangeResponse> Files);
public record CompactFsSpan(string Path, int From, int To);
public record CompactFsReadSpansRequest(string Project, List<CompactFsSpan> Spans, int MaxSpans = 120, int MaxLinesPerSpan = 220, string? Format = null);
public record CompactFsSpanChunk(string Path, int From, int To, string Text);
public record CompactFsReadSpansResponse(string Project, int Count, List<CompactFsSpanChunk> Chunks);

public class CompactFsWriteFileRequest
{
    public string Project { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Content { get; init; }
    public bool Overwrite { get; init; } = true;
    public bool CreateDirs { get; init; } = true;
    public bool EnsureTrailingNewline { get; init; }
}
public record CompactFsWriteFileResponse(string Project, string Path, int Chars, long Bytes, bool Exists);

public class CompactFsEditRequest
{
    public string Project { get; init; } = "";
    public bool DryRun { get; init; }
    public List<CompactFsEditOp>? Operations { get; init; }
}

public class CompactFsEditOp
{
    public string Path { get; init; } = "";
    public string Type { get; init; } = "replace_range"; // replace_range | insert_before | insert_after | delete_range | replace_snippet
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public string? Content { get; init; }
    public string? Find { get; init; }
    public int? Occurrence { get; init; } = 1; // for replace_snippet: 1-based, <=0 means all
}

public record CompactFsEditOpResult(string Path, string Type, bool Ok, string Status, int ChangedLines, int FromLine, int ToLine);
public record CompactFsEditResponse(string Project, bool DryRun, int Applied, int Failed, List<CompactFsEditOpResult> Results);

public class CompactFsWritePatchRequest
{
    public string Project { get; init; } = "";
    public bool DryRun { get; init; } = true;
    public List<CompactPatchOp>? Operations { get; init; }
}
public class CompactPatchOp
{
    public string Path { get; init; } = "";
    public string Find { get; init; } = "";
    public string? Replace { get; init; }
    public string Mode { get; init; } = "literal";
    public bool ReplaceAll { get; init; } = true;
    public bool IgnoreCase { get; init; }
}
public record CompactPatchOpResult(string Path, bool Ok, string Status, int Changes);
public record CompactFsWritePatchResponse(string Project, bool DryRun, int TotalChanges, List<CompactPatchOpResult> Results);

public class CompactFsDiffRequest
{
    public string Project { get; init; } = "";
    public string? Path { get; init; }
    public bool Staged { get; init; }
    public int MaxChars { get; init; } = 24000;
    public int TimeoutMs { get; init; } = 15000;
}
public record CompactFsDiffResponse(string Project, bool IsGitRepo, string? Path, bool Staged, int Lines, string Diff);

public record CompactGitHistoryRequest(string Project, string? Path = null, string? Since = null, string? Until = null, int MaxCommits = 20, string Mode = "meta");
public record CompactGitCommitRequest(string Project, string CommitId, string? Path = null, string Mode = "compact");
public record CompactGitPatchRequest(string Project, string CommitId, string? Path = null, int MaxChars = 30000);
public class CompactGitStatusRequest
{
    public string Project { get; init; } = "";
    public string? Path { get; init; }
    public string Mode { get; init; } = "compact"; // compact | full
    public string? Format { get; init; } // tuple | (default object)
    public bool IncludeUntracked { get; init; } = true;
    public int MaxEntries { get; init; } = 500;
    public int TimeoutMs { get; init; } = 15000;
}

public record CompactGitHistoryResponse(string Project, bool IsGitRepo, string Mode, List<CompactGitCommit> Commits);
public record CompactGitCommitResponse(string Project, bool IsGitRepo, CompactGitCommit? Commit);
public record CompactGitPatchResponse(string Project, bool IsGitRepo, string CommitId, string? Path, string Patch);
public record CompactGitStatusResponse(string Project, bool IsGitRepo, string? Path, string Mode, int Count, List<CompactGitStatusEntry> Entries, List<string> Compact);
public record CompactGitStatusEntry(string Path, string Xy, bool Staged, bool Unstaged, bool Untracked, string? RenamedFrom);

public record CompactGitCommit(
    string Id,
    string Date,
    string Author,
    string Message,
    int Inserted,
    int Deleted,
    List<CompactGitFileChange> Files);

public record CompactGitFileChange(
    string Path,
    string Status,
    int? Inserted,
    int? Deleted,
    string? PrevPath,
    List<CompactGitHunk> Hunks);

public record CompactGitHunk(int OldStart, int OldCount, int NewStart, int NewCount);

public class CompactDepsRequest
{
    public string Project { get; init; } = "";
    public GraphSeed? Seed { get; init; }
}
public record CompactDep(string Type, string From, string To);
public record CompactDepsResponse(string Project, string Seed, int Count, List<CompactDep> Edges);

public record CompactDiagnosticsRequest(string Project, string? Target = null, int TimeoutMs = 60000);
public record CompactDiagnostic(string Path, int Line, int Col, string Severity, string Code, string Message);
public record CompactDiagnosticsResponse(string Project, string Target, int ExitCode, int Count, List<CompactDiagnostic> Diagnostics);

public class CompactTestMapRequest
{
    public string Project { get; init; } = "";
    public string? Q { get; init; }
    public GraphSeed? Seed { get; init; }
    public int Limit { get; init; } = 20;
}
public record CompactTestCandidate(string Path, int Score);
public record CompactTestMapResponse(string Project, int Count, List<CompactTestCandidate> Candidates);

public class CompactTestRunRequest
{
    public string Project { get; init; } = "";
    public string? Target { get; init; }
    public string? Filter { get; init; }
    public int TimeoutMs { get; init; } = 120000;
    public int MaxChars { get; init; } = 20000;
}
public record CompactTestRunResponse(string Project, string Target, int ExitCode, int Count, List<CompactDiagnostic> Diagnostics, string Output);

public class CompactSessionPlanRequest
{
    public string SessionId { get; init; } = "";
    public string? SetGoal { get; init; }
    public string? AppendStep { get; init; }
    public string? SetState { get; init; }
}
public record CompactSessionPlanState(string SessionId, string Goal, string State, IReadOnlyList<string> Steps);

public class CompactQualityGuardRequest
{
    public string Project { get; init; } = "";
    public int TokenBudget { get; init; } = 1200;
    public string[]? ChangedFiles { get; init; }
}
public record CompactQualityGuardResponse(string Project, List<string> Warnings, List<string> Infos);

public class CompactJsCheckRequest
{
    public string Project { get; init; } = "";
    public string? Path { get; init; }
    public int MaxFiles { get; init; } = 120;
    public int TimeoutMs { get; init; } = 12000;
}
public record CompactJsCheckIssue(string Path, int ExitCode, string Message);
public record CompactJsCheckResponse(string Project, int CheckedFiles, bool Ok, List<CompactJsCheckIssue> Issues);

public class CompactFormatRequest
{
    public string Project { get; init; } = "";
    public string? Target { get; init; }
    public string? Path { get; init; }
    public bool CheckOnly { get; init; } = true;
    public int TimeoutMs { get; init; } = 120000;
    public int MaxChars { get; init; } = 12000;
}
public record CompactFormatResponse(string Project, string Target, bool CheckOnly, int ExitCode, string Output);

public class CompactLintRequest
{
    public string Project { get; init; } = "";
    public string? Target { get; init; }
    public string? Path { get; init; }
    public int TimeoutMs { get; init; } = 120000;
    public int MaxChars { get; init; } = 16000;
}
public record CompactLintResponse(string Project, string Target, int ExitCode, int Count, List<CompactDiagnostic> Diagnostics, string Output);

public class CompactTypecheckRequest
{
    public string Project { get; init; } = "";
    public string? Target { get; init; }
    public int TimeoutMs { get; init; } = 120000;
    public int MaxChars { get; init; } = 16000;
}
public record CompactTypecheckResponse(string Project, string Target, int ExitCode, int Count, List<CompactDiagnostic> Diagnostics, string Output);

public class CompactReindexRequest
{
    public string Project { get; init; } = "";
    public string? Path { get; init; }
    public bool PruneStale { get; init; } = true;
    public int MaxFiles { get; init; } = 5000;
}
public record CompactReindexResponse(string Project, string Mode, string? Path, int Indexed, int Removed, int Skipped, int Failed, bool Pruned, long DurationMs);

public class CompactDevServeRequest
{
    public string Project { get; init; } = "";
    public string? Path { get; init; }
    public string? Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5173;
    public string? ServerId { get; init; }
    public bool ReuseIfRunning { get; init; } = true;
}
public record CompactDevServeResponse(string Project, string ServerId, bool Running, int Pid, string Command, string Url, bool StartedNow);
public record CompactDevServeStopRequest(string ServerId);
public record CompactDevServeStopResponse(string ServerId, bool Stopped);

public record CompactSemanticSearchRequest(string Project, string Q, int Limit = 20);

public class CompactRenamePlanRequest
{
    public string Project { get; init; } = "";
    public string SymbolName { get; init; } = "";
    public string NewName { get; init; } = "";
}
public record CompactRenameCandidate(string Path, int Line, string Kind, string OldName, string NewName);
public record CompactRenamePlanResponse(string Project, string SymbolName, string NewName, int Count, List<CompactRenameCandidate> Candidates);

public record CompactSchemaResponse(List<string> Endpoints);

public class CompactWorkflowRunRequest
{
    public string Project { get; init; } = "";
    public string Q { get; init; } = "";
    public int Limit { get; init; } = 8;
    public int TokenBudget { get; init; } = 600;
    public int MaxItems { get; init; } = 20;
    public string[]? PreviousIds { get; init; }
}
public record CompactWorkflowRunResponse(string Project, string Q, CompactResolveResponse Resolve, CompactContextPackResponse ContextPack);
