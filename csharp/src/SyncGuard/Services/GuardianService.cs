using System.Diagnostics;
using System.IO;

namespace SyncGuard.Services;

/// <summary>
/// Real-time folder protection: renames are reverted, deletions logged,
/// .guardian_lockfile auto-restored. Ports guardian.py using
/// FileSystemWatcher instead of watchdog.
/// </summary>
public sealed class GuardianService : IDisposable
{
    private readonly Action<string, string> _log;
    private readonly Dictionary<string, DateTime> _cooldown = new();
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private bool _monitoring;
    private bool _disposed;

    private const string LockFileName = ".guardian_lockfile";

    public string Folder { get; private set; } = string.Empty;
    public bool IsMonitoring => _monitoring;

    private bool _manualPaused;
    private bool _autoPaused;

    /// <summary>Manual pause (user toggles).</summary>
    public bool ManualPaused
    {
        get => _manualPaused;
        set
        {
            if (_manualPaused == value) return;
            _manualPaused = value;
            _log(value ? "Guardian: paused (manual)" : "Guardian: resumed (manual)", "WARN");
        }
    }

    /// <summary>Automatic pause while FreeFileSync runs.</summary>
    public bool AutoPaused
    {
        get => _autoPaused;
        set
        {
            if (_autoPaused == value) return;
            _autoPaused = value;
            _log(value ? "Guardian: FreeFileSync detected – auto-pausing." : "Guardian: FreeFileSync closed – protection resumed.",
                value ? "WARN" : "OK");
        }
    }

    private bool EffectivelyPaused => _manualPaused || _autoPaused;

    public GuardianService(Action<string, string>? log = null) =>
        _log = log ?? ((_, _) => { });

    public void Start(string folder)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            _log($"Guardian: folder not accessible: {folder}", "ERROR");
            return;
        }
        Folder = folder;
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            // 8 KB default drops events under churn; 64 KB headroom.
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true,
        };
        _watcher.Renamed += OnRenamed;
        _watcher.Deleted += OnDeleted;
        _watcher.Error += (_, e) => _log($"Guardian watcher error: {e.GetException()?.Message}", "ERROR");
        _monitoring = true;
        _manualPaused = false;
        _autoPaused = false;
        EnsureLockFile();
        _log($"Guardian: monitoring {folder}", "OK");
    }

    public void Stop()
    {
        _monitoring = false;
        if (_watcher is not null)
        {
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Renamed -= OnRenamed;
                _watcher.Deleted -= OnDeleted;
                _watcher.Dispose();
            }
            catch { }
            _watcher = null;
        }
    }

    /// <summary>Ports guardian_auto_pause: skip events while FFS runs.</summary>
    public static bool IsFreeFileSyncRunning() =>
        Process.GetProcessesByName("FreeFileSync").Length > 0;

    private bool OnCooldown(string path)
    {
        lock (_gate)
        {
            if (_cooldown.TryGetValue(path, out var t))
            {
                if ((DateTime.UtcNow - t).TotalMilliseconds < 500) return true;
                _cooldown.Remove(path);
            }
            return false;
        }
    }

    private void Mark(string path)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            _cooldown[path] = now;
            foreach (var k in _cooldown.Where(kv => (now - kv.Value).TotalMilliseconds >= 500).Select(kv => kv.Key).ToList())
                _cooldown.Remove(k);
        }
    }

    private void OnRenamed(object? sender, RenamedEventArgs e)
    {
        if (EffectivelyPaused || e.OldFullPath is null || e.FullPath is null) return;
        if (OnCooldown(e.OldFullPath) || OnCooldown(e.FullPath)) return;
        Task.Run(() =>
        {
            try
            {
                if (!File.Exists(e.OldFullPath) && !Directory.Exists(e.OldFullPath) &&
                    (File.Exists(e.FullPath) || Directory.Exists(e.FullPath)))
                {
                    _log($"Guardian: reverting rename  {Path.GetFileName(e.FullPath)} → {Path.GetFileName(e.OldFullPath)}", "WARN");
                    Directory.CreateDirectory(Path.GetDirectoryName(e.OldFullPath)!);
                    if (Directory.Exists(e.FullPath))
                        Directory.Move(e.FullPath, e.OldFullPath);
                    else
                        File.Move(e.FullPath, e.OldFullPath);
                    Mark(e.OldFullPath); Mark(e.FullPath);
                    _log($"Guardian: rename denied  ✓  {Path.GetFileName(e.OldFullPath)}", "OK");
                }
            }
            catch (Exception ex) { _log($"Guardian: rename revert error  {ex.Message}", "ERROR"); }
        });
    }

    private void OnDeleted(object? sender, FileSystemEventArgs e)
    {
        if (EffectivelyPaused || e.FullPath is null) return;
        if (OnCooldown(e.FullPath)) return;
        string path = e.FullPath;
        Task.Run(() =>
        {
            try
            {
                if (File.Exists(path) || Directory.Exists(path)) return;
                if (path.EndsWith(LockFileName, StringComparison.OrdinalIgnoreCase))
                {
                    _log($"Guardian: lockfile deleted – restoring  {Path.GetFileName(path)}", "WARN");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, $"Guardian lockfile – protected since {DateTime.Now:o}\n");
                    Mark(path);
                    _log("Guardian: lockfile restored  ✓", "OK");
                }
                else _log($"Guardian: deletion detected (not restored)  {path}", "WARN");
            }
            catch (Exception ex) { _log($"Guardian: deletion handler error  {ex.Message}", "ERROR"); }
        });
    }

    private void EnsureLockFile()
    {
        try
        {
            var lockFile = Path.Combine(Folder, LockFileName);
            if (!File.Exists(lockFile))
                File.WriteAllText(lockFile, $"Guardian lockfile – protected since {DateTime.Now:o}\n");
        }
        catch (Exception ex) { _log($"Guardian: could not create lockfile: {ex.Message}", "WARN"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
