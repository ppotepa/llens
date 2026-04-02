namespace Llens.Api;

public static class CompactTupleCodec
{
    public static CompactTupleEnvelope FromQuery(CompactQueryResponse response)
    {
        var dict = BuildCommonDict(response.Items);
        var rows = response.Items
            .Select(i => (IReadOnlyList<object?>)
            [
                i.Id,
                i.T,
                i.N,
                IndexOf(dict.Paths, i.P),
                i.L,
                IndexOf(dict.Kinds, i.K),
                i.Sc
            ])
            .ToList();

        return new CompactTupleEnvelope
        {
            V = "2",
            Format = "tuple",
            Schema = "compact.query",
            Dict = new Dictionary<string, List<string>>
            {
                ["p"] = dict.Paths,
                ["k"] = dict.Kinds
            },
            Meta = new Dictionary<string, object?>
            {
                ["project"] = response.Project,
                ["mode"] = response.Mode,
                ["q"] = response.Q,
                ["count"] = response.Count,
                ["tokens"] = response.Tokens
            },
            Columns = ["id", "t", "n", "p", "l", "k", "sc"],
            Items = rows
        };
    }

    public static CompactTupleEnvelope FromContextPack(CompactContextPackResponse response)
    {
        var dict = BuildCommonDict(response.Items);
        var rows = response.Items
            .Select(i => (IReadOnlyList<object?>)
            [
                i.Id,
                i.T,
                i.N,
                IndexOf(dict.Paths, i.P),
                i.L,
                IndexOf(dict.Kinds, i.K),
                i.Sc
            ])
            .ToList();

        return new CompactTupleEnvelope
        {
            V = "2",
            Format = "tuple",
            Schema = "compact.context-pack",
            Dict = new Dictionary<string, List<string>>
            {
                ["p"] = dict.Paths,
                ["k"] = dict.Kinds
            },
            Meta = new Dictionary<string, object?>
            {
                ["project"] = response.Project,
                ["mode"] = response.Mode,
                ["q"] = response.Q,
                ["tokenBudget"] = response.TokenBudget,
                ["tokens"] = response.Tokens,
                ["count"] = response.Count
            },
            Columns = ["id", "t", "n", "p", "l", "k", "sc"],
            Items = rows
        };
    }

    public static CompactTupleEnvelope FromGitStatus(CompactGitStatusResponse response)
    {
        var paths = response.Entries
            .Select(e => e.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var xys = response.Entries
            .Select(e => e.Xy)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        IReadOnlyList<IReadOnlyList<object?>> rows;
        IReadOnlyList<string> columns;
        var dict = new Dictionary<string, List<string>>
        {
            ["p"] = paths,
            ["xy"] = xys
        };

        if (response.Entries.Count > 0)
        {
            rows = response.Entries
                .Select(e => (IReadOnlyList<object?>)
                [
                    IndexOf(paths, e.Path),
                    IndexOf(xys, e.Xy),
                    e.Staged ? 1 : 0,
                    e.Unstaged ? 1 : 0,
                    e.Untracked ? 1 : 0,
                    e.RenamedFrom
                ])
                .ToList();
            columns = ["p", "xy", "stg", "ustg", "unt", "from"];
        }
        else
        {
            rows = response.Compact.Select(line => (IReadOnlyList<object?>)[line]).ToList();
            columns = ["line"];
        }

        return new CompactTupleEnvelope
        {
            V = "2",
            Format = "tuple",
            Schema = "compact.git-status",
            Dict = dict,
            Meta = new Dictionary<string, object?>
            {
                ["project"] = response.Project,
                ["path"] = response.Path,
                ["mode"] = response.Mode,
                ["count"] = response.Count,
                ["isGitRepo"] = response.IsGitRepo
            },
            Columns = columns,
            Items = rows
        };
    }

    public static CompactTupleEnvelope FromFsReadMany(string project, int from, int to, List<CompactFsReadRangeResponse> files)
    {
        var paths = files
            .Select(f => f.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = files
            .Select(f => (IReadOnlyList<object?>)
            [
                IndexOf(paths, f.Path),
                string.Join('\n', f.Lines.Select(l => l.T))
            ])
            .ToList();

        return new CompactTupleEnvelope
        {
            V = "2",
            Format = "tuple",
            Schema = "compact.fs-read-many",
            Dict = new Dictionary<string, List<string>>
            {
                ["p"] = paths
            },
            Meta = new Dictionary<string, object?>
            {
                ["project"] = project,
                ["from"] = from,
                ["to"] = to,
                ["count"] = files.Count
            },
            Columns = ["p", "t"],
            Items = rows
        };
    }

    public static CompactTupleEnvelope FromFsReadSpans(string project, List<CompactFsSpanChunk> chunks)
    {
        var paths = chunks
            .Select(c => c.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = chunks
            .Select(c => (IReadOnlyList<object?>)
            [
                IndexOf(paths, c.Path),
                c.From,
                c.To,
                c.Text
            ])
            .ToList();

        return new CompactTupleEnvelope
        {
            V = "2",
            Format = "tuple",
            Schema = "compact.fs-read-spans",
            Dict = new Dictionary<string, List<string>>
            {
                ["p"] = paths
            },
            Meta = new Dictionary<string, object?>
            {
                ["project"] = project,
                ["count"] = chunks.Count
            },
            Columns = ["p", "from", "to", "t"],
            Items = rows
        };
    }

    public static CompactTupleEnvelope FromRg(CompactRgResponse response)
    {
        var paths = response.Hits
            .Select(h => h.P)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = response.Hits
            .Select(h => (IReadOnlyList<object?>)
            [
                IndexOf(paths, h.P),
                h.L,
                h.C,
                h.T
            ])
            .ToList();

        return new CompactTupleEnvelope
        {
            V = "2",
            Format = "tuple",
            Schema = "compact.rg",
            Dict = new Dictionary<string, List<string>>
            {
                ["p"] = paths
            },
            Meta = new Dictionary<string, object?>
            {
                ["project"] = response.Project,
                ["mode"] = response.Mode,
                ["pattern"] = response.Pattern,
                ["count"] = response.Count
            },
            Columns = ["p", "l", "c", "t"],
            Items = rows
        };
    }

    private static (List<string> Paths, List<string> Kinds) BuildCommonDict(List<CompactItem> items)
    {
        var paths = items.Select(i => i.P).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var kinds = items.Select(i => i.K).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return (paths, kinds);
    }

    private static int IndexOf(List<string> values, string value)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Equals(value, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}

public sealed class CompactTupleEnvelope
{
    public string V { get; set; } = "2";
    public string Format { get; set; } = "tuple";
    public string Schema { get; set; } = "";
    public Dictionary<string, List<string>> Dict { get; set; } = [];
    public Dictionary<string, object?> Meta { get; set; } = [];
    public IReadOnlyList<string> Columns { get; set; } = [];
    public IReadOnlyList<IReadOnlyList<object?>> Items { get; set; } = [];
}
