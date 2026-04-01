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
