namespace Llens.Models;

public class ProjectRegistry(IEnumerable<Project> projects)
{
    private readonly IReadOnlyDictionary<string, Project> _projects =
        projects.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public Project? Resolve(string projectName)
        => _projects.GetValueOrDefault(projectName);

    public IReadOnlyCollection<Project> All => (IReadOnlyCollection<Project>)_projects.Values;
}
