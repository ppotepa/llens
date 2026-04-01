using Llens.Tools;

namespace Llens.Languages.Rust;

/// <summary>
/// Invokes the syn-shim Rust binary to extract symbols and imports from .rs files.
/// Covers both SymbolExtraction and ImportExtraction via a single subprocess call.
/// </summary>
public class SynShimTool : ITool<Rust>
{
    public IReadOnlySet<ToolCapability> Capabilities { get; } =
        new HashSet<ToolCapability> { ToolCapability.SymbolExtraction, ToolCapability.ImportExtraction };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken ct = default)
    {
        // TODO: spawn the syn-shim binary, pass context.FilePath, parse JSON output
        await Task.CompletedTask;
        return ToolResult.Fail("syn-shim not yet implemented");
    }
}
