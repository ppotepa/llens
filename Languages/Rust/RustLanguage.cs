using Llens.Tools;

namespace Llens.Languages.Rust;

public class RustLanguage : ILanguage<Rust>
{
    public LanguageId Id => LanguageId.Rust;
    public string Name => "Rust";
    public IReadOnlyList<string> Extensions => [".rs"];

    public IReadOnlyList<ITool<Rust>> Tools =>
    [
        new SynShimTool()
    ];
}
