using Llens.Tools;

namespace Llens.Languages.Rust;

/// <summary>
/// Invokes a Rust binary (syn-shim) that parses .rs files and returns symbols as JSON.
/// </summary>
public class SynShimTool : ITool<Rust>
{
    public string Name => "syn-shim";
    public ToolPurpose Purpose => ToolPurpose.Indexing;

    public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken ct = default)
    {
        // TODO: spawn the syn-shim binary, pass context.FilePath, parse JSON output
        await Task.CompletedTask;
        return ToolResult.Fail("syn-shim not yet implemented");
    }
}
