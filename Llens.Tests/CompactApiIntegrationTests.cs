using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Llens.Tests;

public sealed class LlensServerFixture : IAsyncLifetime
{
    private Process? _proc;
    private HttpClient? _client;

    public string BaseUrl { get; private set; } = "";
    public string ProjectName { get; private set; } = "";
    public string SeedFilePath { get; private set; } = "";

    public HttpClient Client => _client ?? throw new InvalidOperationException("Server client not initialized.");

    public async Task InitializeAsync()
    {
        var port = 5190 + Random.Shared.Next(0, 80);
        BaseUrl = $"http://127.0.0.1:{port}";

        var repoRoot = FindRepoRoot();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{Path.Combine(repoRoot, "Llens.csproj")}\" --urls {BaseUrl}",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start llens server process.");
        _client = new HttpClient { BaseAddress = new Uri(BaseUrl) };

        await WaitForHealthyAsync(_client);
        await WaitForIndexReadyAsync(_client);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null)
            _client.Dispose();

        if (_proc is null) return;
        if (!_proc.HasExited)
        {
            try
            {
                _proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // best effort
            }
        }
        _proc.Dispose();
        await Task.CompletedTask;
    }

    private static async Task WaitForHealthyAsync(HttpClient client)
    {
        var timeout = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < timeout)
        {
            try
            {
                using var resp = await client.GetAsync("/health");
                if (resp.IsSuccessStatusCode)
                    return;
            }
            catch
            {
                // retry
            }
            await Task.Delay(1000);
        }
        throw new TimeoutException("Server did not become healthy within timeout.");
    }

    private async Task WaitForIndexReadyAsync(HttpClient client)
    {
        var timeout = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < timeout)
        {
            var health = await client.GetFromJsonAsync<JsonElement>("/health");
            if (health.ValueKind == JsonValueKind.Undefined)
            {
                await Task.Delay(1000);
                continue;
            }

            var projects = health.GetProperty("projects");
            var llens = projects.EnumerateArray()
                .FirstOrDefault(p => string.Equals(p.GetProperty("name").GetString(), "llens", StringComparison.OrdinalIgnoreCase));

            if (llens.ValueKind == JsonValueKind.Undefined)
            {
                await Task.Delay(1000);
                continue;
            }

            var filesResp = await client.GetAsync("/api/files/?project=llens");
            if (!filesResp.IsSuccessStatusCode)
            {
                await Task.Delay(1000);
                continue;
            }

            using var filesDoc = JsonDocument.Parse(await filesResp.Content.ReadAsStringAsync());
            var files = filesDoc.RootElement;
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
            {
                await Task.Delay(1000);
                continue;
            }

            var seed = files.EnumerateArray()
                .Select(x => x.GetProperty("filePath").GetString())
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && Path.GetFileName(x) == "Program.cs")
                ?? files[0].GetProperty("filePath").GetString();

            if (string.IsNullOrWhiteSpace(seed))
            {
                await Task.Delay(1000);
                continue;
            }

            ProjectName = "llens";
            SeedFilePath = seed;
            return;
        }

        throw new TimeoutException("Index was not ready within timeout.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Llens.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root containing Llens.csproj.");
    }
}

[CollectionDefinition("llens-server")]
public sealed class LlensServerCollectionDefinition : ICollectionFixture<LlensServerFixture>
{
}

[Collection("llens-server")]
public sealed class CompactApiIntegrationTests
{
    private readonly LlensServerFixture _fx;

    public CompactApiIntegrationTests(LlensServerFixture fx)
    {
        _fx = fx;
    }

    [Fact]
    public async Task Resolve_ReturnsExpectedShape()
    {
        var payload = """
                      {
                        "project":"llens",
                        "q":"compact graph endpoint",
                        "limit": 8
                      }
                      """;

        using var resp = await PostAsync("/api/compact/resolve", payload);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("llens", root.GetProperty("project").GetString());
        Assert.True(root.GetProperty("count").GetInt32() >= 0);
        var stage = root.GetProperty("stage").GetString();
        Assert.True(stage is "exact" or "fuzzy" or "snippet");
        Assert.Equal(JsonValueKind.Array, root.GetProperty("items").ValueKind);
    }

    [Fact]
    public async Task ContextPack_ReturnsBudgetedItemsShape()
    {
        var payload = """
                      {
                        "project":"llens",
                        "q":"compact graph endpoint",
                        "mode":"impact",
                        "tokenBudget": 700,
                        "maxItems": 20
                      }
                      """;

        using var resp = await PostAsync("/api/compact/context-pack", payload);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("llens", root.GetProperty("project").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("items").ValueKind);
        Assert.True(root.GetProperty("tokens").GetInt32() <= root.GetProperty("tokenBudget").GetInt32());
    }

    [Fact]
    public async Task Graph_AcceptsFileSeedAndReturnsNodesEdges()
    {
        var payload = $$"""
                        {
                          "project":"{{_fx.ProjectName}}",
                          "seed": { "type":"file", "path":"{{EscapeJson(_fx.SeedFilePath)}}" },
                          "depth": 1,
                          "maxNodes": 40
                        }
                        """;

        using var resp = await PostAsync("/api/compact/graph", payload);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(_fx.ProjectName, root.GetProperty("project").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("nodes").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("edges").ValueKind);
        Assert.True(root.GetProperty("nodes").GetArrayLength() > 0);
    }

    [Fact]
    public async Task WorkflowRun_ReturnsResolveAndContextPack()
    {
        var payload = """
                      {
                        "project":"llens",
                        "q":"compact graph endpoint",
                        "limit":8,
                        "tokenBudget":900,
                        "maxItems":20
                      }
                      """;

        using var resp = await PostAsync("/api/compact/workflow/run", payload);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("llens", root.GetProperty("project").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("resolve").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("contextPack").ValueKind);
    }

    [Fact]
    public async Task Reindex_FileScope_ReturnsExpectedShape()
    {
        var payload = """
                      {
                        "project":"llens",
                        "path":"Program.cs",
                        "pruneStale": true
                      }
                      """;

        using var resp = await PostAsync("/api/compact/reindex", payload);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("llens", root.GetProperty("project").GetString());
        Assert.Equal("file", root.GetProperty("mode").GetString());
        Assert.True(root.GetProperty("indexed").GetInt32() >= 0);
        Assert.True(root.GetProperty("removed").GetInt32() >= 0);
    }

    [Fact]
    public async Task TestRun_RustBasic_ReturnsExpectedShape()
    {
        var payload = """
                      {
                        "project":"rust-basic",
                        "target":"cargo",
                        "timeoutMs":120000,
                        "maxChars":4000
                      }
                      """;

        using var resp = await PostAsync("/api/compact/test/run", payload);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("rust-basic", root.GetProperty("project").GetString());
        Assert.Equal("cargo", root.GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("diagnostics").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("output").ValueKind);
    }

    [Fact]
    public async Task FormatAndLint_ReturnExpectedShape()
    {
        var formatPayload = """
                            {
                              "project":"rust-basic",
                              "target":"cargo",
                              "checkOnly":true,
                              "timeoutMs":120000,
                              "maxChars":4000
                            }
                            """;

        using var formatResp = await PostAsync("/api/compact/format", formatPayload);
        formatResp.EnsureSuccessStatusCode();
        using var formatDoc = JsonDocument.Parse(await formatResp.Content.ReadAsStringAsync());
        var formatRoot = formatDoc.RootElement;
        Assert.Equal("rust-basic", formatRoot.GetProperty("project").GetString());
        Assert.Equal("cargo", formatRoot.GetProperty("target").GetString());
        Assert.True(formatRoot.GetProperty("checkOnly").GetBoolean());
        Assert.Equal(JsonValueKind.String, formatRoot.GetProperty("output").ValueKind);

        var lintPayload = """
                          {
                            "project":"llens",
                            "target":"dotnet",
                            "timeoutMs":120000,
                            "maxChars":4000
                          }
                          """;

        using var lintResp = await PostAsync("/api/compact/lint", lintPayload);
        lintResp.EnsureSuccessStatusCode();
        using var lintDoc = JsonDocument.Parse(await lintResp.Content.ReadAsStringAsync());
        var lintRoot = lintDoc.RootElement;
        Assert.Equal("llens", lintRoot.GetProperty("project").GetString());
        Assert.Equal("dotnet", lintRoot.GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Array, lintRoot.GetProperty("diagnostics").ValueKind);
        Assert.Equal(JsonValueKind.String, lintRoot.GetProperty("output").ValueKind);

        var typecheckPayload = """
                               {
                                 "project":"llens",
                                 "target":"dotnet",
                                 "timeoutMs":120000,
                                 "maxChars":4000
                               }
                               """;

        using var tcResp = await PostAsync("/api/compact/typecheck", typecheckPayload);
        tcResp.EnsureSuccessStatusCode();
        using var tcDoc = JsonDocument.Parse(await tcResp.Content.ReadAsStringAsync());
        var tcRoot = tcDoc.RootElement;
        Assert.Equal("llens", tcRoot.GetProperty("project").GetString());
        Assert.Equal("dotnet", tcRoot.GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Array, tcRoot.GetProperty("diagnostics").ValueKind);
        Assert.Equal(JsonValueKind.String, tcRoot.GetProperty("output").ValueKind);
    }

    private Task<HttpResponseMessage> PostAsync(string path, string payload)
        => _fx.Client.PostAsync(path, new StringContent(payload, Encoding.UTF8, "application/json"));

    private static string EscapeJson(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
