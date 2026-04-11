using System.Text.Json;

namespace Llens.Bench.TaskPacks;

public static class TaskPackLoader
{
    public static TaskPack Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Task pack path is required.");

        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"Task pack not found: {full}");

        var json = File.ReadAllText(full);
        var pack = JsonSerializer.Deserialize<TaskPack>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (pack is null)
            throw new InvalidOperationException($"Failed to parse task pack: {full}");
        if (pack.Tasks.Count == 0)
            throw new InvalidOperationException($"Task pack has no tasks: {full}");

        return pack;
    }
}
