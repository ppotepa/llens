using Dapper;
using Llens.Models;
using Microsoft.Data.Sqlite;

namespace Llens.Caching;

public class SqliteCodeMapCache : ICodeMapCache, IAsyncDisposable
{
    private readonly SqliteConnection _db;

    public SqliteCodeMapCache(IConfiguration config)
    {
        var path = config["Llens:DbPath"] ?? "llens.db";
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        _db.Execute("""
            CREATE TABLE IF NOT EXISTS symbols (
                id          TEXT PRIMARY KEY,
                repo_name   TEXT NOT NULL,
                file_path   TEXT NOT NULL,
                name        TEXT NOT NULL,
                kind        INTEGER NOT NULL,
                line_start  INTEGER,
                line_end    INTEGER,
                signature   TEXT,
                doc_comment TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
            CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_path);
            CREATE INDEX IF NOT EXISTS idx_symbols_repo ON symbols(repo_name);
        """);
    }

    public Task StoreSymbolsAsync(string filePath, IEnumerable<CodeSymbol> symbols, CancellationToken ct = default)
    {
        using var tx = _db.BeginTransaction();
        _db.Execute("DELETE FROM symbols WHERE file_path = @filePath", new { filePath }, tx);

        foreach (var s in symbols)
        {
            _db.Execute("""
                INSERT OR REPLACE INTO symbols (id, repo_name, file_path, name, kind, line_start, line_end, signature, doc_comment)
                VALUES (@Id, @RepoName, @FilePath, @Name, @Kind, @LineStart, @LineEnd, @Signature, @DocComment)
                """, s, tx);
        }

        tx.Commit();
        return Task.CompletedTask;
    }

    public Task RemoveFileAsync(string filePath, CancellationToken ct = default)
    {
        _db.Execute("DELETE FROM symbols WHERE file_path = @filePath", new { filePath });
        return Task.CompletedTask;
    }

    public Task<IEnumerable<CodeSymbol>> QueryByNameAsync(string name, string? repoName = null, CancellationToken ct = default)
    {
        var sql = repoName is null
            ? "SELECT * FROM symbols WHERE name LIKE @pattern"
            : "SELECT * FROM symbols WHERE name LIKE @pattern AND repo_name = @repoName";

        return Task.FromResult(_db.Query<CodeSymbol>(sql, new { pattern = $"%{name}%", repoName }));
    }

    public Task<IEnumerable<CodeSymbol>> QueryByFileAsync(string filePath, CancellationToken ct = default)
        => Task.FromResult(_db.Query<CodeSymbol>("SELECT * FROM symbols WHERE file_path = @filePath", new { filePath }));

    public Task<IEnumerable<CodeSymbol>> QueryByKindAsync(SymbolKind kind, string? repoName = null, CancellationToken ct = default)
    {
        var sql = repoName is null
            ? "SELECT * FROM symbols WHERE kind = @kind"
            : "SELECT * FROM symbols WHERE kind = @kind AND repo_name = @repoName";

        return Task.FromResult(_db.Query<CodeSymbol>(sql, new { kind = (int)kind, repoName }));
    }

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        return ValueTask.CompletedTask;
    }
}
