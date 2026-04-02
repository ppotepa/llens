using Llens.Caching;
using Llens.Models;

namespace Llens.Api;

internal static class IndexedPathResolver
{
    public static async Task<(string? Path, string? Error)> ResolveAsync(
        string inputPath,
        string? projectName,
        ProjectRegistry projects,
        ICodeMapCache cache,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            return (null, "'path' is required.");

        if (Path.IsPathRooted(inputPath))
            return (Path.GetFullPath(inputPath), null);

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            var project = projects.Resolve(projectName);
            if (project is null)
                return (null, $"Project '{projectName}' is not registered.");

            var expected = Path.GetFullPath(Path.Combine(project.Config.ResolvedPath, inputPath));
            var indexed = await cache.GetAllFilesAsync(project.Name, ct);
            if (indexed.Any(f => PathEquals(f.FilePath, expected)))
                return (expected, null);

            var match = MatchBySuffix(indexed.Select(f => f.FilePath), inputPath);
            return match;
        }

        var allFiles = new List<string>(capacity: 8192);
        foreach (var p in projects.All)
        {
            var files = await cache.GetAllFilesAsync(p.Name, ct);
            allFiles.AddRange(files.Select(f => f.FilePath));
        }

        return MatchBySuffix(allFiles, inputPath);
    }

    private static (string? Path, string? Error) MatchBySuffix(IEnumerable<string> files, string requestedPath)
    {
        var needle = NormalizeRel(requestedPath);
        var matches = files
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f =>
            {
                var normalized = NormalizeAbs(f);
                return normalized.Equals(needle, StringComparison.OrdinalIgnoreCase)
                    || normalized.EndsWith("/" + needle, StringComparison.OrdinalIgnoreCase);
            })
            .Take(6)
            .ToList();

        return matches.Count switch
        {
            0 => (null, $"No indexed file matches path '{requestedPath}'."),
            1 => (matches[0], null),
            _ => (null, $"Path '{requestedPath}' is ambiguous across indexed files. Provide 'project' or an absolute path.")
        };
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRel(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.TrimStart('/');
    }

    private static string NormalizeAbs(string path)
        => Path.GetFullPath(path).Replace('\\', '/');
}
