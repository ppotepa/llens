using Llens.Api;
using Llens.Application.Fs;
using Llens.Application.JsCheck;
using Llens.Caching;
using Llens.Cli;
using Llens.Indexing;
using Llens.Languages;
using Llens.Models;
using Llens.Observability;
using Llens.Scanning;
using Llens.Watching;

var builder = WebApplication.CreateBuilder(args);

foreach (var language in LanguagePluginLoader.LoadFromBaseDirectory(AppContext.BaseDirectory))
    builder.Services.AddSingleton<ILanguage>(language);
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
builder.Services.AddSingleton<QueryTelemetry>();
builder.Services.AddSingleton<CompactSessionStore>();
builder.Services.AddSingleton<CompactDevServerStore>();
builder.Services.AddSingleton<IJsCheckService, JsCheckService>();
builder.Services.AddSingleton<ICompactFsService, CompactFsService>();
builder.Services.AddSingleton<CompactCliCommands>();
builder.Services.AddSingleton<CliCommandRegistry>();
builder.Services.AddSingleton<CliCommandRunner>();
builder.Services.AddHostedService<RepoWatcherService>();

var app = builder.Build();

if (args.Length > 0 && args[0].Equals("cli", StringComparison.OrdinalIgnoreCase))
{
    var runner = app.Services.GetRequiredService<CliCommandRunner>();
    var exitCode = await runner.RunAsync(args.Skip(1).ToArray(), CancellationToken.None);
    Environment.ExitCode = exitCode;
    return;
}

app.UseStaticFiles();
app.MapGet("/browse", () => Results.Redirect("/browse.html"));

app.MapGet("/health", (ProjectRegistry projects) => Results.Ok(new
{
    status = "ok",
    projects = projects.All.Select(p => new { p.Name, languages = p.Languages.All.Select(l => l.Name) })
}));

app.MapProjectRoutes();
app.MapSymbolRoutes();
app.MapFileRoutes();
app.MapQueryRoutes();
app.MapGraphRoutes();
app.MapContextPackRoutes();
app.MapCompactRoutes();
app.MapCompactOpsRoutes();
app.MapTelemetryRoutes();

app.Run();
