namespace Llens.Languages.Rust;

public sealed class RustLanguage : ILanguage<Rust>
{
    public LanguageId Id => LanguageId.Rust;
    public string Name => "Rust";
    public IReadOnlyList<string> Extensions => [".rs"];

    public ISymbolExtractor<Rust> SymbolExtractor { get; } = new SynShimExtractor();
    public IImportResolver<Rust>? ImportResolver { get; } = new CargoImportResolver();
    public IUsageExtractor<Rust>? UsageExtractor { get; } = new RustUsageExtractor();
    public IReferenceResolver<Rust>? ReferenceResolver { get; } = new RustReferenceResolver();
}
