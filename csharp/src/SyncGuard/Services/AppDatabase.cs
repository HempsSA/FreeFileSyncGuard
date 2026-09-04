using Microsoft.Data.Sqlite;

namespace SyncGuard.Services;

/// <summary>
/// SQLite database: scan history (last 200 runs/job, replaces per-job
/// history_*.json) and warm file-index cache (replaces cache_*.json).
/// WAL mode for concurrent reader/writer access from scanner threads.
/// </summary>
public sealed class AppDatabase : IDisposable
{
    private readonly string _connectionString;
    private bool _disposed;

    public AppDatabase(string? path = null)
    {
        var dbPath = path ?? AppPaths.DatabaseFile;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        Initialize();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS scan_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'IDLE',
                total INTEGER NOT NULL DEFAULT 0,
                changed INTEGER NOT NULL DEFAULT 0,
                pct REAL NOT NULL DEFAULT 0,
                duration_s REAL NOT NULL DEFAULT 0,
                triggered_by TEXT NOT NULL DEFAULT '',
                anomaly_score REAL NOT NULL DEFAULT 0,
                message TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_history_job_time ON scan_history(job_id, id DESC);
            CREATE TABLE IF NOT EXISTS file_index (
                source_hash TEXT NOT NULL,
                path TEXT NOT NULL,
                mtime REAL NOT NULL,
                size INTEGER NOT NULL,
                PRIMARY KEY (source_hash, path)
            );
            CREATE TABLE IF NOT EXISTS dir_index (
                source_hash TEXT NOT NULL,
                path TEXT NOT NULL,
                mtime REAL NOT NULL,
                PRIMARY KEY (source_hash, path)
            );
            CREATE TABLE IF NOT EXISTS index_meta (
                source_hash TEXT PRIMARY KEY,
                scanned_at REAL NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _disposed = true;
        if (_disposed) SqliteConnection.ClearAllPools();
    }
}
