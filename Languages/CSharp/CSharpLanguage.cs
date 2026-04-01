using Llens.Tools;

namespace Llens.Languages.CSharp;

public class CSharpLanguage : ILanguage<CSharp>
{
    public string Name => "C#";
    public IReadOnlyList<string> Extensions => [".cs"];

    public IReadOnlyList<ITool<CSharp>> Tools =>
    [
        new RoslynTool()
    ];
}
