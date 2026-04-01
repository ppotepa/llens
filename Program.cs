using Llens.Api;
using Llens.Caching;
using Llens.Indexing;
using Llens.Languages;
using Llens.Languages.CSharp;
using Llens.Languages.Rust;
using Llens.Models;
using Llens.Watching;

var builder = WebApplication.CreateBuilder(args);

// All known languages — add new ones here
builder.Services.AddSingleton<ILanguage, CSharpLanguage>();
builder.Services.AddSingleton<ILanguage, RustLanguage>();
builder.Services.AddSingleton<LanguageCatalogue>();

// Build a per-project LanguageRegistry from config, then wrap in ProjectRegistry
builder.Services.AddSingleton<ProjectRegistry>(sp =>
{
    var catalogue = sp.GetRequiredService<LanguageCatalogue>();
    var repoConfigs = builder.Configuration
        .GetSection("Llens:Repos")
        .Get<List<RepoConfig>>() ?? [];

    var projects = repoConfigs.Select(repo =>
        new Project(repo, catalogue.BuildRegistry(repo.Languages)));

    return new ProjectRegistry(projects);
});

builder.Services.AddSingleton<ICodeMapCache, SqliteCodeMapCache>();
builder.Services.AddSingleton<ICodeIndexer, CodeIndexer>();
builder.Services.AddHostedService<RepoWatcherService>();

var app = builder.Build();

app.MapGet("/health", (ProjectRegistry projects, LanguageCatalogue catalogue) => Results.Ok(new
{
    status = "ok",
    knownLanguages = catalogue.KnownLanguages,
    projects = projects.All.Select(p => new
    {
        p.Name,
        p.Config.Path,
        languages = p.Languages.All.Select(l => l.Name)
    })
}));

app.MapCodeMapRoutes();

app.Run();
