using Llens.Models;

namespace Llens.Tools;

public record ToolResult(
    bool Success,
    IReadOnlyList<CodeSymbol> Symbols,
    IReadOnlyList<string> Imports,
    string? Error = null
)
{
    public static ToolResult Ok(IReadOnlyList<CodeSymbol> symbols, IReadOnlyList<string>? imports = null)
        => new(true, symbols, imports ?? []);

    public static ToolResult Fail(string error)
        => new(false, [], [], error);
}
