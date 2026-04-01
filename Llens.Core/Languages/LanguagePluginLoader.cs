using System.Reflection;
using System.Runtime.Loader;

namespace Llens.Languages;

public static class LanguagePluginLoader
{
    public static IReadOnlyList<ILanguage> LoadFromBaseDirectory(string baseDirectory)
    {
        var assemblies = new List<Assembly>();
        foreach (var path in Directory.EnumerateFiles(baseDirectory, "Llens.Language.*.dll"))
        {
            try
            {
                var asm = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => string.Equals(a.Location, path, StringComparison.OrdinalIgnoreCase))
                    ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                assemblies.Add(asm);
            }
            catch
            {
                // Skip malformed/missing plugin assemblies; host can still start with the remaining languages.
            }
        }

        return assemblies
            .SelectMany(SafeGetTypes)
            .Where(t => !t.IsAbstract && typeof(ILanguage).IsAssignableFrom(t))
            .Select(t => Activator.CreateInstance(t))
            .OfType<ILanguage>()
            .GroupBy(l => l.Id)
            .Select(g => g.First())
            .ToList();
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>();
        }
    }
}
