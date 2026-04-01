using Llens.Tools;

namespace Llens.Languages.CSharp;

public class CSharpLanguage : ILanguage<CSharp>
{
    public string Name => "CSharp";
    public IReadOnlyList<string> Extensions => [".cs"];

    public IReadOnlyList<ITool<CSharp>> Tools =>
    [
        new RoslynTool()
    ];
}
