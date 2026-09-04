namespace SyncGuard.Models;

/// <summary>Live scan progress, reported by ParallelScanner. Ports ScanProgress.</summary>
public sealed class ScanProgress
{
    public long Scanned { get; init; }
    public long Changed { get; init; }
    public long TotalHint { get; init; }
    public int SkippedDirs { get; init; }
    public int ActiveDirs { get; init; }
    public string CurrentDir { get; init; } = string.Empty;
    public double FilesPerSec { get; init; }
    public bool Warm { get; init; }
    public string Engine { get; init; } = "parallel";
    public int Workers { get; init; }

    public double Fraction => TotalHint > 0 ? Math.Min(1.0, (double)Scanned / TotalHint) : 0.0;
}

/// <summary>Outcome of a full source scan.</summary>
public sealed class ScanResult
{
    public bool Aborted { get; init; }
    public bool Incomplete { get; init; }
    public List<string> Errors { get; init; } = new();
    public long Total { get; init; }
    public long Changed { get; init; }
    public long Deleted { get; init; }
    public double Percent { get; init; }
    public bool WasCold { get; init; }
    public Dictionary<string, FileRecord> NewFiles { get; init; } = new();
    public Dictionary<string, double> NewDirs { get; init; } = new();
    /// <summary>Normalized paths of new/modified files (for entropy/extension sampling).</summary>
    public List<string> ChangedPaths { get; init; } = new();
    public TimeSpan Elapsed { get; init; }
}

/// <summary>Cached/indexed file entry (mtime seconds unix + size).</summary>
public sealed record FileRecord(double Mtime, long Size);

/// <summary>Persisted per-run history row (SQLite + CSV export).</summary>
public sealed class ScanRecord
{
    public long Id { get; set; }
    public string JobId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Status { get; set; } = "IDLE";
    public long Total { get; set; }
    public long Changed { get; set; }
    public double Percent { get; set; }
    public double DurationSeconds { get; set; }
    public string TriggeredBy { get; set; } = string.Empty;
    public double AnomalyScore { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>Job run outcome returned by ChangeGuardService.</summary>
public sealed class GuardOutcome
{
    public string Status { get; init; } = "ERROR";
    public long Total { get; init; }
    public long Changed { get; init; }
    public double Percent { get; init; }
    public AnomalyScore? Anomaly { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>Entropy sampling result. Ports EntropyResult.</summary>
public sealed class EntropyResult
{
    public int Sampled { get; init; }
    public int HighEntropy { get; init; }
    public double AvgEntropy { get; init; }
    public double MaxEntropy { get; init; }
    public List<string> FlaggedFiles { get; init; } = new();
    public bool IsSuspicious { get; init; }
}

/// <summary>Extension anomaly result. Ports ExtensionResult.</summary>
public sealed class ExtensionResult
{
    public int TotalChanged { get; init; }
    public int Suspicious { get; init; }
    public Dictionary<string, int> SuspiciousExts { get; init; } = new();
    public List<string> FlaggedFiles { get; init; } = new();
    public bool IsSuspicious { get; init; }
}

/// <summary>Composite anomaly assessment. Ports AnomalyScore.</summary>
public sealed class AnomalyScore
{
    public double ChangeRate { get; init; }
    public bool EntropyFlag { get; init; }
    public bool ExtensionFlag { get; init; }
    public double DeleteRatio { get; init; }
    public double RenameRatio { get; init; }
    public double Score { get; init; }
    public bool IsBlocked { get; init; }
    public List<string> Reasons { get; init; } = new();
    public EntropyResult? EntropyResult { get; init; }
    public ExtensionResult? ExtensionResult { get; init; }
}

/// <summary>Destination manifest snapshot. Ports SnapshotManifest.</summary>
public sealed class SnapshotManifest
{
    public string JobId { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public double Epoch { get; set; }
    public string DestPath { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public long TotalSize { get; set; }
    public Dictionary<string, SnapshotEntry> Files { get; set; } = new();
}

public sealed class SnapshotEntry
{
    public long Size { get; set; }
    public double Mtime { get; set; }
    public string Hash { get; set; } = string.Empty;
}

/// <summary>Pre/post sync comparison. Ports SnapshotDiff.</summary>
public sealed class SnapshotDiff
{
    public int Added { get; set; }
    public int Removed { get; set; }
    public int Modified { get; set; }
    public int Unchanged { get; set; }
    public List<string> HashMismatch { get; set; } = new();
    public List<string> SizeAnomaly { get; set; } = new();
    public bool IsClean { get; set; } = true;
    public string Summary { get; set; } = string.Empty;
}
