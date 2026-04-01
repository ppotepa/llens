using Llens.Tools;

namespace Llens.Languages.Rust;

public class RustLanguage : ILanguage<Rust>
{
    public string Name => "Rust";
    public IReadOnlyList<string> Extensions => [".rs"];

    public IReadOnlyList<ITool<Rust>> Tools =>
    [
        new SynShimTool(),
        // new RustAnalyzerTool(),  // future: LSP-based semantic tool
        // new TreeSitterRustTool() // future: fallback structural tool
    ];
}
