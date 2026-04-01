using System.Net.Http.Json;
using System.Text.Json;
using Llens.Api;
using Llens.Caching;
using Llens.Languages;
using Llens.Models;
using Llens.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Llens.Tests;

public sealed class GraphCallerEdgesDeterministicTests
{
    [Fact]
    public async Task GraphQuery_ReturnsExpectedCallerEdges_FromFixtureReferences()
    {
        await using var fixture = FixtureData.Create();
        await using var host = await GraphTestHost.StartAsync(fixture);

        var request = new GraphQueryRequest
        {
            Project = fixture.ProjectName,
            Seed = new GraphSeed { Type = "symbol", Id = $"symbol:{fixture.TargetSymbolId}" },
            Expand = new GraphExpand
            {
                EdgeTypes = ["callers", "references", "contains"],
                Direction = "both",
                Depth = 2,
                MaxNodes = 80
            },
            Include = new GraphInclude { Snippets = false }
        };

        using var response = await host.Client.PostAsJsonAsync("/api/graph/query", request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var edges = root.GetProperty("edges").EnumerateArray().ToList();
        var callers = edges.Where(e => e.GetProperty("type").GetString() == "callers").ToList();
        Assert.NotEmpty(callers);

        var toTarget = callers.Where(e => e.GetProperty("to").GetString() == $"symbol:{fixture.TargetSymbolId}").ToList();
        Assert.True(toTarget.Count >= 2);
        Assert.Contains(toTarget, e => e.GetProperty("from").GetString() == $"symbol:{fixture.CallerASymbolId}");
        Assert.Contains(toTarget, e => e.GetProperty("from").GetString() == $"symbol:{fixture.CallerCSymbolId}");
    }

    private sealed class GraphTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public HttpClient Client { get; }

        private GraphTestHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public static async Task<GraphTestHost> StartAsync(FixtureData fixture)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddSingleton(new QueryTelemetry());
            builder.Services.AddSingleton<ICodeMapCache>(fixture.Cache);
            builder.Services.AddSingleton(fixture.Registry);

            var app = builder.Build();
            app.MapGraphRoutes();

            var port = 5300 + Random.Shared.Next(0, 200);
            var url = $"http://127.0.0.1:{port}";
            app.Urls.Add(url);
            await app.StartAsync();

            var client = new HttpClient { BaseAddress = new Uri(url) };
            return new GraphTestHost(app, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class FixtureData : IAsyncDisposable
    {
        public required string ProjectName { get; init; }
        public required string TempRoot { get; init; }
        public required string TargetSymbolId { get; init; }
        public required string CallerASymbolId { get; init; }
        public required string CallerCSymbolId { get; init; }
        public required ICodeMapCache Cache { get; init; }
        public required ProjectRegistry Registry { get; init; }

        public static FixtureData Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"llens-graph-fixture-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            var fileA = Path.Combine(root, "a.cs");
            var fileB = Path.Combine(root, "b.cs");
            var fileC = Path.Combine(root, "c.cs");

            File.WriteAllText(fileA, """
                class A {
                  void Run() {
                    Target();
                  }
                }
                """);

            File.WriteAllText(fileB, """
                class B {
                  void Target() { }
                }
                """);

            File.WriteAllText(fileC, """
                class C {
                  void Work() {
                    Target();
                  }
                }
                """);

            var project = "fixture";
            var targetId = $"{project}::{fileB}::Target::Method::2";
            var callerAId = $"{project}::{fileA}::Run::Method::2";
            var callerCId = $"{project}::{fileC}::Work::Method::2";

            var symbols = new[]
            {
                new CodeSymbol { Id = callerAId, RepoName = project, FilePath = fileA, Name = "Run", Kind = SymbolKind.Method, LineStart = 2, LineEnd = 4, Signature = "void Run()" },
                new CodeSymbol { Id = targetId, RepoName = project, FilePath = fileB, Name = "Target", Kind = SymbolKind.Method, LineStart = 2, LineEnd = 2, Signature = "void Target()" },
                new CodeSymbol { Id = callerCId, RepoName = project, FilePath = fileC, Name = "Work", Kind = SymbolKind.Method, LineStart = 2, LineEnd = 4, Signature = "void Work()" }
            };

            var refs = new[]
            {
                new SymbolReference { SymbolId = targetId, InFilePath = fileA, RepoName = project, Line = 3, Context = "Target();" },
                new SymbolReference { SymbolId = targetId, InFilePath = fileC, RepoName = project, Line = 3, Context = "Target();" }
            };

            var cache = new FixtureCache(symbols, refs, project, new[]
            {
                new FileNode { FilePath = fileA, RepoName = project, Language = "CSharp", Imports = [], SymbolCount = 1, LastIndexedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                new FileNode { FilePath = fileB, RepoName = project, Language = "CSharp", Imports = [], SymbolCount = 1, LastIndexedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                new FileNode { FilePath = fileC, RepoName = project, Language = "CSharp", Imports = [], SymbolCount = 1, LastIndexedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
            });

            var registry = new ProjectRegistry(new[]
            {
                new Project(
                    new RepoConfig { Name = project, Path = root, Languages = [LanguageId.CSharp], ExcludePaths = [] },
                    new LanguageRegistry([]))
            });

            return new FixtureData
            {
                ProjectName = project,
                TempRoot = root,
                TargetSymbolId = targetId,
                CallerASymbolId = callerAId,
                CallerCSymbolId = callerCId,
                Cache = cache,
                Registry = registry
            };
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(TempRoot))
                    Directory.Delete(TempRoot, recursive: true);
            }
            catch
            {
                // best effort
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixtureCache : ICodeMapCache
    {
        private readonly List<CodeSymbol> _symbols;
        private readonly List<SymbolReference> _refs;
        private readonly Dictionary<string, FileNode> _files;

        public FixtureCache(IEnumerable<CodeSymbol> symbols, IEnumerable<SymbolReference> refs, string repo, IEnumerable<FileNode> files)
        {
            _symbols = symbols.ToList();
            _refs = refs.ToList();
            _files = files.ToDictionary(f => Path.GetFullPath(f.FilePath), StringComparer.OrdinalIgnoreCase);
        }

        public Task StoreSymbolsAsync(string filePath, IEnumerable<CodeSymbol> symbols, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveFileAsync(string filePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IEnumerable<CodeSymbol>> QueryByNameAsync(string name, string? repoName = null, CancellationToken ct = default)
        {
            var q = _symbols
                .Where(s => repoName is null || s.RepoName.Equals(repoName, StringComparison.OrdinalIgnoreCase))
                .Where(s => string.IsNullOrEmpty(name) || s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(q);
        }

        public Task<IEnumerable<CodeSymbol>> QueryByFileAsync(string filePath, CancellationToken ct = default)
            => Task.FromResult(_symbols.Where(s => s.FilePath.Equals(Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase)));

        public Task<IEnumerable<CodeSymbol>> QueryByKindAsync(SymbolKind kind, string? repoName = null, CancellationToken ct = default)
            => Task.FromResult(_symbols.Where(s => s.Kind == kind && (repoName is null || s.RepoName.Equals(repoName, StringComparison.OrdinalIgnoreCase))));

        public Task<IEnumerable<CodeSymbol>> QueryImplementorsAsync(string symbolName, string? repoName = null, CancellationToken ct = default)
            => Task.FromResult(Enumerable.Empty<CodeSymbol>());

        public Task RemoveReferencesInFileAsync(string filePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task StoreReferencesAsync(IEnumerable<SymbolReference> references, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IEnumerable<SymbolReference>> QueryReferencesAsync(string symbolId, string? repoName = null, CancellationToken ct = default)
            => Task.FromResult(_refs.Where(r => r.SymbolId.Equals(symbolId, StringComparison.OrdinalIgnoreCase) &&
                                                (repoName is null || r.RepoName.Equals(repoName, StringComparison.OrdinalIgnoreCase))));

        public Task StoreFileNodeAsync(FileNode file, CancellationToken ct = default) => Task.CompletedTask;

        public Task<FileNode?> GetFileNodeAsync(string filePath, CancellationToken ct = default)
        {
            _files.TryGetValue(Path.GetFullPath(filePath), out var node);
            return Task.FromResult(node);
        }

        public Task<IEnumerable<FileNode>> GetDependentsAsync(string filePath, string? repoName = null, CancellationToken ct = default)
        {
            var full = Path.GetFullPath(filePath);
            var deps = _files.Values.Where(f => f.Imports.Contains(full, StringComparer.OrdinalIgnoreCase))
                .Where(f => repoName is null || f.RepoName.Equals(repoName, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(deps);
        }

        public Task<IEnumerable<FileNode>> GetAllFilesAsync(string repoName, CancellationToken ct = default)
            => Task.FromResult(_files.Values.Where(f => f.RepoName.Equals(repoName, StringComparison.OrdinalIgnoreCase)));

        public async Task<string?> GetSourceContextAsync(string filePath, int line, int radiusLines = 20, CancellationToken ct = default)
        {
            if (!File.Exists(filePath)) return null;
            var lines = await File.ReadAllLinesAsync(filePath, ct);
            var from = Math.Max(0, line - radiusLines - 1);
            var to = Math.Min(lines.Length - 1, line + radiusLines - 1);
            return string.Join('\n', lines[from..(to + 1)]);
        }
    }
}
