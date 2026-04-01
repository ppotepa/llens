namespace Llens.Cli;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ToolCommandAttribute : Attribute
{
    public ToolCommandAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public string Description { get; init; } = "";
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class ToolArgAttribute : Attribute
{
    public ToolArgAttribute(string? name = null)
    {
        Name = name;
    }

    public string? Name { get; }
    public string Description { get; init; } = "";
}
