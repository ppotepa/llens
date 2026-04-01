using System.Reflection;
using System.Text.Json;

namespace Llens.Cli;

public sealed class CliCommandRunner
{
    private readonly CliCommandRegistry _registry;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public CliCommandRunner(CliCommandRegistry registry)
    {
        _registry = registry;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        var commandName = args[0];
        if (!_registry.TryGet(commandName, out var descriptor))
        {
            Console.Error.WriteLine($"Unknown command: {commandName}");
            PrintHelp();
            return 2;
        }

        if (args.Skip(1).Any(IsHelp))
        {
            PrintCommandHelp(descriptor);
            return 0;
        }

        try
        {
            var argMap = ParseArgs(args.Skip(1).ToArray());
            var result = await InvokeAsync(descriptor, argMap, ct);
            Console.WriteLine(JsonSerializer.Serialize(result, _jsonOptions));
            return 0;
        }
        catch (CliBindingException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintCommandHelp(descriptor);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool IsHelp(string arg)
        => arg.Equals("-h", StringComparison.OrdinalIgnoreCase)
           || arg.Equals("--help", StringComparison.OrdinalIgnoreCase)
           || arg.Equals("help", StringComparison.OrdinalIgnoreCase);

    private async Task<object?> InvokeAsync(CliCommandDescriptor descriptor, Dictionary<string, string> argMap, CancellationToken ct)
    {
        var parameters = descriptor.Method.GetParameters();
        if (parameters.Length == 0)
            throw new CliBindingException($"Command '{descriptor.Name}' is misconfigured: request argument is missing.");

        var requestType = parameters[0].ParameterType;
        var request = BindRequest(requestType, argMap);
        object?[] invokeArgs;
        if (parameters.Length == 1)
        {
            invokeArgs = [request];
        }
        else if (parameters.Length == 2 && parameters[1].ParameterType == typeof(CancellationToken))
        {
            invokeArgs = [request, ct];
        }
        else
        {
            throw new CliBindingException($"Command '{descriptor.Name}' signature is not supported.");
        }

        var raw = descriptor.Method.Invoke(descriptor.Target, invokeArgs);
        if (raw is Task t)
        {
            await t.ConfigureAwait(false);
            var resultProperty = t.GetType().GetProperty("Result");
            return resultProperty is null ? null : resultProperty.GetValue(t);
        }

        return raw;
    }

    private static object BindRequest(Type requestType, Dictionary<string, string> args)
    {
        var request = Activator.CreateInstance(requestType)
            ?? throw new CliBindingException($"Could not instantiate request type '{requestType.Name}'.");
        var nullability = new NullabilityInfoContext();
        var props = requestType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanWrite)
            .ToList();

        foreach (var prop in props)
        {
            var argName = ResolveArgName(prop);
            if (!TryGetArgValue(args, argName, out var rawValue))
            {
                if (IsRequired(prop, nullability))
                    throw new CliBindingException($"Missing required argument '--{argName}'.");
                continue;
            }

            var converted = ConvertValue(prop.PropertyType, rawValue);
            prop.SetValue(request, converted);
        }

        return request;
    }

    private static string ResolveArgName(PropertyInfo prop)
    {
        var custom = prop.GetCustomAttribute<ToolArgAttribute>()?.Name;
        if (!string.IsNullOrWhiteSpace(custom)) return custom!;
        return ToKebabCase(prop.Name);
    }

    private static bool TryGetArgValue(Dictionary<string, string> args, string argName, out string value)
    {
        if (args.TryGetValue(Normalize(argName), out value!)) return true;
        value = "";
        return false;
    }

    private static bool IsRequired(PropertyInfo prop, NullabilityInfoContext nullability)
    {
        var type = prop.PropertyType;
        if (Nullable.GetUnderlyingType(type) is not null) return false;
        if (type.IsValueType) return true;

        var info = nullability.Create(prop);
        return info.WriteState == NullabilityState.NotNull;
    }

    private static object? ConvertValue(Type targetType, string raw)
    {
        var actual = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (actual == typeof(string)) return raw;
        if (actual == typeof(int)) return int.Parse(raw);
        if (actual == typeof(long)) return long.Parse(raw);
        if (actual == typeof(bool))
            return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
        if (actual.IsEnum) return Enum.Parse(actual, raw, ignoreCase: true);
        throw new CliBindingException($"Unsupported argument type: {targetType.Name}");
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new CliBindingException($"Unexpected argument '{token}'. Expected '--name value'.");

            var key = Normalize(token[2..]);
            if (string.IsNullOrWhiteSpace(key))
                throw new CliBindingException("Argument name cannot be empty.");

            string value;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }
            else
            {
                value = "true";
            }

            map[key] = value;
        }
        return map;
    }

    private void PrintHelp()
    {
        Console.WriteLine("Usage: dotnet run -- cli <command> [--args]");
        Console.WriteLine("Commands:");
        foreach (var command in _registry.All.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"  {command.Name,-20} {command.Description}");
    }

    private static void PrintCommandHelp(CliCommandDescriptor descriptor)
    {
        Console.WriteLine($"{descriptor.Name}: {descriptor.Description}");
        Console.WriteLine("Arguments:");
        var nullability = new NullabilityInfoContext();
        var requestType = descriptor.Method.GetParameters()[0].ParameterType;
        foreach (var prop in requestType.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.CanWrite))
        {
            var argName = ResolveArgName(prop);
            var required = IsRequired(prop, nullability) ? "required" : "optional";
            var typeLabel = GetTypeLabel(prop.PropertyType);
            var description = prop.GetCustomAttribute<ToolArgAttribute>()?.Description;
            var suffix = string.IsNullOrWhiteSpace(description) ? "" : $" - {description}";
            Console.WriteLine($"  --{argName,-20} {typeLabel,-10} {required}{suffix}");
        }
    }

    private static string GetTypeLabel(Type t)
    {
        var nullable = Nullable.GetUnderlyingType(t);
        if (nullable is not null) return nullable.Name.ToLowerInvariant() + "?";
        return t.Name.ToLowerInvariant();
    }

    private static string Normalize(string name)
        => name.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();

    private static string ToKebabCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var chars = new List<char>(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (i > 0 && char.IsUpper(c)) chars.Add('-');
            chars.Add(char.ToLowerInvariant(c));
        }
        return new string(chars.ToArray());
    }
}

public sealed class CliBindingException : Exception
{
    public CliBindingException(string message) : base(message)
    {
    }
}
