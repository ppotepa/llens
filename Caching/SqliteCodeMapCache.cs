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

            CREATE TABLE IF NOT EXISTS references_ (
                symbol_id   TEXT NOT NULL,
                in_file     TEXT NOT NULL,
                repo_name   TEXT NOT NULL,
                line        INTEGER,
                context     TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_refs_symbol ON references_(symbol_id);

            CREATE TABLE IF NOT EXISTS file_nodes (
                file_path   TEXT PRIMARY KEY,
                repo_name   TEXT NOT NULL,
                language    TEXT NOT NULL,
                imports     TEXT NOT NULL DEFAULT '[]',
                symbol_count INTEGER NOT NULL DEFAULT 0,
                indexed_at  INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_files_repo ON file_nodes(repo_name);

            CREATE TABLE IF NOT EXISTS file_imports (
                from_file   TEXT NOT NULL,
                to_file     TEXT NOT NULL,
                PRIMARY KEY (from_file, to_file)
            );
            CREATE INDEX IF NOT EXISTS idx_imports_to ON file_imports(to_file);
        """);
    }

    // --- Symbols ---

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
        _db.Execute("DELETE FROM references_ WHERE in_file = @filePath", new { filePath });
        _db.Execute("DELETE FROM file_nodes WHERE file_path = @filePath", new { filePath });
        _db.Execute("DELETE FROM file_imports WHERE from_file = @filePath", new { filePath });
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

    public Task<IEnumerable<CodeSymbol>> QueryImplementorsAsync(string symbolName, string? repoName = null, CancellationToken ct = default)
    {
        // Heuristic implementor detection using class/struct/trait-impl symbols and signatures.
        // C# classes/interfaces carry base info in Signature; Rust trait impls are named "Trait for Type".
        var sql = repoName is null
            ? """
              SELECT * FROM symbols
              WHERE kind IN (0, 7, 9)
                AND (name LIKE @pattern OR signature LIKE @pattern OR name LIKE @traitImplPattern)
              """
            : """
              SELECT * FROM symbols
              WHERE kind IN (0, 7, 9)
                AND repo_name = @repoName
                AND (name LIKE @pattern OR signature LIKE @pattern OR name LIKE @traitImplPattern)
              """;
        var needle = symbolName.Trim();
        return Task.FromResult(_db.Query<CodeSymbol>(sql, new
        {
            pattern = $"%{needle}%",
            traitImplPattern = $"{needle} for %",
            repoName
        }));
    }

    // --- References ---

    public Task RemoveReferencesInFileAsync(string filePath, CancellationToken ct = default)
    {
        _db.Execute("DELETE FROM references_ WHERE in_file = @filePath", new { filePath });
        return Task.CompletedTask;
    }

    public Task StoreReferencesAsync(IEnumerable<SymbolReference> references, CancellationToken ct = default)
    {
        using var tx = _db.BeginTransaction();
        foreach (var r in references)
        {
            _db.Execute("""
                INSERT OR IGNORE INTO references_ (symbol_id, in_file, repo_name, line, context)
                VALUES (@SymbolId, @InFilePath, @RepoName, @Line, @Context)
                """, r, tx);
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    public Task<IEnumerable<SymbolReference>> QueryReferencesAsync(string symbolId, string? repoName = null, CancellationToken ct = default)
    {
        var sql = repoName is null
            ? "SELECT symbol_id as SymbolId, in_file as InFilePath, repo_name as RepoName, line as Line, context as Context FROM references_ WHERE symbol_id = @symbolId"
            : "SELECT symbol_id as SymbolId, in_file as InFilePath, repo_name as RepoName, line as Line, context as Context FROM references_ WHERE symbol_id = @symbolId AND repo_name = @repoName";
        return Task.FromResult(_db.Query<SymbolReference>(sql, new { symbolId, repoName }));
    }

    // --- File graph ---

    public Task StoreFileNodeAsync(FileNode file, CancellationToken ct = default)
    {
        using var tx = _db.BeginTransaction();
        _db.Execute("""
            INSERT OR REPLACE INTO file_nodes (file_path, repo_name, language, symbol_count, indexed_at)
            VALUES (@FilePath, @RepoName, @Language, @SymbolCount, @LastIndexedAt)
            """, file, tx);

        _db.Execute("DELETE FROM file_imports WHERE from_file = @FilePath", file, tx);
        foreach (var import in file.Imports)
        {
            _db.Execute("INSERT OR IGNORE INTO file_imports (from_file, to_file) VALUES (@from, @to)",
                new { from = file.FilePath, to = import }, tx);
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    public Task<FileNode?> GetFileNodeAsync(string filePath, CancellationToken ct = default)
    {
        var node = _db.QuerySingleOrDefault<dynamic>(
            "SELECT * FROM file_nodes WHERE file_path = @filePath", new { filePath });
        if (node is null) return Task.FromResult<FileNode?>(null);

        var imports = _db.Query<string>(
            "SELECT to_file FROM file_imports WHERE from_file = @filePath", new { filePath }).ToArray();

        return Task.FromResult<FileNode?>(new FileNode
        {
            FilePath = node.file_path,
            RepoName = node.repo_name,
            Language = node.language,
            SymbolCount = (int)node.symbol_count,
            LastIndexedAt = (long)node.indexed_at,
            Imports = imports
        });
    }

    public async Task<IEnumerable<FileNode>> GetDependentsAsync(string filePath, string? repoName = null, CancellationToken ct = default)
    {
        var dependentPaths = _db.Query<string>(
            "SELECT from_file FROM file_imports WHERE to_file = @filePath", new { filePath });

        var nodes = new List<FileNode>();
        foreach (var path in dependentPaths)
        {
            var node = await GetFileNodeAsync(path, ct);
            if (node is not null && (repoName is null || node.RepoName == repoName))
                nodes.Add(node);
        }
        return nodes;
    }

    public async Task<IEnumerable<FileNode>> GetAllFilesAsync(string repoName, CancellationToken ct = default)
    {
        var paths = _db.Query<string>(
            "SELECT file_path FROM file_nodes WHERE repo_name = @repoName", new { repoName });

        var nodes = new List<FileNode>();
        foreach (var path in paths)
        {
            var node = await GetFileNodeAsync(path, ct);
            if (node is not null) nodes.Add(node);
        }
        return nodes;
    }

    // --- Context extraction ---

    public async Task<string?> GetSourceContextAsync(string filePath, int line, int radiusLines = 20, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return null;

        var lines = await File.ReadAllLinesAsync(filePath, ct);
        var from = Math.Max(0, line - radiusLines - 1);
        var to = Math.Min(lines.Length - 1, line + radiusLines - 1);

        return string.Join('\n', lines[from..(to + 1)]);
    }

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        return ValueTask.CompletedTask;
    }
}
