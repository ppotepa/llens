namespace Llens.Languages.CSharp;

public sealed class CSharpLanguage : ILanguage<CSharp>
{
    public LanguageId Id => LanguageId.CSharp;
    public string Name => "CSharp";
    public IReadOnlyList<string> Extensions => [".cs"];

    public ISymbolExtractor<CSharp> SymbolExtractor { get; } = new RoslynExtractor();
    public IImportResolver<CSharp>? ImportResolver { get; } = new CSharpImportResolver();
    public IUsageExtractor<CSharp>? UsageExtractor { get; } = new RoslynUsageExtractor();
    public IReferenceResolver<CSharp>? ReferenceResolver { get; } = new RoslynReferenceResolver();
}
