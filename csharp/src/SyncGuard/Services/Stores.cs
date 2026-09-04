using System.Text.Json;
using SyncGuard.Models;

namespace SyncGuard.Services;

/// <summary>
/// Thread-safe JSON job store. Ports JobStore. Auto-imports the legacy
/// syncguard_jobs.json (Python schema-compatible) on first run.
/// </summary>
public sealed class JobStore
{
    private readonly string _path;
    private readonly ReaderWriterLockSlim _lock = new();
    private List<JobConfig> _jobs = new();

    public JobStore(string? path = null)
    {
        _path = path ?? AppPaths.ConfigFile;
        Load();
    }

    public IReadOnlyList<JobConfig> Jobs
    {
        get { _lock.EnterReadLock(); try { return _jobs.ToList(); } finally { _lock.ExitReadLock(); } }
    }

    private void Load()
    {
        if (!File.Exists(_path) && File.Exists(AppPaths.LegacyConfigFile))
        {
            try
            {
                File.Copy(AppPaths.LegacyConfigFile, _path);
                AppLog.Write($"Imported legacy job config from {AppPaths.LegacyConfigFile}");
            }
            catch (Exception ex) { AppLog.Write($"Legacy config import failed: {ex.Message}", "WARN"); }
        }

        var raw = JsonFileStore.LoadWithRecovery<List<JobConfig>>(_path, () => new List<JobConfig>(), "Job configuration");
        var seen = new HashSet<string>();
        bool migrated = false;
        foreach (var job in raw)
        {
            if (string.IsNullOrWhiteSpace(job.JobId) || !seen.Add(job.JobId))
            {
                job.JobId = Guid.NewGuid().ToString("N");
                migrated = true;
            }
        }
        _jobs = raw;
        if (migrated) Save();
        else if (_jobs.Count == 0 && !File.Exists(_path))
        {
            // Seed a starter job like the Python default config.
            _jobs.Add(new JobConfig { Name = "Job 1" });
            Save();
        }
    }

    public void Save()
    {
        _lock.EnterWriteLock();
        try { JsonFileStore.AtomicWrite(_path, _jobs); }
        finally { _lock.ExitWriteLock(); }
    }

    public void Add(JobConfig job)
    {
        _lock.EnterWriteLock();
        try { _jobs.Add(job); JsonFileStore.AtomicWrite(_path, _jobs); }
        finally { _lock.ExitWriteLock(); }
    }

    public void RemoveAt(int index)
    {
        _lock.EnterWriteLock();
        try { _jobs.RemoveAt(index); JsonFileStore.AtomicWrite(_path, _jobs); }
        finally { _lock.ExitWriteLock(); }
    }

    public void Clear()
    {
        _lock.EnterWriteLock();
        try { _jobs.Clear(); JsonFileStore.AtomicWrite(_path, _jobs); }
        finally { _lock.ExitWriteLock(); }
    }

    public void UpdateAt(int index, JobConfig job)
    {
        _lock.EnterWriteLock();
        try { _jobs[index] = job; JsonFileStore.AtomicWrite(_path, _jobs); }
        finally { _lock.ExitWriteLock(); }
    }

    public string ExportJson() => JsonSerializer.Serialize(_jobs, JsonFileStore.Options);
}

/// <summary>Global settings store (notifications). Ports SettingsStore.</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    public NotificationSettings Notification { get; private set; } = new();

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        Load();
    }

    private sealed class Envelope
    {
        public NotificationSettings? Notification { get; set; }
    }

    private void Load()
    {
        var raw = JsonFileStore.LoadWithRecovery<Envelope>(_path, () => new Envelope(), "Global settings");
        Notification = raw.Notification ?? new NotificationSettings();
    }

    public void Save() => JsonFileStore.AtomicWrite(_path, new Envelope { Notification = Notification });
}
