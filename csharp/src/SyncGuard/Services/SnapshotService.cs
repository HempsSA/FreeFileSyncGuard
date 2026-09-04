using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SyncGuard.Models;

namespace SyncGuard.Services;

/// <summary>
/// Destination manifest capture, persistence (last N per job) and
/// pre/post-sync comparison. Ports snapshot.py + rollback.py analysis.
/// </summary>
public sealed class SnapshotService
{
    private readonly int _maxSnapshots;

    public SnapshotService(int maxSnapshots = 3) => _maxSnapshots = maxSnapshots;

    public static string SnapshotDir(string jobId)
    {
        var d = Path.Combine(AppPaths.SnapshotsDir, jobId);
        Directory.CreateDirectory(d);
        return d;
    }

    private static string HashFirst1K(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[1024];
            int read = fs.Read(buf, 0, buf.Length);
            if (read == 0) return string.Empty;
            return Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, read)))[..16].ToLowerInvariant();
        }
        catch { return string.Empty; }
    }

    public SnapshotManifest? Capture(string destPath, string jobId,
        Action<string, string>? log = null, int sampleCount = 0)
    {
        if (string.IsNullOrWhiteSpace(destPath) || !Directory.Exists(destPath))
        {
            log?.Invoke($"Snapshot: destination not accessible: {destPath}", "ERROR");
            return null;
        }
        var manifest = new SnapshotManifest
        {
            JobId = jobId,
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss"),
            Epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DestPath = destPath,
        };
        var toHash = new List<string>();
        try
        {
            // Resilient walk: an unreadable subtree must not void the
            // whole snapshot (it did previously via EnumerateFiles).
            foreach (var fp in EnumerateFilesSafe(destPath))
            {
                try
                {
                    var st = new FileInfo(fp);
                    var rel = Path.GetRelativePath(destPath, fp);
                    manifest.Files[rel] = new SnapshotEntry { Size = st.Length, Mtime = ToUnix(st.LastWriteTimeUtc), Hash = string.Empty };
                    toHash.Add(rel);
                    manifest.TotalSize += st.Length;
                }
                catch { /* skip unreadable */ }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Snapshot: walk failed: {ex.Message}", "ERROR");
            return null;
        }
        manifest.TotalFiles = manifest.Files.Count;
        var hashing = (sampleCount > 0 && sampleCount < toHash.Count)
            ? toHash.OrderBy(_ => Random.Shared.Next()).Take(sampleCount).ToList()
            : toHash;
        foreach (var rel in hashing)
            manifest.Files[rel].Hash = HashFirst1K(Path.Combine(destPath, rel));
        log?.Invoke($"Snapshot: captured {manifest.TotalFiles} files ({FormatSize(manifest.TotalSize)}) from {destPath}", "INFO");
        return manifest;
    }

    public string Save(SnapshotManifest manifest, int? keep = null)
    {
        var dir = SnapshotDir(manifest.JobId);
        // Avoid filename collision within the same second.
        var file = Path.Combine(dir, manifest.Timestamp + ".json");
        for (int i = 1; File.Exists(file); i++)
            file = Path.Combine(dir, $"{manifest.Timestamp}-{i}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Prune(manifest.JobId, keep ?? _maxSnapshots);
        return file;
    }

    public static List<string> List(string jobId)
    {
        var dir = Path.Combine(AppPaths.SnapshotsDir, jobId);
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.GetFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).Order().ToList()!;
    }

    public static SnapshotManifest? Load(string jobId, string timestamp)
    {
        var file = Path.Combine(AppPaths.SnapshotsDir, jobId, timestamp + ".json");
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(file));
        }
        catch (Exception ex)
        {
            AppLog.Write($"Failed to load snapshot: {ex.Message}", "ERROR");
            return null;
        }
    }

    private static void Prune(string jobId, int keep)
    {
        var stamps = List(jobId);
        if (stamps.Count <= keep) return;
        var dir = Path.Combine(AppPaths.SnapshotsDir, jobId);
        foreach (var stamp in stamps.Take(stamps.Count - keep))
            try { File.Delete(Path.Combine(dir, stamp + ".json")); } catch { }
    }

    public static SnapshotDiff Compare(SnapshotManifest before, SnapshotManifest after, bool hashCheck = true)
    {
        var diff = new SnapshotDiff();
        var b = new HashSet<string>(before.Files.Keys);
        var a = new HashSet<string>(after.Files.Keys);
        diff.Added = a.Except(b).Count();
        diff.Removed = b.Except(a).Count();
        foreach (var rel in b.Intersect(a))
        {
            var be = before.Files[rel];
            var ae = after.Files[rel];
            bool modified = be.Mtime != ae.Mtime || be.Size != ae.Size;
            if (be.Size != ae.Size && ae.Size < be.Size * 0.5)
                diff.SizeAnomaly.Add(rel);
            if (modified)
            {
                diff.Modified++;
                if (hashCheck && !string.IsNullOrEmpty(be.Hash) && !string.IsNullOrEmpty(ae.Hash) && be.Hash != ae.Hash)
                    diff.HashMismatch.Add(rel);
            }
            else diff.Unchanged++;
        }
        diff.IsClean = diff.Removed == 0 && diff.HashMismatch.Count == 0 && diff.SizeAnomaly.Count == 0;
        var parts = new List<string>();
        if (diff.Added > 0) parts.Add($"{diff.Added} added");
        if (diff.Removed > 0) parts.Add($"{diff.Removed} removed");
        if (diff.Modified > 0) parts.Add($"{diff.Modified} modified");
        if (diff.HashMismatch.Count > 0) parts.Add($"{diff.HashMismatch.Count} hash mismatches");
        if (diff.SizeAnomaly.Count > 0) parts.Add($"{diff.SizeAnomaly.Count} size anomalies");
        diff.Summary = parts.Count > 0 ? string.Join(", ", parts) : "no changes";
        return diff;
    }

    /// <summary>
    /// Rollback analysis: which snapshot files are missing / truncated in
    /// the current destination. Returns (missing, sizeAnomalies).
    /// </summary>
    public static (int Missing, int Anomalies) AnalyzeRollback(
        SnapshotManifest snapshot, string destPath, Action<string, string>? log = null)
    {
        int missing = 0, anomalies = 0;
        if (!Directory.Exists(destPath))
        {
            log?.Invoke($"Rollback: destination not found: {destPath}", "ERROR");
            return (0, 0);
        }
        var current = new HashSet<string>(
            EnumerateFilesSafe(destPath)
                .Select(fp => Path.GetRelativePath(destPath, fp)));
        foreach (var rel in snapshot.Files.Keys.Except(current))
        {
            log?.Invoke($"Rollback: MISSING file: {rel}", "WARN");
            missing++;
        }
        foreach (var rel in snapshot.Files.Keys.Intersect(current))
        {
            try
            {
                var size = new FileInfo(Path.Combine(destPath, rel)).Length;
                if (size < snapshot.Files[rel].Size * 0.5)
                {
                    log?.Invoke($"Rollback: SIZE ANOMALY: {rel} (now {size}B, was {snapshot.Files[rel].Size}B)", "WARN");
                    anomalies++;
                }
            }
            catch { }
        }
        log?.Invoke($"Rollback analysis: {missing} missing, {anomalies} size anomalies", "INFO");
        return (missing, anomalies);
    }

    private static double ToUnix(DateTime dt) =>
        new DateTimeOffset(dt).ToUnixTimeSeconds();

    /// <summary>Manual-stack walk that skips denied subtrees.</summary>
    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subdirs = Array.Empty<string>();
            string[] files = Array.Empty<string>();
            try { subdirs = Directory.GetDirectories(dir); } catch { continue; }
            try { files = Directory.GetFiles(dir); } catch { /* descend anyway */ }
            foreach (var f in files) yield return f;
            foreach (var s in subdirs) stack.Push(s);
        }
    }

    public static string FormatSize(long n)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = n;
        foreach (var u in units)
        {
            if (v < 1024) return $"{v:F1} {u}";
            v /= 1024;
        }
        return $"{v:F1} PB";
    }
}
