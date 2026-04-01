using Llens.Languages;

namespace Llens.Models;

public class Project(RepoConfig config, LanguageRegistry languages)
{
    public RepoConfig Config => config;
    public LanguageRegistry Languages => languages;
    public string Name => config.Name;
}
