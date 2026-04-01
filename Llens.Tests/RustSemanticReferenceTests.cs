using Llens.Indexing;
using Llens.Languages.Rust;
using Llens.Models;
using Xunit;

namespace Llens.Tests;

public sealed class RustSemanticReferenceTests
{
    [Fact]
    public async Task BuildSemanticReferences_PrefersImportedFunction_ForCallUsage()
    {
        var root = CreateTempDir();
        try
        {
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(Path.Combine(src, "services"));
            var filePath = Path.Combine(src, "main.rs");
            var svcPath = Path.Combine(src, "services", "order_service.rs");
            File.WriteAllText(filePath, "use crate::services::order_service::create_order;\nfn run() { create_order(); }\n");
            File.WriteAllText(svcPath, "pub fn create_order() {}\n");

            var functionId = $"fixture:{svcPath}:create_order:10";
            var noiseId = $"fixture:{filePath}:create_order:1";
            var symbols = new[]
            {
                new CodeSymbol { Id = functionId, RepoName = "fixture", FilePath = svcPath, Name = "create_order", Kind = SymbolKind.Function, LineStart = 1, LineEnd = 1 },
                new CodeSymbol { Id = noiseId, RepoName = "fixture", FilePath = filePath, Name = "create_order", Kind = SymbolKind.Unknown, LineStart = 1, LineEnd = 1 }
            };

            var lang = new RustLanguage();
            var refs = await lang.BuildSemanticReferencesAsync(
                new LanguageReferenceContext(
                    "fixture",
                    filePath,
                    await File.ReadAllLinesAsync(filePath),
                    (name, _, _) => Task.FromResult(symbols.Where(s => s.Name.Equals(name, StringComparison.Ordinal)) as IEnumerable<CodeSymbol>),
                    (_, _) => Task.FromResult(new[]
                    {
                        new FileNode { FilePath = filePath, RepoName = "fixture", Language = "Rust", Imports = [], SymbolCount = 0, LastIndexedAt = 0 },
                        new FileNode { FilePath = svcPath, RepoName = "fixture", Language = "Rust", Imports = [], SymbolCount = 0, LastIndexedAt = 0 }
                    } as IEnumerable<FileNode>)),
                CancellationToken.None);

            var callRef = refs.FirstOrDefault(r => r.Line == 2);
            Assert.NotNull(callRef);
            Assert.Equal(functionId, callRef!.SymbolId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task BuildSemanticReferences_PrefersStruct_ForTypeUsage()
    {
        var root = CreateTempDir();
        try
        {
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(Path.Combine(src, "models"));
            var filePath = Path.Combine(src, "main.rs");
            var modelPath = Path.Combine(src, "models", "order.rs");
            File.WriteAllText(filePath, "use crate::models::order::Order;\nfn run(o: Order) -> Order { Order { id: 1 } }\n");
            File.WriteAllText(modelPath, "pub struct Order { pub id: i32 }\n");

            var structId = $"fixture:{modelPath}:Order:1";
            var fnId = $"fixture:{modelPath}:Order:2";
            var symbols = new[]
            {
                new CodeSymbol { Id = structId, RepoName = "fixture", FilePath = modelPath, Name = "Order", Kind = SymbolKind.Struct, LineStart = 1, LineEnd = 1 },
                new CodeSymbol { Id = fnId, RepoName = "fixture", FilePath = modelPath, Name = "Order", Kind = SymbolKind.Function, LineStart = 2, LineEnd = 2 }
            };

            var lang = new RustLanguage();
            var refs = await lang.BuildSemanticReferencesAsync(
                new LanguageReferenceContext(
                    "fixture",
                    filePath,
                    await File.ReadAllLinesAsync(filePath),
                    (name, _, _) => Task.FromResult(symbols.Where(s => s.Name.Equals(name, StringComparison.Ordinal)) as IEnumerable<CodeSymbol>),
                    (_, _) => Task.FromResult(new[]
                    {
                        new FileNode { FilePath = filePath, RepoName = "fixture", Language = "Rust", Imports = [], SymbolCount = 0, LastIndexedAt = 0 },
                        new FileNode { FilePath = modelPath, RepoName = "fixture", Language = "Rust", Imports = [], SymbolCount = 0, LastIndexedAt = 0 }
                    } as IEnumerable<FileNode>)),
                CancellationToken.None);

            var typeRefs = refs.Where(r => r.Context.Contains("Order", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(typeRefs);
            Assert.All(typeRefs, r => Assert.Equal(structId, r.SymbolId));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"llens-rust-semantic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Cargo.toml"), "[package]\nname = \"fixture\"\nversion = \"0.1.0\"\n");
        return root;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
