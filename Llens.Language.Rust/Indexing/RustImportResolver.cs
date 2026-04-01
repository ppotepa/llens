using System.Text.RegularExpressions;

namespace Llens.Indexing;

/// <summary>
/// Resolves Rust imports to concrete file paths when they point to local crate modules.
/// Handles crate/self/super roots, nested group imports, and plain module paths.
/// </summary>
internal static class RustImportResolver
{
    public static IReadOnlyList<string> ResolveToFilePaths(string repoRoot, string filePath, IEnumerable<string> rawImports)
    {
        var fullFile = Path.GetFullPath(filePath);
        var crateRoot = FindCrateRoot(fullFile) ?? (Directory.Exists(repoRoot) ? FindCrateRoot(Path.GetFullPath(repoRoot)) : null);
        if (crateRoot is null)
            return [];

        var srcRoot = Directory.Exists(Path.Combine(crateRoot, "src"))
            ? Path.Combine(crateRoot, "src")
            : crateRoot;

        var currentModule = GetCurrentModuleSegments(srcRoot, fullFile);
        if (currentModule is null)
            return [];

        var crateName = ReadCrateName(crateRoot);
        var localCrates = ResolveLocalCrateSrcRoots(repoRoot, crateRoot);
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var import in rawImports.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var expanded in ExpandImportPaths(import))
            {
                var candidate = ResolveSingleImport(srcRoot, currentModule, expanded, crateName, localCrates);
                if (candidate is null) continue;
                if (candidate.Equals(fullFile, StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(candidate);
            }
        }

        return [.. results];
    }

    private static string? ResolveSingleImport(
        string srcRoot,
        IReadOnlyList<string> currentModule,
        string importPath,
        string? crateName,
        IReadOnlyDictionary<string, string> localCrates)
    {
        if (string.IsNullOrWhiteSpace(importPath))
            return null;

        var clean = CleanupImport(importPath);
        var segments = clean.Split("::", StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count == 0)
            return null;

        if (segments[0].Equals("crate", StringComparison.OrdinalIgnoreCase))
            return ResolveModuleToFile(srcRoot, segments.Skip(1).ToList());

        if (!string.IsNullOrWhiteSpace(crateName) && segments[0].Equals(crateName, StringComparison.OrdinalIgnoreCase))
            return ResolveModuleToFile(srcRoot, segments.Skip(1).ToList());

        if (segments[0].Equals("self", StringComparison.OrdinalIgnoreCase))
            return ResolveModuleToFile(srcRoot, [.. currentModule, .. segments.Skip(1)]);

        if (segments[0].Equals("super", StringComparison.OrdinalIgnoreCase))
        {
            var scope = currentModule.ToList();
            var i = 0;
            while (i < segments.Count && segments[i].Equals("super", StringComparison.OrdinalIgnoreCase))
            {
                if (scope.Count == 0) break;
                scope.RemoveAt(scope.Count - 1);
                i++;
            }
            return ResolveModuleToFile(srcRoot, [.. scope, .. segments.Skip(i)]);
        }

        if (localCrates.TryGetValue(NormalizeCrateKey(segments[0]), out var depSrcRoot))
        {
            var depResolved = ResolveModuleToFile(depSrcRoot, segments.Skip(1).ToList());
            if (depResolved is not null) return depResolved;
        }

        // Ambiguous path (could be external crate). Try local crate absolute first.
        var absolute = ResolveModuleToFile(srcRoot, segments);
        if (absolute is not null) return absolute;

        // Then try current module scope and current module parent scope.
        var local = ResolveModuleToFile(srcRoot, [.. currentModule, .. segments]);
        if (local is not null) return local;

        if (currentModule.Count > 0)
        {
            var parent = currentModule.Take(currentModule.Count - 1).ToList();
            local = ResolveModuleToFile(srcRoot, [.. parent, .. segments]);
            if (local is not null) return local;
        }

        return null;
    }

    private static string CleanupImport(string importPath)
    {
        var clean = importPath.Trim().TrimEnd(';').Trim();
        if (clean.StartsWith("pub ", StringComparison.OrdinalIgnoreCase))
            clean = clean[4..].TrimStart();
        if (clean.StartsWith("use ", StringComparison.OrdinalIgnoreCase))
            clean = clean[4..].TrimStart();
        if (clean.EndsWith("::*", StringComparison.Ordinal))
            clean = clean[..^3];
        var asIndex = clean.IndexOf(" as ", StringComparison.OrdinalIgnoreCase);
        if (asIndex >= 0)
            clean = clean[..asIndex];
        if (clean.EndsWith("::self", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^6];
        return clean.Trim();
    }

    private static List<string> ExpandImportPaths(string importPath)
    {
        var clean = CleanupImport(importPath);
        if (string.IsNullOrWhiteSpace(clean)) return [];
        var braceStart = clean.IndexOf('{');
        if (braceStart < 0) return [clean];

        var braceEnd = FindMatchingBrace(clean, braceStart);
        if (braceEnd < 0) return [clean];

        var prefix = clean[..braceStart].TrimEnd(':');
        var inner = clean[(braceStart + 1)..braceEnd];
        var suffix = clean[(braceEnd + 1)..].Trim();
        suffix = suffix.StartsWith("::", StringComparison.Ordinal) ? suffix : "";

        var parts = SplitTopLevel(inner, ',');
        var expanded = new List<string>(parts.Count);
        foreach (var rawPart in parts)
        {
            var part = rawPart.Trim();
            if (part.Length == 0) continue;
            foreach (var nested in ExpandImportPaths(part))
            {
                if (nested.Equals("self", StringComparison.OrdinalIgnoreCase))
                    expanded.Add(prefix);
                else
                    expanded.Add($"{prefix}::{nested}{suffix}");
            }
        }

        return expanded.Count == 0 ? [clean] : expanded;
    }

    private static int FindMatchingBrace(string text, int braceStart)
    {
        var depth = 0;
        for (var i = braceStart; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == separator && depth == 0)
            {
                result.Add(text[start..i]);
                start = i + 1;
            }
        }
        result.Add(text[start..]);
        return result;
    }

    private static string? ResolveModuleToFile(string srcRoot, List<string> moduleSegments)
    {
        if (moduleSegments.Count == 0)
        {
            var main = Path.Combine(srcRoot, "main.rs");
            if (File.Exists(main)) return Path.GetFullPath(main);
            var lib = Path.Combine(srcRoot, "lib.rs");
            return File.Exists(lib) ? Path.GetFullPath(lib) : null;
        }

        for (var len = moduleSegments.Count; len >= 1; len--)
        {
            var prefix = moduleSegments.Take(len).ToArray();
            var rsFile = Path.GetFullPath(Path.Combine(srcRoot, Path.Combine(prefix) + ".rs"));
            if (File.Exists(rsFile)) return rsFile;

            var modFile = Path.GetFullPath(Path.Combine(srcRoot, Path.Combine(prefix), "mod.rs"));
            if (File.Exists(modFile)) return modFile;
        }

        return null;
    }

    private static List<string>? GetCurrentModuleSegments(string srcRoot, string fullFilePath)
    {
        var rel = Path.GetRelativePath(srcRoot, fullFilePath);
        if (rel.StartsWith("..", StringComparison.Ordinal))
            return null;

        var parts = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0) return null;

        var file = parts[^1];
        var dirs = parts.Take(parts.Count - 1).ToList();

        if (file.Equals("lib.rs", StringComparison.OrdinalIgnoreCase)
            || file.Equals("main.rs", StringComparison.OrdinalIgnoreCase))
            return dirs;

        if (file.Equals("mod.rs", StringComparison.OrdinalIgnoreCase))
            return dirs;

        var stem = Path.GetFileNameWithoutExtension(file);
        return [.. dirs, stem];
    }

    private static string? FindCrateRoot(string startPath)
    {
        var dir = File.Exists(startPath) ? Path.GetDirectoryName(startPath) : startPath;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Cargo.toml")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static string? ReadCrateName(string crateRoot)
    {
        var cargoToml = Path.Combine(crateRoot, "Cargo.toml");
        if (!File.Exists(cargoToml)) return null;

        var inPackage = false;
        foreach (var line in File.ReadLines(cargoToml))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                inPackage = trimmed.Equals("[package]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inPackage) continue;
            if (!trimmed.StartsWith("name", StringComparison.Ordinal)) continue;
            var idx = trimmed.IndexOf('=');
            if (idx < 0) continue;
            var value = trimmed[(idx + 1)..].Trim().Trim('"');
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static Dictionary<string, string> ResolveLocalCrateSrcRoots(string repoRoot, string crateRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in ReadPathDependencies(crateRoot))
            map[kv.Key] = kv.Value;

        var workspaceRoot = FindWorkspaceRoot(crateRoot) ?? (FindWorkspaceRoot(Path.GetFullPath(repoRoot)) ?? crateRoot);
        foreach (var memberRoot in EnumerateWorkspaceMembers(workspaceRoot))
        {
            var name = ReadCrateName(memberRoot);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var src = Directory.Exists(Path.Combine(memberRoot, "src")) ? Path.Combine(memberRoot, "src") : memberRoot;
            map[NormalizeCrateKey(name)] = src;
        }

        var currentName = ReadCrateName(crateRoot);
        if (!string.IsNullOrWhiteSpace(currentName))
        {
            var currentSrc = Directory.Exists(Path.Combine(crateRoot, "src")) ? Path.Combine(crateRoot, "src") : crateRoot;
            map[NormalizeCrateKey(currentName)] = currentSrc;
        }

        return map;
    }

    private static string? FindWorkspaceRoot(string startPath)
    {
        var dir = File.Exists(startPath) ? Path.GetDirectoryName(startPath) : startPath;
        while (dir is not null)
        {
            var cargoToml = Path.Combine(dir, "Cargo.toml");
            if (File.Exists(cargoToml))
            {
                var text = File.ReadAllText(cargoToml);
                if (text.Contains("[workspace]", StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static IEnumerable<string> EnumerateWorkspaceMembers(string workspaceRoot)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(workspaceRoot))
            return roots;

        var rootCargo = Path.Combine(workspaceRoot, "Cargo.toml");
        if (File.Exists(rootCargo))
        {
            roots.Add(workspaceRoot);
            foreach (var member in ParseWorkspaceMembers(rootCargo))
            {
                var candidate = Path.GetFullPath(Path.Combine(workspaceRoot, member));
                if (!candidate.StartsWith(Path.GetFullPath(workspaceRoot), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (member.Contains('*'))
                {
                    var baseDir = candidate[..candidate.IndexOf('*')].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (Directory.Exists(baseDir))
                    {
                        foreach (var sub in Directory.EnumerateDirectories(baseDir))
                        {
                            if (File.Exists(Path.Combine(sub, "Cargo.toml")))
                                roots.Add(Path.GetFullPath(sub));
                        }
                    }
                    continue;
                }

                if (File.Exists(Path.Combine(candidate, "Cargo.toml")))
                    roots.Add(candidate);
            }
        }

        return roots;
    }

    private static IEnumerable<string> ParseWorkspaceMembers(string cargoTomlPath)
    {
        var text = File.ReadAllText(cargoTomlPath);
        var m = Regex.Match(text, @"members\s*=\s*\[(?<body>[\s\S]*?)\]", RegexOptions.IgnoreCase);
        if (!m.Success) return [];
        return Regex.Matches(m.Groups["body"].Value, "\"([^\"]+)\"")
            .Select(x => x.Groups[1].Value.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadPathDependencies(string crateRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cargoToml = Path.Combine(crateRoot, "Cargo.toml");
        if (!File.Exists(cargoToml)) return map;

        var inDeps = false;
        foreach (var raw in File.ReadLines(cargoToml))
        {
            var line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                inDeps = line.Equals("[dependencies]", StringComparison.OrdinalIgnoreCase)
                    || line.Equals("[dev-dependencies]", StringComparison.OrdinalIgnoreCase)
                    || line.Equals("[build-dependencies]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inDeps || line.Length == 0 || line.StartsWith('#')) continue;

            var m = Regex.Match(line, @"^(?<name>[A-Za-z0-9_\-]+)\s*=\s*\{[^}]*path\s*=\s*""(?<path>[^""]+)""", RegexOptions.IgnoreCase);
            if (!m.Success) continue;

            var name = m.Groups["name"].Value;
            var path = m.Groups["path"].Value;
            var depRoot = Path.GetFullPath(Path.Combine(crateRoot, path));
            if (!File.Exists(Path.Combine(depRoot, "Cargo.toml"))) continue;
            var src = Directory.Exists(Path.Combine(depRoot, "src")) ? Path.Combine(depRoot, "src") : depRoot;
            map[NormalizeCrateKey(name)] = src;
        }

        return map;
    }

    private static string NormalizeCrateKey(string name)
        => name.Replace('-', '_').Trim();
}
