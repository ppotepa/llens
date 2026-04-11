using System.Text.RegularExpressions;

namespace Llens.Languages.Rust;

/// <summary>
/// Regex-based usage extractor for Rust source.
/// Extracts calls, path segments, type annotations, generic args, and struct literals.
/// </summary>
public sealed class RustUsageExtractor : IUsageExtractor<Rust>
{
    private static readonly Regex RustCallRegex = new(@"(?:(?:\b|::|\.)([_A-Za-z][_A-Za-z0-9]*))\s*\(", RegexOptions.Compiled);
    private static readonly Regex RustPathRegex = new(@"\b(?:crate|self|super|[_A-Za-z][_A-Za-z0-9]*)(?:::[_A-Za-z][_A-Za-z0-9]*)+\b", RegexOptions.Compiled);
    private static readonly Regex RustTypeAnnotationRegex = new(@"(?:->|:)\s*&?\s*(?:'[_A-Za-z][_A-Za-z0-9]*\s+)?(?:mut\s+)?([_A-Za-z][_A-Za-z0-9]*)", RegexOptions.Compiled);
    private static readonly Regex RustGenericArgRegex = new(@"(?:<|,)\s*&?\s*(?:'[_A-Za-z][_A-Za-z0-9]*\s+)?([_A-Za-z][_A-Za-z0-9]*)", RegexOptions.Compiled);
    private static readonly Regex RustStructLiteralRegex = new(@"\b([A-Z][_A-Za-z0-9]*)\s*\{", RegexOptions.Compiled);

    private static readonly HashSet<string> KeywordStopWords = new(StringComparer.Ordinal)
    {
        "fn", "mod", "pub", "crate", "self", "super", "impl", "trait", "let", "mut", "match", "where", "const", "static",
        "use", "enum", "struct", "type", "unsafe", "move", "ref", "as", "in", "loop", "dyn", "Self"
    };

    public IEnumerable<(string Token, int Line, string Context)> Extract(string filePath, IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var line = RemoveRustCommentTail(raw);
            if (string.IsNullOrWhiteSpace(line)) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match m in RustCallRegex.Matches(line))
            {
                if (TryGetToken(m.Groups[1].Value, out var token) && seen.Add(token))
                    yield return (token, i + 1, raw.Trim());
            }

            foreach (Match m in RustPathRegex.Matches(line))
            {
                var segments = m.Value.Split("::", StringSplitOptions.RemoveEmptyEntries);
                foreach (var segment in segments)
                {
                    if (TryGetToken(segment, out var token) && seen.Add(token))
                        yield return (token, i + 1, raw.Trim());
                }
            }

            foreach (Match m in RustTypeAnnotationRegex.Matches(line))
            {
                if (TryGetToken(m.Groups[1].Value, out var token) && seen.Add(token))
                    yield return (token, i + 1, raw.Trim());
            }

            foreach (Match m in RustGenericArgRegex.Matches(line))
            {
                if (TryGetToken(m.Groups[1].Value, out var token) && seen.Add(token))
                    yield return (token, i + 1, raw.Trim());
            }

            foreach (Match m in RustStructLiteralRegex.Matches(line))
            {
                if (TryGetToken(m.Groups[1].Value, out var token) && seen.Add(token))
                    yield return (token, i + 1, raw.Trim());
            }
        }
    }

    private static string RemoveRustCommentTail(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    private static bool TryGetToken(string raw, out string token)
    {
        token = raw.Trim();
        if (string.IsNullOrWhiteSpace(token) || token.Length < 2)
            return false;
        if (KeywordStopWords.Contains(token))
            return false;
        if (token.All(c => c == '_'))
            return false;
        return true;
    }
}
