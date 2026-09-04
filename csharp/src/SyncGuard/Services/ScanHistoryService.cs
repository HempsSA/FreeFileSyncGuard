using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SyncGuard.Models;

namespace SyncGuard.Services;

/// <summary>
/// SQLite-backed scan history (keeps last 200 runs per job).
/// Ports ScanHistory; imports legacy history_*.json files once.
/// </summary>
public sealed class ScanHistoryService
{
    private const int HistoryMax = 200;
    private readonly AppDatabase _db;

    public ScanHistoryService(AppDatabase db) => _db = db;

    public static string SourceHash(string key)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    public List<ScanRecord> Get(string jobId, int limit = HistoryMax)
    {
        var list = new List<ScanRecord>();
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, job_id, timestamp, status, total, changed, pct, duration_s, triggered_by, anomaly_score, message FROM scan_history WHERE job_id = $job ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$job", jobId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ScanRecord
            {
                Id = r.GetInt64(0),
                JobId = r.GetString(1),
                Timestamp = DateTime.Parse(r.GetString(2)),
                Status = r.GetString(3),
                Total = r.GetInt64(4),
                Changed = r.GetInt64(5),
                Percent = r.GetDouble(6),
                DurationSeconds = r.GetDouble(7),
                TriggeredBy = r.GetString(8),
                AnomalyScore = r.GetDouble(9),
                Message = r.GetString(10),
            });
        }
        return list;
    }

    public void Add(ScanRecord record)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO scan_history (job_id, timestamp, status, total, changed, pct, duration_s, triggered_by, anomaly_score, message) VALUES ($job,$ts,$st,$tot,$chg,$pct,$dur,$trig,$ano,$msg)";
        cmd.Parameters.AddWithValue("$job", record.JobId);
        cmd.Parameters.AddWithValue("$ts", record.Timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("$st", record.Status);
        cmd.Parameters.AddWithValue("$tot", record.Total);
        cmd.Parameters.AddWithValue("$chg", record.Changed);
        cmd.Parameters.AddWithValue("$pct", record.Percent);
        cmd.Parameters.AddWithValue("$dur", record.DurationSeconds);
        cmd.Parameters.AddWithValue("$trig", record.TriggeredBy);
        cmd.Parameters.AddWithValue("$ano", record.AnomalyScore);
        cmd.Parameters.AddWithValue("$msg", record.Message);
        cmd.ExecuteNonQuery();
        using var prune = conn.CreateCommand();
        prune.CommandText = "DELETE FROM scan_history WHERE job_id = $job AND id NOT IN (SELECT id FROM scan_history WHERE job_id = $job ORDER BY id DESC LIMIT $keep)";
        prune.Parameters.AddWithValue("$job", record.JobId);
        prune.Parameters.AddWithValue("$keep", HistoryMax);
        prune.ExecuteNonQuery();
    }

    public void Clear(string jobId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scan_history WHERE job_id = $job";
        cmd.Parameters.AddWithValue("$job", jobId);
        cmd.ExecuteNonQuery();
    }

    public string ExportCsv(string jobId)
    {
        var sb = new StringBuilder("timestamp,status,total,changed,pct,duration_s,triggered_by,anomaly_score,message\n");
        foreach (var r in Get(jobId, int.MaxValue))
        {
            string Cell(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            sb.Append($"{r.Timestamp:o},{r.Status},{r.Total},{r.Changed},{r.Percent},{r.DurationSeconds},{Cell(r.TriggeredBy)},{r.AnomalyScore},{Cell(r.Message)}\n");
        }
        return sb.ToString();
    }

    /// <summary>One-time import of Python-era history_*.json files.</summary>
    public void ImportLegacy(string jobId, string jobKey)
    {
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM scan_history WHERE job_id = $job";
            cmd.Parameters.AddWithValue("$job", jobId);
            if ((long)cmd.ExecuteScalar()! > 0) return;
        }
        var legacy = Path.Combine(AppPaths.LegacyCacheDir, "history_" + SourceHash(jobKey) + ".json");
        if (!File.Exists(legacy)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                try
                {
                    Add(new ScanRecord
                    {
                        JobId = jobId,
                        Timestamp = el.TryGetProperty("timestamp", out var ts) && DateTime.TryParse(ts.GetString(), out var dt) ? dt : DateTime.Now,
                        Status = el.TryGetProperty("status", out var st) ? st.GetString() ?? "IDLE" : "IDLE",
                        Total = el.TryGetProperty("total", out var t) ? t.GetInt64() : 0,
                        Changed = el.TryGetProperty("changed", out var c) ? c.GetInt64() : 0,
                        Percent = el.TryGetProperty("pct", out var p) ? p.GetDouble() : 0,
                        TriggeredBy = el.TryGetProperty("triggered_by", out var tb) ? tb.GetString() ?? "" : "",
                        Message = el.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "",
                    });
                }
                catch { /* skip bad rows */ }
            }
            AppLog.Write($"Imported legacy scan history for job {jobId}");
        }
        catch (Exception ex) { AppLog.Write($"Legacy history import failed: {ex.Message}", "WARN"); }
    }
}
