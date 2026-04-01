using Llens.Tools;

namespace Llens.Languages.Rust;

public class SynShimTool : ITool<Rust>
{
    public ToolKind Kind => ToolKind.SynShim;
    public ToolPurpose Purpose => ToolPurpose.Indexing;

    public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken ct = default)
    {
        // TODO: spawn the syn-shim binary, pass context.FilePath, parse JSON output
        await Task.CompletedTask;
        return ToolResult.Fail("syn-shim not yet implemented");
    }
}
