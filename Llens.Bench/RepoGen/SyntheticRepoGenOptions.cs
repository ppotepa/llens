namespace Llens.Bench.RepoGen;

public sealed record SyntheticRepoGenOptions(
    string OutputPath,
    int CommitCount = 100,
    int FileTarget = 100,
    int Seed = 42,
    bool Force = false);
