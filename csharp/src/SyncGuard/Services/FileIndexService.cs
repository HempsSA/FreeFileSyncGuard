using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SyncGuard.Models;

namespace SyncGuard.Services;

/// <summary>
/// SQLite warm cache of file/dir mtimes per source path.
/// Ports ScanCache: unchanged directories are skipped on rescan.
/// </summary>
public sealed class FileIndexService
{
    private readonly AppDatabase _db;
    private readonly string _hash;
    private readonly Dictionary<string, FileRecord> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _scannedAt = DateTime.MinValue;
    private readonly ReaderWriterLockSlim _lock = new();

    public string SourcePath { get; }

    public FileIndexService(AppDatabase db, string sourcePath)
    {
        _db = db;
        SourcePath = sourcePath;
        _hash = ScanHistoryService.SourceHash(sourcePath.ToUpperInvariant());
        Load();
    }

    private void Load()
    {
        try
        {
            using var conn = _db.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT path, mtime, size FROM file_index WHERE source_hash = $h";
                cmd.Parameters.AddWithValue("$h", _hash);
                using var r = cmd.ExecuteReader();
                while (r.Read()) _files[Normalize(r.GetString(0))] = new FileRecord(r.GetDouble(1), r.GetInt64(2));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT path, mtime FROM dir_index WHERE source_hash = $h";
                cmd.Parameters.AddWithValue("$h", _hash);
                using var r = cmd.ExecuteReader();
                while (r.Read()) _dirs[Normalize(r.GetString(0))] = r.GetDouble(1);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT scanned_at FROM index_meta WHERE source_hash = $h";
                cmd.Parameters.AddWithValue("$h", _hash);
                var v = cmd.ExecuteScalar();
                if (v is not null) _scannedAt = DateTime.UnixEpoch.AddSeconds(Convert.ToDouble(v));
            }
        }
        catch (Exception ex) { AppLog.Write($"Index load failed: {ex.Message}", "WARN"); }

        // Legacy cache_*.json import (Python format: {files:{}, dirs:{}, ts}).
        if (_files.Count == 0)
            ImportLegacyCache();
    }

    private void ImportLegacyCache()
    {
        var legacy = Path.Combine(AppPaths.LegacyCacheDir, "cache_" + ScanHistoryService.SourceHash(SourcePath) + ".json");
        if (!File.Exists(legacy)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
            var root = doc.RootElement;
            if (root.TryGetProperty("files", out var files))
                foreach (var p in files.EnumerateObject())
                {
                    double mtime = 0; long size = -1;
                    if (p.Value.ValueKind == JsonValueKind.Array)
                    {
                        mtime = p.Value[0].GetDouble();
                        if (p.Value.GetArrayLength() > 1) size = p.Value[1].GetInt64();
                    }
                    else mtime = p.Value.GetDouble();
                    _files[Normalize(p.Name)] = new FileRecord(mtime, size);
                }
            if (root.TryGetProperty("dirs", out var dirs))
                foreach (var p in dirs.EnumerateObject())
                    _dirs[Normalize(p.Name)] = p.Value.GetDouble();
            if (_files.Count > 0) AppLog.Write($"Imported legacy file index ({_files.Count} files)");
        }
        catch (Exception ex) { AppLog.Write($"Legacy index import failed: {ex.Message}", "WARN"); }
    }

    private static string Normalize(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant(); }
        catch { return p.ToUpperInvariant(); }
    }

    public string NormalizePath(string p) => Normalize(p);

    public int KnownTotal
    {
        get { _lock.EnterReadLock(); try { return _files.Count; } finally { _lock.ExitReadLock(); } }
    }

    public string AgeString
    {
        get
        {
            if (_scannedAt == DateTime.MinValue) return "never";
            var secs = (DateTime.UtcNow - _scannedAt.ToUniversalTime()).TotalSeconds;
            if (secs < 120) return $"{(int)secs}s ago";
            if (secs < 3600) return $"{(int)(secs / 60)}m ago";
            return $"{(int)(secs / 3600)}h ago";
        }
    }

    public bool DirUnchanged(string dirPath, double mtime)
    {
        _lock.EnterReadLock();
        try { return _dirs.TryGetValue(Normalize(dirPath), out var c) && Math.Abs(c - mtime) < 1.0; }
        finally { _lock.ExitReadLock(); }
    }

    public FileRecord? FileRecord(string path)
    {
        _lock.EnterReadLock();
        try { return _files.TryGetValue(Normalize(path), out var v) ? v : null; }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyDictionary<string, FileRecord> SnapshotFiles()
    {
        _lock.EnterReadLock();
        try { return new Dictionary<string, FileRecord>(_files, StringComparer.OrdinalIgnoreCase); }
        finally { _lock.ExitReadLock(); }
    }

    public Dictionary<string, FileRecord> FilesInDirectory(string dirPath)
    {
        var norm = Normalize(dirPath);
        var result = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        _lock.EnterReadLock();
        try
        {
            foreach (var kv in _files)
            {
                // NOTE: dirname must be re-normalized — "C:\F" has parent
                // "C:\", which normalizes to "C:" (trailing sep trimmed).
                var parent = Path.GetDirectoryName(kv.Key);
                if (parent is not null && Normalize(parent) == norm)
                    result[kv.Key] = kv.Value;
            }
        }
        finally { _lock.ExitReadLock(); }
        return result;
    }

    public void Save(Dictionary<string, FileRecord> files, Dictionary<string, double> dirs)
    {
        var normFiles = files.ToDictionary(kv => Normalize(kv.Key), kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var normDirs = dirs.ToDictionary(kv => Normalize(kv.Key), kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        _lock.EnterWriteLock();
        try
        {
            _files.Clear(); _dirs.Clear();
            foreach (var kv in normFiles) _files[kv.Key] = kv.Value;
            foreach (var kv in normDirs) _dirs[kv.Key] = kv.Value;
            _scannedAt = DateTime.UtcNow;
        }
        finally { _lock.ExitWriteLock(); }

        try
        {
            using var conn = _db.Open();
            using var tx = conn.BeginTransaction();
            foreach (var sql in new[] {
                "DELETE FROM file_index WHERE source_hash = $h",
                "DELETE FROM dir_index WHERE source_hash = $h" })
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = sql;
                del.Parameters.AddWithValue("$h", _hash);
                del.ExecuteNonQuery();
            }
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO file_index (source_hash, path, mtime, size) VALUES ($h,$p,$m,$s)";
                var ph = ins.CreateParameter(); ph.ParameterName = "$h"; ph.Value = _hash; ins.Parameters.Add(ph);
                var pp = ins.CreateParameter(); pp.ParameterName = "$p"; ins.Parameters.Add(pp);
                var pm = ins.CreateParameter(); pm.ParameterName = "$m"; ins.Parameters.Add(pm);
                var ps = ins.CreateParameter(); ps.ParameterName = "$s"; ins.Parameters.Add(ps);
                foreach (var kv in normFiles)
                {
                    pp.Value = kv.Key; pm.Value = kv.Value.Mtime; ps.Value = kv.Value.Size;
                    ins.ExecuteNonQuery();
                }
            }
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO dir_index (source_hash, path, mtime) VALUES ($h,$p,$m)";
                var ph = ins.CreateParameter(); ph.ParameterName = "$h"; ph.Value = _hash; ins.Parameters.Add(ph);
                var pp = ins.CreateParameter(); pp.ParameterName = "$p"; ins.Parameters.Add(pp);
                var pm = ins.CreateParameter(); pm.ParameterName = "$m"; ins.Parameters.Add(pm);
                foreach (var kv in normDirs)
                {
                    pp.Value = kv.Key; pm.Value = kv.Value;
                    ins.ExecuteNonQuery();
                }
            }
            using (var meta = conn.CreateCommand())
            {
                meta.Transaction = tx;
                meta.CommandText = "INSERT INTO index_meta (source_hash, scanned_at) VALUES ($h,$t) ON CONFLICT(source_hash) DO UPDATE SET scanned_at=$t";
                meta.Parameters.AddWithValue("$h", _hash);
                meta.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                meta.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Could not save file index: {ex.Message}", "ERROR");
            throw;
        }
    }

    public void Clear()
    {
        _lock.EnterWriteLock();
        try { _files.Clear(); _dirs.Clear(); _scannedAt = DateTime.MinValue; }
        finally { _lock.ExitWriteLock(); }
        try
        {
            using var conn = _db.Open();
            foreach (var table in new[] { "file_index", "dir_index", "index_meta" })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {table} WHERE source_hash = $h";
                cmd.Parameters.AddWithValue("$h", _hash);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex) { AppLog.Write($"Could not clear file index: {ex.Message}", "WARN"); }
    }
}
