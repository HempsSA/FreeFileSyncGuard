using System.Net.Http;
using System.Net.Http.Json;
using SyncGuard.Models;

namespace SyncGuard.Services;

/// <summary>
/// ntfy.sh push notifications. Ports notifications.py (HttpClient
/// replaces urllib). Single shared HttpClient for the process.
/// </summary>
public sealed class NotificationService
{
    public const string DefaultServer = "https://ntfy.sh";
    public const string DefaultTopic = "syncguard-alerts";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<bool> SendAsync(
        string message, string topic, string title = "SyncGuard",
        int priority = 3, string[]? tags = null,
        string server = DefaultServer, string accessToken = "",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic)) return false;
        var url = server.TrimEnd('/') + "/" + topic;
        var payload = new Dictionary<string, object>
        {
            ["message"] = message,
            ["title"] = title,
            ["priority"] = priority,
        };
        if (tags is { Length: > 0 }) payload["tags"] = tags;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload),
            };
            if (!string.IsNullOrWhiteSpace(accessToken))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public static Task<bool> SendJobAsync(
        string jobName, string status,
        long total = 0, long changed = 0, double pct = 0,
        double durationS = 0, string triggeredBy = "",
        NotificationSettings? settings = null,
        CancellationToken ct = default)
    {
        settings ??= new NotificationSettings();
        if (!settings.Enabled) return Task.FromResult(false);
        if (status == "OK" && !settings.NotifyOk) return Task.FromResult(false);
        if (status == "WARN" && !settings.NotifyWarn) return Task.FromResult(false);
        if ((status == "ERROR" || status == "ABORTED") && !settings.NotifyError) return Task.FromResult(false);

        var tags = new Dictionary<string, string>
        {
            ["OK"] = "white_check_mark", ["WARN"] = "warning",
            ["ERROR"] = "x", ["ABORTED"] = "no_entry",
        };
        var priorities = new Dictionary<string, int>
        {
            ["OK"] = 1, ["WARN"] = 3, ["ERROR"] = 5, ["ABORTED"] = 5,
        };
        var parts = new List<string> { $"Job '{jobName}' finished with status: {status}" };
        if (total > 0) parts.Add($"Files: {total:N0} scanned, {changed:N0} changed ({pct}%)");
        if (durationS > 0)
        {
            int m = (int)(durationS / 60), s = (int)(durationS % 60);
            parts.Add(m > 0 ? $"Duration: {m}m {s}s" : $"Duration: {s}s");
        }
        if (!string.IsNullOrWhiteSpace(triggeredBy)) parts.Add($"Triggered by: {triggeredBy}");

        return SendAsync(string.Join("\n", parts), settings.Topic,
            $"SyncGuard — {jobName}",
            priorities.TryGetValue(status, out var p) ? p : 3,
            new[] { tags.TryGetValue(status, out var t) ? t : "bell" },
            settings.Server, settings.AccessToken, ct);
    }
}

/// <summary>
/// Daily HH:MM scheduler. Ports the schedule-based logic in app.py:
/// a 20s timer fires each enabled job once per matching minute.
/// </summary>
public sealed class SchedulerService : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly Func<IReadOnlyList<JobConfig>> _jobs;
    private readonly Func<string, string, Task> _runJob;
    private readonly HashSet<string> _fired = new();
    private bool _disposed;

    public SchedulerService(Func<IReadOnlyList<JobConfig>> jobs, Func<string, string, Task> runJob)
    {
        _jobs = jobs;
        _runJob = runJob;
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
    }

    private void Tick()
    {
        try
        {
            var now = DateTime.Now;
            string today = now.ToString("yyyy-MM-dd");
            // Forget yesterday's markers.
            foreach (var k in _fired.Where(k => !k.EndsWith(today)).ToList())
                _fired.Remove(k);
            foreach (var job in _jobs())
            {
                if (!job.Enabled) continue;
                foreach (var t in job.ScheduleTimes)
                {
                    if (!TryParseHm(t, out int h, out int m)) continue;
                    string key = $"{job.JobId}@{t}#{today}";
                    if (now.Hour == h && now.Minute == m && _fired.Add(key))
                    {
                        var captured = job;
                        _ = Task.Run(async () =>
                        {
                            try { await _runJob(captured.JobId, "schedule"); }
                            catch (Exception ex) { AppLog.Write($"Scheduled run failed: {ex.Message}", "ERROR"); }
                        });
                    }
                }
            }
        }
        catch (Exception ex) { AppLog.Write($"Scheduler tick failed: {ex.Message}", "ERROR"); }
    }

    public static bool TryParseHm(string s, out int h, out int m)
    {
        h = m = 0;
        var parts = s.Trim().Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out h) || !int.TryParse(parts[1], out m)) return false;
        return h is >= 0 and < 24 && m is >= 0 and < 60;
    }

    /// <summary>Next scheduled run across all jobs (for the countdown label).</summary>
    public static DateTime? NextRun(JobConfig job, DateTime from)
    {
        DateTime? best = null;
        foreach (var t in job.ScheduleTimes)
        {
            if (!TryParseHm(t, out int h, out int m)) continue;
            var candidate = new DateTime(from.Year, from.Month, from.Day, h, m, 0);
            if (candidate <= from) candidate = candidate.AddDays(1);
            if (best is null || candidate < best) best = candidate;
        }
        return best;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
