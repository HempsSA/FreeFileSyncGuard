using System.Text.Json.Serialization;

namespace SyncGuard.Models;

/// <summary>
/// One sync job. JSON-serializable; field names match the Python
/// syncguard_jobs.json schema so legacy configs import losslessly.
/// </summary>
public sealed class JobConfig
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "New Job";

    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("ffs_exe")]
    public string FfsExe { get; set; } = AppPaths.DefaultFfsExe;

    [JsonPropertyName("batch_file")]
    public string BatchFile { get; set; } = string.Empty;

    [JsonPropertyName("log_file")]
    public string LogFile { get; set; } = string.Empty;

    [JsonPropertyName("threshold")]
    public int Threshold { get; set; } = 40;

    [JsonPropertyName("hours_back")]
    public int HoursBack { get; set; } = 24;

    [JsonPropertyName("num_workers")]
    public int NumWorkers { get; set; } = 0;

    [JsonPropertyName("exclude_patterns")]
    public List<string> ExcludePatterns { get; set; } = new();

    [JsonPropertyName("schedule_times")]
    public List<string> ScheduleTimes { get; set; } = new();

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("guardian_folder")]
    public string GuardianFolder { get; set; } = string.Empty;

    [JsonPropertyName("guardian_auto_pause")]
    public bool GuardianAutoPause { get; set; } = false;

    // Ransomware protection
    [JsonPropertyName("destination_path")]
    public string DestinationPath { get; set; } = string.Empty;

    [JsonPropertyName("ransomware_protection")]
    public bool RansomwareProtection { get; set; } = true;

    [JsonPropertyName("entropy_threshold")]
    public double EntropyThreshold { get; set; } = 7.5;

    [JsonPropertyName("snapshot_before_sync")]
    public bool SnapshotBeforeSync { get; set; } = true;

    [JsonPropertyName("max_snapshots")]
    public int MaxSnapshots { get; set; } = 3;

    [JsonPropertyName("custom_extensions")]
    public List<string> CustomExtensions { get; set; } = new();

    [JsonPropertyName("anomaly_block_score")]
    public double AnomalyBlockScore { get; set; } = 60.0;

    public JobConfig CloneWithNewId()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        var clone = System.Text.Json.JsonSerializer.Deserialize<JobConfig>(json)!;
        clone.JobId = Guid.NewGuid().ToString("N");
        clone.Name += " (copy)";
        return clone;
    }

    /// <summary>
    /// Common Windows files/folders worth excluding. Ports
    /// DEFAULT_EXCLUDE_PATTERNS from the Python build; shown as a
    /// display fallback for jobs with no saved patterns.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExcludePatterns = new[]
    {
        // Windows system
        "System Volume Information",
        "$Recycle.Bin",
        "Recycle",
        "RECYCLER",
        // Windows temp / cache
        "*.tmp",
        "*.temp",
        "~$*",
        "Thumbs.db",
        "desktop.ini",
        "*.lnk",
        // Windows update / installer cache
        "WinSxS",
        "SoftwareDistribution",
        "Installer",
        // Windows Defender / antivirus
        "*msdownld.tmp",
        // Windows logs
        "*.log",
        // Browser caches
        "*.idx",
        // Office lock files
        "~*.*",
        // macOS (if cross-syncing)
        ".DS_Store",
        "._*",
        "__MACOSX",
        // Linux (if cross-syncing)
        ".Trash-*",
        ".cache",
        // Version control
        ".git",
        ".svn",
        ".hg",
        // SyncGuard internal
        "syncguard_cache",
    };
}

/// <summary>Global notification settings (syncguard_settings.json).</summary>
public sealed class NotificationSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("server")]
    public string Server { get; set; } = "https://ntfy.sh";

    [JsonPropertyName("topic")]
    public string Topic { get; set; } = "syncguard-alerts";

    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("notify_ok")]
    public bool NotifyOk { get; set; } = true;

    [JsonPropertyName("notify_warn")]
    public bool NotifyWarn { get; set; } = true;

    [JsonPropertyName("notify_error")]
    public bool NotifyError { get; set; } = true;
}
