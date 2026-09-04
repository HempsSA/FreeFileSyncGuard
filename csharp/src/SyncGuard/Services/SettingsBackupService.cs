using System.Text.Json.Serialization;
using SyncGuard.Models;

namespace SyncGuard.Services;

/// <summary>
/// Versioned settings backup envelope: all jobs + notification settings.
/// Also accepts a bare job array (e.g. a raw syncguard_jobs.json) on import.
/// </summary>
public sealed class SettingsBackup
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = AppPaths.AppVersion;

    [JsonPropertyName("exportedAt")]
    public DateTime ExportedAt { get; set; } = DateTime.Now;

    [JsonPropertyName("jobs")]
    public List<JobConfig> Jobs { get; set; } = new();

    [JsonPropertyName("notification")]
    public NotificationSettings? Notification { get; set; }
}

/// <summary>
/// Export/import of settings backups. Pure file logic (no UI) so it is unit-testable.
/// </summary>
public static class SettingsBackupService
{
    public static async Task ExportAsync(string file, IReadOnlyList<JobConfig> jobs, NotificationSettings notification)
    {
        var backup = new SettingsBackup
        {
            ExportedAt = DateTime.Now,
            Jobs = jobs.ToList(),
            Notification = notification,
        };
        await File.WriteAllTextAsync(file,
            System.Text.Json.JsonSerializer.Serialize(backup, JsonFileStore.Options));
    }

    /// <summary>
    /// Reads a backup file. Accepts the versioned envelope or a bare job array.
    /// Returns sanitized jobs (unique non-empty IDs/names) plus optional notification settings.
    /// </summary>
    public static async Task<(List<JobConfig> Jobs, NotificationSettings? Notification)> ImportAsync(
        string file, IEnumerable<string>? existingJobIds = null)
    {
        string json;
        try { json = await File.ReadAllTextAsync(file); }
        catch (Exception ex) { throw new InvalidDataException($"Cannot read {file}: {ex.Message}", ex); }

        System.Text.Json.JsonDocument doc;
        try { doc = System.Text.Json.JsonDocument.Parse(json); }
        catch (Exception ex) { throw new InvalidDataException($"Not valid JSON: {ex.Message}", ex); }
        using (doc)
        {
            List<JobConfig>? jobs;
            NotificationSettings? notification = null;
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                jobs = System.Text.Json.JsonSerializer.Deserialize<List<JobConfig>>(json, JsonFileStore.Options);
            }
            else if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var backup = System.Text.Json.JsonSerializer.Deserialize<SettingsBackup>(json, JsonFileStore.Options)
                    ?? throw new InvalidDataException("Unrecognized backup file (expected jobs array or backup object).");
                jobs = backup.Jobs ?? new List<JobConfig>();
                notification = backup.Notification;
            }
            else throw new InvalidDataException("Unrecognized backup file (expected jobs array or backup object).");

            return (Sanitize(jobs ?? new List<JobConfig>(), existingJobIds), notification);
        }
    }

    internal static List<JobConfig> Sanitize(IEnumerable<JobConfig> incoming, IEnumerable<string>? existingJobIds)
    {
        var seen = new HashSet<string>(existingJobIds ?? Enumerable.Empty<string>());
        var result = new List<JobConfig>();
        int n = 0;
        foreach (var job in incoming)
        {
            if (job is null) continue;
            if (string.IsNullOrWhiteSpace(job.JobId) || !seen.Add(job.JobId))
                job.JobId = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(job.Name)) job.Name = $"Imported Job {++n}";
            result.Add(job);
        }
        return result;
    }
}
