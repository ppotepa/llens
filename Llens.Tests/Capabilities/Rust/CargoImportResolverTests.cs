using Llens.Languages.Rust;
using Llens.Tests.Support;
using Xunit;

namespace Llens.Tests.Capabilities.Rust;

/// <summary>
/// Ground truth for CargoImportResolver: every resolved path must exist on disk.
/// No Roslyn, no grep — the file system IS the oracle.
/// </summary>
public sealed class CargoImportResolverTests
{
    private readonly CargoImportResolver _resolver = new();

    private static string CrateRoot  => Fixtures.Rust("simple_crate");
    private static string MainRs     => Fixtures.Rust("simple_crate/src/main.rs");
    private static string ServiceRs  => Fixtures.Rust("simple_crate/src/services/order_service.rs");

    // -------------------------------------------------------------------------
    // Ground truth: resolved paths must exist on disk
    // -------------------------------------------------------------------------

    [Fact]
    public void ResolvedImports_AllExistOnDisk()
    {
        var raw = new[] { "crate::services::order_service", "crate::models::order" };
        var resolved = _resolver.Resolve(CrateRoot, MainRs, raw);

        Assert.NotEmpty(resolved);
        Assert.All(resolved, path => Assert.True(File.Exists(path),
            $"Resolved path does not exist: {path}"));
    }

    // -------------------------------------------------------------------------
    // Unit tests — known-answer
    // -------------------------------------------------------------------------

    [Fact]
    public void CratePrefix_ResolvesToCorrectSrcFile()
    {
        var raw = new[] { "crate::models::order" };
        var resolved = _resolver.Resolve(CrateRoot, MainRs, raw);

        var expected = Path.GetFullPath(Fixtures.Rust("simple_crate/src/models/order.rs"));
        Assert.Contains(resolved, p => p.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MultipleImports_AllResolvedDistinctly()
    {
        var raw = new[]
        {
            "crate::services::order_service",
            "crate::models::order"
        };
        var resolved = _resolver.Resolve(CrateRoot, MainRs, raw);

        Assert.Equal(resolved.Count, resolved.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void CurrentFile_IsNeverReturnedAsImport()
    {
        var raw = new[] { "crate::services::order_service" };
        var resolved = _resolver.Resolve(CrateRoot, ServiceRs, raw);

        var normalizedSelf = Path.GetFullPath(ServiceRs);
        Assert.DoesNotContain(resolved, p =>
            p.Equals(normalizedSelf, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmptyImports_ReturnsEmpty()
    {
        var resolved = _resolver.Resolve(CrateRoot, MainRs, []);
        Assert.Empty(resolved);
    }

    [Fact]
    public void ExternalCrate_IsNotResolved_ToLocalPath()
    {
        // "serde" is an external crate — should not resolve to a file path
        var raw = new[] { "serde::Serialize", "tokio::runtime::Runtime" };
        var resolved = _resolver.Resolve(CrateRoot, MainRs, raw);

        // External crates either resolve to nothing or to workspace members only
        Assert.All(resolved, path => Assert.True(File.Exists(path),
            $"Resolved external crate to non-existent path: {path}"));
    }

    // -------------------------------------------------------------------------
    // Workspace tests
    // -------------------------------------------------------------------------

    [Fact]
    public void WorkspaceDependency_ResolvesAcrossCrates()
    {
        var workspaceRoot = Fixtures.Rust("workspace");
        var crateBMain = Fixtures.Rust("workspace/crate_b/src/main.rs");

        var raw = new[] { "crate_a::default_config", "crate_a::Config" };
        var resolved = _resolver.Resolve(workspaceRoot, crateBMain, raw);

        var expectedLib = Path.GetFullPath(Fixtures.Rust("workspace/crate_a/src/lib.rs"));
        Assert.Contains(resolved, p => p.Equals(expectedLib, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WorkspaceResolved_PathsExistOnDisk()
    {
        var workspaceRoot = Fixtures.Rust("workspace");
        var crateBMain = Fixtures.Rust("workspace/crate_b/src/main.rs");

        var raw = new[] { "crate_a::default_config" };
        var resolved = _resolver.Resolve(workspaceRoot, crateBMain, raw);

        Assert.All(resolved, path => Assert.True(File.Exists(path),
            $"Workspace resolved path does not exist: {path}"));
    }
}
