using Llens.Api;
using Llens.Application.Fs;
using Llens.Application.JsCheck;
using Llens.Application.Reindex;
using Llens.Models;
using System.Text.Json;

namespace Llens.Cli;

public sealed class CompactCliCommands
{
    private readonly ProjectRegistry _projects;
    private readonly IJsCheckService _jsCheckService;
    private readonly ICompactFsService _fsService;
    private readonly ICompactReindexService _reindexService;

    public CompactCliCommands(
        ProjectRegistry projects,
        IJsCheckService jsCheckService,
        ICompactFsService fsService,
        ICompactReindexService reindexService)
    {
        _projects = projects;
        _jsCheckService = jsCheckService;
        _fsService = fsService;
        _reindexService = reindexService;
    }

    [ToolCommand("js.check", Description = "Syntax-check JavaScript files")]
    public async Task<CompactJsCheckResponse> JsCheck(JsCheckCliRequest request, CancellationToken ct)
    {
        var project = _projects.Resolve(request.Project);
        if (project is null)
            throw new CliBindingException($"Project '{request.Project}' is not registered.");

        var apiRequest = new CompactJsCheckRequest
        {
            Project = request.Project,
            Path = request.Path,
            MaxFiles = request.MaxFiles ?? 0,
            TimeoutMs = request.TimeoutMs ?? 0
        };

        var outcome = await _jsCheckService.RunAsync(project, apiRequest, ct);
        if (!outcome.Ok || outcome.Response is null)
            throw new CliBindingException(outcome.ErrorMessage ?? "js.check failed.");

        return outcome.Response;
    }

    [ToolCommand("fs.tree", Description = "List project tree snapshot")]
    public CompactFsTreeResponse FsTree(FsTreeCliRequest request)
    {
        var project = ResolveProject(request.Project);
        var apiRequest = new CompactFsTreeRequest(request.Project, request.Path, request.MaxDepth ?? 3, request.MaxEntries ?? 600);
        var outcome = _fsService.Tree(project, apiRequest);
        if (!outcome.Ok || outcome.Response is null)
            throw new CliBindingException(outcome.ErrorMessage ?? "fs.tree failed.");
        return outcome.Response;
    }

    [ToolCommand("fs.read-range", Description = "Read a file line range")]
    public async Task<CompactFsReadRangeResponse> FsReadRange(FsReadRangeCliRequest request, CancellationToken ct)
    {
        var project = ResolveProject(request.Project);
        var apiRequest = new CompactFsReadRangeRequest(request.Project, request.Path, request.From, request.To);
        var outcome = await _fsService.ReadRangeAsync(project, apiRequest, ct);
        if (!outcome.Ok || outcome.Response is null)
            throw new CliBindingException(outcome.ErrorMessage ?? "fs.read-range failed.");
        return outcome.Response;
    }

    [ToolCommand("fs.write-file", Description = "Write or overwrite a file")]
    public CompactFsWriteFileResponse FsWriteFile(FsWriteFileCliRequest request)
    {
        var project = ResolveProject(request.Project);
        var content = request.Content;
        if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(request.ContentFile))
        {
            if (!File.Exists(request.ContentFile))
                throw new CliBindingException($"Content file not found: {request.ContentFile}");
            content = File.ReadAllText(request.ContentFile);
        }

        var apiRequest = new CompactFsWriteFileRequest
        {
            Project = request.Project,
            Path = request.Path,
            Content = content ?? "",
            Overwrite = request.Overwrite ?? true,
            CreateDirs = request.CreateDirs ?? true,
            EnsureTrailingNewline = request.EnsureTrailingNewline ?? false
        };
        var outcome = _fsService.WriteFile(project, apiRequest);
        if (!outcome.Ok || outcome.Response is null)
            throw new CliBindingException(outcome.ErrorMessage ?? "fs.write-file failed.");
        return outcome.Response;
    }

    [ToolCommand("fs.edit", Description = "Apply targeted partial edits from an operations JSON file")]
    public async Task<CompactFsEditResponse> FsEdit(FsEditCliRequest request, CancellationToken ct)
    {
        var project = ResolveProject(request.Project);
        if (string.IsNullOrWhiteSpace(request.OpsFile))
            throw new CliBindingException("'--ops-file' is required.");
        if (!File.Exists(request.OpsFile))
            throw new CliBindingException($"Ops file not found: {request.OpsFile}");

        var json = await File.ReadAllTextAsync(request.OpsFile, ct);
        var ops = JsonSerializer.Deserialize<List<CompactFsEditOp>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (ops is null || ops.Count == 0)
            throw new CliBindingException("Ops file did not contain any operations.");

        var apiRequest = new CompactFsEditRequest
        {
            Project = request.Project,
            DryRun = request.DryRun ?? false,
            Operations = ops
        };
        var outcome = await _fsService.EditAsync(project, apiRequest, ct);
        if (!outcome.Ok || outcome.Response is null)
            throw new CliBindingException(outcome.ErrorMessage ?? "fs.edit failed.");
        return outcome.Response;
    }

    [ToolCommand("fs.diff", Description = "Get git diff for project/path")]
    public async Task<CompactFsDiffResponse> FsDiff(FsDiffCliRequest request, CancellationToken ct)
    {
        var project = ResolveProject(request.Project);
        var apiRequest = new CompactFsDiffRequest
        {
            Project = request.Project,
            Path = request.Path,
            Staged = request.Staged ?? false,
            MaxChars = request.MaxChars ?? 24000,
            TimeoutMs = request.TimeoutMs ?? 15000
        };
        var outcome = await _fsService.DiffAsync(project, apiRequest, ct);
        if (!outcome.Ok || outcome.Response is null)
            throw new CliBindingException(outcome.ErrorMessage ?? "fs.diff failed.");
        return outcome.Response;
    }

    [ToolCommand("git.status", Description = "Get git working tree status (compact/full)")]
    public async Task<object> GitStatus(GitStatusCliRequest request, CancellationToken ct)
    {
        var project = ResolveProject(request.Project);
        var apiRequest = new CompactGitStatusRequest
        {
            Project = request.Project,
            Path = request.Path,
            Mode = request.Mode ?? "compact",
            Format = request.Format,
            IncludeUntracked = request.IncludeUntracked ?? true,
            MaxEntries = request.MaxEntries ?? 500,
            TimeoutMs = request.TimeoutMs ?? 15000
        };
        var outcome = await _fsService.GitStatusAsync(project, apiRequest, ct);
        if (!outcome.Ok || outcome.Response is null)
            throw new CliBindingException(outcome.ErrorMessage ?? "git.status failed.");
        if (string.Equals(request.Format?.Trim(), "tuple", StringComparison.OrdinalIgnoreCase))
            return CompactTupleCodec.FromGitStatus(outcome.Response);
        return outcome.Response;
    }

    [ToolCommand("test.run", Description = "Run project tests with auto-detected backend")]
    public async Task<CompactTestRunResponse> TestRun(TestRunCliRequest request, CancellationToken ct)
    {
        var project = ResolveProject(request.Project);
        var apiRequest = new CompactTestRunRequest
        {
            Project = request.Project,
            Target = request.Target,
            Filter = request.Filter,
            TimeoutMs = request.TimeoutMs ?? 120000,
            MaxChars = request.MaxChars ?? 20000
        };
        return await CompactOpsCommandBridge.RunTestAsync(project, apiRequest, ct);
    }

    [ToolCommand("format", Description = "Run formatter check or write with auto-detected backend")]
    public async Task<CompactFormatResponse> Format(FormatCliRequest request, CancellationToken ct)
    {
        var project = ResolveProject(request.Project);
        var apiRequest = new CompactFormatRequest
        {
            Project = request.Project,
            Target = request.Target,
            Path = request.Path,
            CheckOnly = request.CheckOnly ?? true,
            TimeoutMs = request.TimeoutMs ?? 120000,
            MaxChars = request.MaxChars ?? 12000
        };
        return await CompactOpsCommandBridge.RunFormatAsync(project, apiRequest, ct);
    }

    [ToolCommand("lint", Description = "Run lint/analyzer checks with auto-detected backend")]
    public async Task<CompactLintResponse> Lint(LintCliRequest request, CancellationToken ct)
    {
        var project = ResolveProject(request.Project);
        var apiRequest = new CompactLintRequest
        {
            Project = request.Project,
            Target = request.Target,
            Path = request.Path,
            TimeoutMs = request.TimeoutMs ?? 120000,
            MaxChars = request.MaxChars ?? 16000
        };
        return await CompactOpsCommandBridge.RunLintAsync(project, apiRequest, ct);
    }

    [ToolCommand("typecheck", Description = "Run typecheck/build-check with auto-detected backend")]
    public async Task<CompactTypecheckResponse> Typecheck(TypecheckCliRequest request, CancellationToken ct)
    {
        var project = ResolveProject(request.Project);
        var apiRequest = new CompactTypecheckRequest
        {
            Project = request.Project,
            Target = request.Target,
            TimeoutMs = request.TimeoutMs ?? 120000,
            MaxChars = request.MaxChars ?? 16000
        };
        return await CompactOpsCommandBridge.RunTypecheckAsync(project, apiRequest, ct);
    }

    [ToolCommand("reindex", Description = "Refresh project, file, or directory index state")]
    public async Task<CompactReindexResponse> Reindex(ReindexCliRequest request, CancellationToken ct)
    {
        var project = ResolveProject(request.Project);
        var apiRequest = new CompactReindexRequest
        {
            Project = request.Project,
            Path = request.Path,
            PruneStale = request.PruneStale ?? true,
            MaxFiles = request.MaxFiles ?? 5000
        };
        try
        {
            return await _reindexService.RunAsync(project, apiRequest, ct);
        }
        catch (FileNotFoundException)
        {
            throw new CliBindingException("Path not found.");
        }
        catch (InvalidOperationException ex)
        {
            throw new CliBindingException(ex.Message);
        }
    }

    private Project ResolveProject(string projectName)
    {
        var project = _projects.Resolve(projectName);
        if (project is null)
            throw new CliBindingException($"Project '{projectName}' is not registered.");
        return project;
    }

}

public sealed class JsCheckCliRequest
{
    [ToolArg("project", Description = "Registered project name")]
    public string Project { get; set; } = "";

    [ToolArg("path", Description = "Optional file or directory path")]
    public string? Path { get; set; }

    [ToolArg("max-files", Description = "Optional scan cap")]
    public int? MaxFiles { get; set; }

    [ToolArg("timeout-ms", Description = "Optional per-file timeout")]
    public int? TimeoutMs { get; set; }
}

public sealed class FsTreeCliRequest
{
    [ToolArg("project", Description = "Registered project name")]
    public string Project { get; set; } = "";

    [ToolArg("path", Description = "Optional directory path within project")]
    public string? Path { get; set; }

    [ToolArg("max-depth", Description = "Optional max traversal depth")]
    public int? MaxDepth { get; set; }

    [ToolArg("max-entries", Description = "Optional max returned entries")]
    public int? MaxEntries { get; set; }
}

public sealed class FsReadRangeCliRequest
{
    [ToolArg("project", Description = "Registered project name")]
    public string Project { get; set; } = "";

    [ToolArg("path", Description = "Path in project")]
    public string Path { get; set; } = "";

    [ToolArg("from", Description = "Start line (1-based)")]
    public int From { get; set; }

    [ToolArg("to", Description = "End line (1-based)")]
    public int To { get; set; }
}

public sealed class FsWriteFileCliRequest
{
    [ToolArg("project", Description = "Registered project name")]
    public string Project { get; set; } = "";

    [ToolArg("path", Description = "Path in project")]
    public string Path { get; set; } = "";

    [ToolArg("content", Description = "Inline content")]
    public string? Content { get; set; }

    [ToolArg("content-file", Description = "Read content from local file path")]
    public string? ContentFile { get; set; }

    [ToolArg("overwrite", Description = "Overwrite existing file")]
    public bool? Overwrite { get; set; }

    [ToolArg("create-dirs", Description = "Create missing directories")]
    public bool? CreateDirs { get; set; }

    [ToolArg("ensure-trailing-newline", Description = "Append newline if missing")]
    public bool? EnsureTrailingNewline { get; set; }
}

public sealed class FsDiffCliRequest
{
    [ToolArg("project", Description = "Registered project name")]
    public string Project { get; set; } = "";

    [ToolArg("path", Description = "Optional path in project")]
    public string? Path { get; set; }

    [ToolArg("staged", Description = "Use staged diff")]
    public bool? Staged { get; set; }

    [ToolArg("max-chars", Description = "Max returned diff chars")]
    public int? MaxChars { get; set; }

    [ToolArg("timeout-ms", Description = "Git command timeout")]
    public int? TimeoutMs { get; set; }
}

public sealed class FsEditCliRequest
{
    [ToolArg("project", Description = "Registered project name")]
    public string Project { get; set; } = "";

    [ToolArg("ops-file", Description = "JSON array of edit operations")]
    public string OpsFile { get; set; } = "";

    [ToolArg("dry-run", Description = "Plan only, do not write files")]
    public bool? DryRun { get; set; }
}

public sealed class GitStatusCliRequest
{
    [ToolArg("project", Description = "Registered project name")]
    public string Project { get; set; } = "";

    [ToolArg("path", Description = "Optional path in project")]
    public string? Path { get; set; }

    [ToolArg("mode", Description = "compact or full")]
    public string? Mode { get; set; }

    [ToolArg("format", Description = "tuple or object")]
    public string? Format { get; set; }

    [ToolArg("include-untracked", Description = "Include untracked files")]
    public bool? IncludeUntracked { get; set; }

    [ToolArg("max-entries", Description = "Max status entries")]
    public int? MaxEntries { get; set; }

    [ToolArg("timeout-ms", Description = "Git command timeout")]
    public int? TimeoutMs { get; set; }
}

public sealed class ReindexCliRequest
{
    [ToolArg("project", Description = "Registered project name")]
    public string Project { get; set; } = "";

    [ToolArg("path", Description = "Optional file or directory path in project")]
    public string? Path { get; set; }

    [ToolArg("prune-stale", Description = "Remove cached entries for missing files")]
    public bool? PruneStale { get; set; }

    [ToolArg("max-files", Description = "Optional directory reindex cap")]
    public int? MaxFiles { get; set; }
}

public sealed class TestRunCliRequest
{
    [ToolArg("project", Description = "Registered project name")]
    public string Project { get; set; } = "";

    [ToolArg("target", Description = "dotnet, cargo, node, or auto")]
    public string? Target { get; set; }

    [ToolArg("filter", Description = "Optional test filter or pattern")]
    public string? Filter { get; set; }

    [ToolArg("timeout-ms", Description = "Execution timeout")]
    public int? TimeoutMs { get; set; }

    [ToolArg("max-chars", Description = "Max returned output chars")]
    public int? MaxChars { get; set; }
}

public sealed class FormatCliRequest
{
    [ToolArg("project", Description = "Registered project name")]
    public string Project { get; set; } = "";

    [ToolArg("target", Description = "dotnet, cargo, node, or auto")]
    public string? Target { get; set; }

    [ToolArg("path", Description = "Optional file or directory path")]
    public string? Path { get; set; }

    [ToolArg("check-only", Description = "Verify formatting without writing")]
    public bool? CheckOnly { get; set; }

    [ToolArg("timeout-ms", Description = "Execution timeout")]
    public int? TimeoutMs { get; set; }

    [ToolArg("max-chars", Description = "Max returned output chars")]
    public int? MaxChars { get; set; }
}

public sealed class LintCliRequest
{
    [ToolArg("project", Description = "Registered project name")]
    public string Project { get; set; } = "";

    [ToolArg("target", Description = "dotnet, cargo, node, or auto")]
    public string? Target { get; set; }

    [ToolArg("path", Description = "Optional file or directory path")]
    public string? Path { get; set; }

    [ToolArg("timeout-ms", Description = "Execution timeout")]
    public int? TimeoutMs { get; set; }

    [ToolArg("max-chars", Description = "Max returned output chars")]
    public int? MaxChars { get; set; }
}

public sealed class TypecheckCliRequest
{
    [ToolArg("project", Description = "Registered project name")]
    public string Project { get; set; } = "";

    [ToolArg("target", Description = "dotnet, cargo, node, or auto")]
    public string? Target { get; set; }

    [ToolArg("timeout-ms", Description = "Execution timeout")]
    public int? TimeoutMs { get; set; }

    [ToolArg("max-chars", Description = "Max returned output chars")]
    public int? MaxChars { get; set; }
}
