using Llens.Api;
using Llens.Caching;
using Llens.Indexing;
using Llens.Languages;
using Llens.Languages.CSharp;
using Llens.Languages.Rust;
using Llens.Models;
using Llens.Watching;

var builder = WebApplication.CreateBuilder(args);

var repos = builder.Configuration
    .GetSection("Llens:Repos")
    .Get<List<RepoConfig>>() ?? [];

// Languages + their tools
builder.Services.AddSingleton<ILanguage, CSharpLanguage>();
builder.Services.AddSingleton<ILanguage, RustLanguage>();
builder.Services.AddSingleton<LanguageRegistry>(sp =>
    new LanguageRegistry(sp.GetServices<ILanguage>()));

builder.Services.AddSingleton<IEnumerable<RepoConfig>>(repos);
builder.Services.AddSingleton<ICodeMapCache, SqliteCodeMapCache>();
builder.Services.AddSingleton<ICodeIndexer, CodeIndexer>();
builder.Services.AddHostedService<RepoWatcherService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", repos = repos.Select(r => r.Name) }));
app.MapCodeMapRoutes();

app.Run();
