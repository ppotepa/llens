namespace Llens.Bench.TaskPacks;

public sealed class TaskPack
{
    public string Name { get; set; } = "unnamed-pack";
    public string? Repo { get; set; }
    public List<HistoryTask> Tasks { get; set; } = [];
}

public sealed class HistoryTask
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Note { get; set; }
}
