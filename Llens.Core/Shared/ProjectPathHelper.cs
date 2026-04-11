namespace Llens.Shared;

public static class ProjectPathHelper
{
    public static string? EnsureWithinProject(string projectRoot, string relativeOrAbsolutePath)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(projectRoot, relativeOrAbsolutePath));

        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefixA = root + Path.DirectorySeparatorChar;
        var rootPrefixB = root + Path.AltDirectorySeparatorChar;

        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(rootPrefixA, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(rootPrefixB, StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    public static bool IsPathWithin(string filePath, string scopePath)
    {
        var fullFile = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullScope = Path.GetFullPath(scopePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullFile.Equals(fullScope, StringComparison.OrdinalIgnoreCase)
               || fullFile.StartsWith(fullScope + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || fullFile.StartsWith(fullScope + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizePathForMatch(string path)
        => path.Replace('\\', '/').Trim();
}
