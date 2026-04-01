using Llens.Models;

namespace Llens.Tools;

public record ToolResult(
    bool Success,
    IReadOnlyList<CodeSymbol> Symbols,
    string? Error = null
)
{
    public static ToolResult Ok(IReadOnlyList<CodeSymbol> symbols) => new(true, symbols);
    public static ToolResult Fail(string error) => new(false, [], error);
}
