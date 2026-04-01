using Llens.Api;
using Llens.Caching;
using Llens.Indexing;
using Llens.Languages;
using Llens.Languages.CSharp;
using Llens.Languages.Rust;
using Llens.Models;
using Llens.Scanning;
using Llens.Watching;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ILanguage, CSharpLanguage>();
builder.Services.AddSingleton<ILanguage, RustLanguage>();
builder.Services.AddSingleton<LanguageCatalogue>();

builder.Services.AddSingleton<ProjectRegistry>(sp =>
{
    var catalogue = sp.GetRequiredService<LanguageCatalogue>();
    var repos = builder.Configuration
        .GetSection("Llens:Repos")
        .Get<List<RepoConfig>>() ?? [];

    var projects = repos.Select(repo =>
        new Project(repo, catalogue.BuildRegistry(repo.Languages)));

    return new ProjectRegistry(projects);
});

builder.Services.AddSingleton<IFileScanner, GitAwareFileScanner>();
builder.Services.AddSingleton<ICodeMapCache, InMemoryCodeMapCache>(); // swap to SqliteCodeMapCache when persistence is needed
builder.Services.AddSingleton<ICodeIndexer, CodeIndexer>();
builder.Services.AddHostedService<RepoWatcherService>();

var app = builder.Build();

app.MapGet("/health", (ProjectRegistry projects) => Results.Ok(new
{
    status = "ok",
    projects = projects.All.Select(p => new { p.Name, languages = p.Languages.All.Select(l => l.Name) })
}));

app.MapProjectRoutes();
app.MapSymbolRoutes();
app.MapFileRoutes();

app.Run();
