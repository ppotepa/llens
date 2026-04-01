using System.Reflection;

namespace Llens.Cli;

public sealed class CliCommandRegistry
{
    private readonly Dictionary<string, CliCommandDescriptor> _commands;

    public CliCommandRegistry(IServiceProvider services)
    {
        _commands = Build(services);
    }

    public IReadOnlyDictionary<string, CliCommandDescriptor> All => _commands;

    public bool TryGet(string name, out CliCommandDescriptor descriptor)
        => _commands.TryGetValue(name, out descriptor!);

    private static Dictionary<string, CliCommandDescriptor> Build(IServiceProvider services)
    {
        var map = new Dictionary<string, CliCommandDescriptor>(StringComparer.OrdinalIgnoreCase);
        var methods = typeof(CompactCliCommands)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<ToolCommandAttribute>()))
            .Where(x => x.Attr is not null)
            .ToList();

        foreach (var entry in methods)
        {
            var attr = entry.Attr!;
            var instance = services.GetRequiredService<CompactCliCommands>();
            var descriptor = new CliCommandDescriptor(attr.Name, attr.Description, instance, entry.Method);
            map[attr.Name] = descriptor;
        }
        return map;
    }
}

public sealed record CliCommandDescriptor(
    string Name,
    string Description,
    object Target,
    MethodInfo Method);
