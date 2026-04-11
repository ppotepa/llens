using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Llens.Models;
using Llens.Tools;

namespace Llens.Languages.Rust;

/// <summary>
/// Wraps the external <c>syn-shim</c> binary to extract Rust symbols and imports.
/// Out-of-process execution keeps Rust parsing logic in a native Rust binary.
/// </summary>
public sealed class SynShimExtractor : ISymbolExtractor<Rust>
{
    private static readonly string? BinaryPath = FindBinary();

    public async Task<ToolResult> ExtractAsync(ToolContext context, CancellationToken ct = default)
    {
        if (BinaryPath is null)
            return ToolResult.Fail("syn-shim binary not found. Run 'cargo build --release' in tools/syn-shim.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = BinaryPath,
                Arguments = $"\"{context.FilePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            return ToolResult.Fail($"syn-shim exited with code {process.ExitCode}");

        ShimOutput? output;
        try { output = JsonSerializer.Deserialize<ShimOutput>(stdout); }
        catch (JsonException ex) { return ToolResult.Fail($"Failed to parse syn-shim output: {ex.Message}"); }

        if (output is null)
            return ToolResult.Fail("syn-shim returned null output");

        var symbols = output.Symbols.Select(s => new CodeSymbol
        {
            Id = $"{context.RepoName}:{context.FilePath}:{s.Name}:{s.Line}",
            RepoName = context.RepoName,
            FilePath = context.FilePath,
            Name = s.Name,
            Kind = ParseKind(s.Kind),
            LineStart = s.Line,
            Signature = s.Signature,
        }).ToList();

        return ToolResult.Ok(symbols, output.Imports);
    }

    private static SymbolKind ParseKind(string kind) => kind switch
    {
        "Function"  => SymbolKind.Function,
        "Struct"    => SymbolKind.Struct,
        "Enum"      => SymbolKind.Enum,
        "Trait"     => SymbolKind.Trait,
        "TraitImpl" => SymbolKind.TraitImpl,
        "Module"    => SymbolKind.Module,
        _           => SymbolKind.Unknown,
    };

    private static string? FindBinary()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "syn-shim", "target", "release", "syn-shim"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "syn-shim", "target", "release", "syn-shim"),
            FindFromCwd(),
        };

        return candidates.Select(p => p is null ? null : Path.GetFullPath(p))
                         .FirstOrDefault(p => p is not null && File.Exists(p));
    }

    private static string? FindFromCwd()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tools", "syn-shim", "target", "release", "syn-shim");
            if (File.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private sealed class ShimOutput
    {
        [JsonPropertyName("symbols")] public List<ShimSymbol> Symbols { get; set; } = [];
        [JsonPropertyName("imports")] public List<string> Imports { get; set; } = [];
    }

    private sealed class ShimSymbol
    {
        [JsonPropertyName("name")]      public string Name      { get; set; } = "";
        [JsonPropertyName("kind")]      public string Kind      { get; set; } = "";
        [JsonPropertyName("line")]      public int    Line      { get; set; }
        [JsonPropertyName("signature")] public string? Signature { get; set; }
    }
}
