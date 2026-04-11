using Llens.Languages;

namespace Llens.Models;

public class Project(RepoConfig config, ILanguageRegistry languages)
{
    public RepoConfig Config => config;
    public ILanguageRegistry Languages => languages;
    public string Name => config.Name;
}
