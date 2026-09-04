using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SyncGuard.Models;
using SyncGuard.Services;

namespace SyncGuard.ViewModels;

/// <summary>One row in the job list: config + live status.</summary>
public sealed partial class JobItem : ObservableObject
{
    public JobConfig Job { get; }

    [ObservableProperty]
    private string _status = "IDLE";

    public string DisplayName => Job.Name;

    public JobItem(JobConfig job) => Job = job;

    public void RefreshName() => OnPropertyChanged(nameof(DisplayName));
}

/// <summary>Data passed to the threshold-override dialog.</summary>
public sealed record ThresholdRequest(string JobName, double Percent, long Total, long Changed, int Limit);

/// <summary>
/// Main view-model. Ports SyncGuardApp: job CRUD, run pipeline,
/// scheduling, history, guardian, ransomware settings, notifications,
/// activity log and the FFS exclude tool.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly JobStore _store;
    private readonly SettingsStore _settings;
    private readonly AppDatabase _db;
    private readonly ScanHistoryService _history;
    private readonly Dispatcher _dispatcher;
    private readonly SchedulerService _scheduler;
    private readonly GuardianService _guardian;
    private readonly Dictionary<string, FileIndexService> _indexes = new();
    private readonly HashSet<string> _runningJobs = new();
    private readonly HashSet<string> _runningSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _runGate = new();
    private readonly DispatcherTimer _countdownTimer;
    private readonly DispatcherTimer _guardianPollTimer;
    private ParallelScanner? _activeScanner;
    private readonly object _scannerGate = new();
    private bool _disposed;
    private bool _suspendSave;

    /// <summary>Set by the view: shows the override dialog on the UI thread.</summary>
    public Func<ThresholdRequest, bool>? ThresholdPrompt { get; set; }

    public event Action<string>? GuardianStateChanged;

    // ---- Job list ----
    [ObservableProperty]
    private ObservableCollection<JobItem> _jobs = new();

    [ObservableProperty]
    private JobItem? _selectedJob;

    // ---- Config tab ----
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _sourcePath = string.Empty;
    [ObservableProperty] private string _ffsExe = string.Empty;
    [ObservableProperty] private string _batchFile = string.Empty;
    [ObservableProperty] private string _logFile = string.Empty;
    [ObservableProperty] private int _threshold = 40;
    [ObservableProperty] private int _hoursBack = 24;
    [ObservableProperty] private int _numWorkers;
    [ObservableProperty] private string _excludeText = string.Empty;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _cacheInfo = "Cache: not loaded";
    [ObservableProperty] private string _pathStatus = string.Empty;

    // ---- Schedule tab ----
    [ObservableProperty] private ObservableCollection<string> _scheduleTimes = new();
    [ObservableProperty] private string _newTimeText = string.Empty;
    [ObservableProperty] private string _nextRunText = string.Empty;
    [ObservableProperty] private string _countdownText = string.Empty;

    // ---- History tab ----
    [ObservableProperty] private ObservableCollection<ScanRecord> _historyRecords = new();
    [ObservableProperty] private string _historyStatus = string.Empty;

    // ---- Guardian tab ----
    [ObservableProperty] private string _guardianFolder = string.Empty;
    [ObservableProperty] private bool _guardianAutoPause;
    [ObservableProperty] private string _guardianStatus = "Guardian: idle";
    [ObservableProperty] private bool _guardianRunning;

    // ---- Ransomware tab ----
    [ObservableProperty] private string _destinationPath = string.Empty;
    [ObservableProperty] private bool _ransomwareProtection = true;
    [ObservableProperty] private double _entropyThreshold = 7.5;
    [ObservableProperty] private bool _snapshotBeforeSync = true;
    [ObservableProperty] private int _maxSnapshots = 3;
    [ObservableProperty] private double _anomalyBlockScore = 60;
    [ObservableProperty] private string _customExtensionsText = string.Empty;

    // ---- Notifications tab ----
    [ObservableProperty] private bool _ntfyEnabled;
    [ObservableProperty] private string _ntfyServer = NotificationService.DefaultServer;
    [ObservableProperty] private string _ntfyTopic = NotificationService.DefaultTopic;
    [ObservableProperty] private string _ntfyToken = string.Empty;
    [ObservableProperty] private bool _ntfyOk = true;
    [ObservableProperty] private bool _ntfyWarn = true;
    [ObservableProperty] private bool _ntfyError = true;
    [ObservableProperty] private string _ntfyStatus = string.Empty;

    // ---- Progress / log ----
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _scanPaused;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private ObservableCollection<LogLine> _logLines = new();

    public string ScanPauseText => ScanPaused ? "Resume" : "Pause";

    // ---- FFS excludes tab ----
    [ObservableProperty] private ObservableCollection<string> _ffsFiles = new();
    [ObservableProperty] private string? _selectedFfsFile;
    [ObservableProperty] private string _ffsFilterText = string.Join("\n", FfsExcludeService.WindowsExcludes);
    [ObservableProperty] private string _ffsOutput = string.Empty;

    public NotificationSettings NtfySettings => _settings.Notification;

    public MainViewModel(JobStore store, SettingsStore settings, AppDatabase db)
    {
        _store = store;
        _settings = settings;
        _db = db;
        _history = new ScanHistoryService(db);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _guardian = new GuardianService((m, l) => Log(m, l));
        _scheduler = new SchedulerService(() => _store.Jobs, RunJobAsync);

        foreach (var j in _store.Jobs) Jobs.Add(new JobItem(j));
        if (Jobs.Count > 0) SelectedJob = Jobs[0];

        var n = _settings.Notification;
        _ntfyEnabled = n.Enabled; _ntfyServer = n.Server; _ntfyTopic = n.Topic;
        _ntfyToken = n.AccessToken; _ntfyOk = n.NotifyOk; _ntfyWarn = n.NotifyWarn; _ntfyError = n.NotifyError;

        AppLog.LinesChanged += RefreshLog;
        RefreshLog();

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => UpdateCountdown();
        _countdownTimer.Start();

        // Ports the Python guardian auto-pause poll (was every 2 s there;
        // 5 s here is plenty and cheaper).
        _guardianPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _guardianPollTimer.Tick += (_, _) => PollGuardianAutoPause();
        _guardianPollTimer.Start();

        Log($"{AppPaths.AppName} {AppPaths.AppVersion} started.", "OK");
    }

    partial void OnScanPausedChanged(bool value) => OnPropertyChanged(nameof(ScanPauseText));

    partial void OnSelectedJobChanged(JobItem? oldValue, JobItem? newValue)
    {
        if (oldValue is not null) SaveEditsTo(oldValue.Job, persist: true);
        if (newValue is not null) LoadJobIntoForm(newValue.Job);
        UpdateCountdown();
        _ = RefreshHistoryAsync();
        UpdateGuardianStatus();
    }

    // ================= Jobs =================

    [RelayCommand]
    private void NewJob()
    {
        PersistSelection();
        var job = new JobConfig { Name = $"Job {Jobs.Count + 1}" };
        _store.Add(job);
        var item = new JobItem(job);
        Jobs.Add(item);
        SelectedJob = item;
        Log($"Created job: {job.Name}", "OK");
    }

    [RelayCommand]
    private void DuplicateJob()
    {
        if (SelectedJob is null) return;
        PersistSelection();
        var dup = SelectedJob.Job.CloneWithNewId();
        _store.Add(dup);
        var item = new JobItem(dup);
        Jobs.Add(item);
        SelectedJob = item;
        Log($"Duplicated job: {dup.Name}", "OK");
    }

    [RelayCommand]
    private void RemoveJob()
    {
        if (SelectedJob is null) return;
        int idx = Jobs.IndexOf(SelectedJob);
        string name = SelectedJob.Job.Name;
        _store.RemoveAt(_store.Jobs.ToList().FindIndex(j => j.JobId == SelectedJob.Job.JobId));
        Jobs.RemoveAt(idx);
        SelectedJob = Jobs.Count > 0 ? Jobs[Math.Min(idx, Jobs.Count - 1)] : null;
        Log($"Removed job: {name}", "WARN");
    }

    [RelayCommand]
    private void SaveJob()
    {
        if (SelectedJob is null) return;
        SaveEditsTo(SelectedJob.Job, persist: true);
        SelectedJob.RefreshName();
        Log($"Saved job: {SelectedJob.Job.Name}", "OK");
    }

    private void PersistSelection()
    {
        if (SelectedJob is not null) SaveEditsTo(SelectedJob.Job, persist: true);
    }

    private void LoadJobIntoForm(JobConfig job)
    {
        _suspendSave = true;
        try
        {
            Name = job.Name; SourcePath = job.SourcePath; FfsExe = job.FfsExe;
            BatchFile = job.BatchFile; LogFile = job.LogFile;
            Threshold = job.Threshold; HoursBack = job.HoursBack; NumWorkers = job.NumWorkers;
            // Python parity: jobs with no saved patterns display the
            // common Windows defaults (persisted on next save).
            ExcludeText = job.ExcludePatterns.Count > 0
                ? string.Join("\n", job.ExcludePatterns)
                : string.Join("\n", JobConfig.DefaultExcludePatterns);
            Enabled = job.Enabled;
            ScheduleTimes = new ObservableCollection<string>(job.ScheduleTimes);
            GuardianFolder = job.GuardianFolder; GuardianAutoPause = job.GuardianAutoPause;
            DestinationPath = job.DestinationPath; RansomwareProtection = job.RansomwareProtection;
            EntropyThreshold = job.EntropyThreshold; SnapshotBeforeSync = job.SnapshotBeforeSync;
            MaxSnapshots = job.MaxSnapshots; AnomalyBlockScore = job.AnomalyBlockScore;
            CustomExtensionsText = string.Join("\n", job.CustomExtensions);
            PathStatus = ValidatePaths(job);
            CacheInfo = $"Cache: {GetIndex(job).KnownTotal:N0} files indexed ({GetIndex(job).AgeString})";
        }
        finally { _suspendSave = false; }
    }

    private void SaveEditsTo(JobConfig job, bool persist)
    {
        if (_suspendSave) return;
        job.Name = Name; job.SourcePath = SourcePath; job.FfsExe = FfsExe;
        job.BatchFile = BatchFile; job.LogFile = LogFile;
        job.Threshold = Threshold; job.HoursBack = HoursBack; job.NumWorkers = NumWorkers;
        job.ExcludePatterns = ExcludeText.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        job.Enabled = Enabled;
        job.ScheduleTimes = ScheduleTimes.ToList();
        job.GuardianFolder = GuardianFolder; job.GuardianAutoPause = GuardianAutoPause;
        job.DestinationPath = DestinationPath; job.RansomwareProtection = RansomwareProtection;
        job.EntropyThreshold = EntropyThreshold; job.SnapshotBeforeSync = SnapshotBeforeSync;
        job.MaxSnapshots = MaxSnapshots; job.AnomalyBlockScore = AnomalyBlockScore;
        job.CustomExtensions = CustomExtensionsText.Split('\n').Select(s => s.Trim().TrimStart('.')).Where(s => s.Length > 0).Select(s => "." + s.ToLowerInvariant()).ToList();
        if (persist)
        {
            int idx = _store.Jobs.ToList().FindIndex(j => j.JobId == job.JobId);
            if (idx >= 0) _store.UpdateAt(idx, job);
        }
    }

    private static string ValidatePaths(JobConfig job)
    {
        var problems = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.SourcePath) && !Directory.Exists(job.SourcePath)) problems.Add("source missing");
        if (!string.IsNullOrWhiteSpace(job.FfsExe) && !File.Exists(job.FfsExe)) problems.Add("FFS exe missing");
        if (!string.IsNullOrWhiteSpace(job.BatchFile) && !File.Exists(job.BatchFile)) problems.Add("batch file missing");
        return problems.Count == 0 ? "Paths OK" : "Missing: " + string.Join(", ", problems);
    }

    private FileIndexService GetIndex(JobConfig job)
    {
        lock (_indexes)
        {
            if (!_indexes.TryGetValue(job.JobId, out var idx))
            {
                idx = new FileIndexService(_db, string.IsNullOrWhiteSpace(job.SourcePath) ? $"job:{job.JobId}" : job.SourcePath);
                _indexes[job.JobId] = idx;
            }
            return idx;
        }
    }

    // ================= Run pipeline =================

    [RelayCommand]
    private async Task RunJob()
    {
        if (SelectedJob is null) return;
        PersistSelection();
        await RunJobAsync(SelectedJob.Job.JobId, "manual");
    }

    [RelayCommand]
    private async Task RunAllJobs()
    {
        PersistSelection();
        foreach (var item in Jobs.Where(j => j.Job.Enabled).ToList())
            await RunJobAsync(item.Job.JobId, "manual-all");
    }

    public async Task RunJobAsync(string jobId, string triggeredBy)
    {
        JobItem? item;
        lock (_runGate)
        {
            item = Jobs.FirstOrDefault(j => j.Job.JobId == jobId);
            if (item is null || _runningJobs.Contains(jobId)) return;
            string? src = item.Job.SourcePath.Length > 0 ? SourceKey(item.Job.SourcePath) : null;
            if (src is not null && _runningSources.Contains(src))
            {
                Log($"Same-source guard: '{item.Job.Name}' shares a source with a running job — skipped.", "WARN");
                return;
            }
            _runningJobs.Add(jobId);
            if (src is not null) _runningSources.Add(src);
        }

        await _dispatcher.InvokeAsync(() =>
        {
            item.Status = "RUNNING";
            IsRunning = true; ScanPaused = false;
            ProgressIndeterminate = true;
            ProgressText = $"Scanning {item.Job.Name}…";
        });

        var start = DateTime.Now;
        GuardOutcome outcome = new() { Status = "ERROR" };
        try
        {
            var job = item.Job;
            var guard = new ChangeGuardService(
                job, GetIndex(job), new SnapshotService(job.MaxSnapshots),
                (m, l) => Log(m, l),
                p => _dispatcher.InvokeAsync(() =>
                {
                    ProgressIndeterminate = false;
                    ProgressValue = p.Fraction * 100;
                    ProgressText = $"{p.Scanned:N0} files ({p.Changed:N0} changed, {p.FilesPerSec:N0}/s) — {p.CurrentDir}";
                }),
                (pct, total, changed) => ThresholdPrompt?.Invoke(new ThresholdRequest(job.Name, pct, total, changed, job.Threshold)) == true,
                scanner => { lock (_scannerGate) _activeScanner = scanner; });

            outcome = await Task.Run(() => guard.CheckAndRun());
        }
        catch (Exception ex)
        {
            Log($"Run failed: {ex.Message}", "ERROR");
        }
        finally
        {
            lock (_scannerGate) _activeScanner = null;
            lock (_runGate)
            {
                _runningJobs.Remove(jobId);
                if (item.Job.SourcePath.Length > 0)
                    _runningSources.Remove(SourceKey(item.Job.SourcePath));
            }
            await _dispatcher.InvokeAsync(() => ScanPaused = false);
        }

        var duration = DateTime.Now - start;
        _history.Add(new ScanRecord
        {
            JobId = item.Job.JobId, Timestamp = start, Status = outcome.Status,
            Total = outcome.Total, Changed = outcome.Changed, Percent = outcome.Percent,
            DurationSeconds = duration.TotalSeconds, TriggeredBy = triggeredBy,
            AnomalyScore = outcome.Anomaly?.Score ?? 0,
            Message = outcome.Status,
        });
        _history.ImportLegacy(item.Job.JobId, item.Job.JobId); // no-op once rows exist

        await _dispatcher.InvokeAsync(() =>
        {
            item.Status = outcome.Status;
            IsRunning = _runningJobs.Count > 0;
            if (!IsRunning) { ProgressIndeterminate = false; ProgressValue = 0; ProgressText = $"Last run: {item.Job.Name} → {outcome.Status}"; }
            CacheInfo = $"Cache: {GetIndex(item.Job).KnownTotal:N0} files indexed ({GetIndex(item.Job).AgeString})";
        });
        _ = RefreshHistoryAsync();
        await NotificationService.SendJobAsync(item.Job.Name, outcome.Status,
            outcome.Total, outcome.Changed, outcome.Percent,
            duration.TotalSeconds, triggeredBy, _settings.Notification);
    }

    /// <summary>Canonical same-source-guard key; never throws.</summary>
    private static string SourceKey(string path)
    {
        try { return Path.GetFullPath(path.TrimEnd('\\', '/')).ToUpperInvariant(); }
        catch { return path.ToUpperInvariant(); }
    }

    [RelayCommand]
    private void ClearCache()
    {
        if (SelectedJob is null) return;
        GetIndex(SelectedJob.Job).Clear();
        CacheInfo = "Cache: cleared";
        Log($"Cleared scan cache for {SelectedJob.Job.Name}", "WARN");
    }

    /// <summary>Merges the common Windows defaults into the exclude box (keeps custom entries).</summary>
    [RelayCommand]
    private void LoadWindowsDefaults()
    {
        var existing = ExcludeText.Split('\n')
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var p in JobConfig.DefaultExcludePatterns)
            if (seen.Add(p)) { existing.Add(p); added++; }
        ExcludeText = string.Join("\n", existing);
        Log(added > 0 ? $"Added {added} Windows default exclude pattern(s)." : "Windows default patterns already present.", "OK");
    }

    // ================= Settings export / import =================

    [RelayCommand]
    private async Task ExportSettings()
    {
        PersistSelection();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"syncguard-backup-{DateTime.Now:yyyyMMdd}.syncguard.json",
            Filter = "SyncGuard backup (*.syncguard.json)|*.syncguard.json|JSON files (*.json)|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await SettingsBackupService.ExportAsync(dlg.FileName, _store.Jobs, _settings.Notification);
            Log($"Exported {_store.Jobs.Count} job(s) + notification settings to {dlg.FileName}", "OK");
        }
        catch (Exception ex) { Log($"Export failed: {ex.Message}", "ERROR"); }
    }

    [RelayCommand]
    private async Task ImportSettings()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "SyncGuard backup (*.syncguard.json;*.json)|*.syncguard.json;*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var (jobs, notification) = await SettingsBackupService.ImportAsync(
                dlg.FileName, _store.Jobs.Select(j => j.JobId));
            foreach (var job in jobs)
            {
                _store.Add(job);
                Jobs.Add(new JobItem(job));
            }
            if (notification is not null)
            {
                var n = _settings.Notification;
                n.Enabled = notification.Enabled; n.Server = notification.Server;
                n.Topic = notification.Topic; n.AccessToken = notification.AccessToken;
                n.NotifyOk = notification.NotifyOk; n.NotifyWarn = notification.NotifyWarn;
                n.NotifyError = notification.NotifyError;
                _settings.Save();
                NtfyEnabled = n.Enabled; NtfyServer = n.Server; NtfyTopic = n.Topic;
                NtfyToken = n.AccessToken; NtfyOk = n.NotifyOk; NtfyWarn = n.NotifyWarn; NtfyError = n.NotifyError;
            }
            if (SelectedJob is null && Jobs.Count > 0) SelectedJob = Jobs[0];
            Log(jobs.Count == 0 && notification is null
                ? "Import file contained nothing to import."
                : $"Imported {jobs.Count} job(s)" + (notification is not null ? " + notification settings." : "."),
                jobs.Count == 0 && notification is null ? "WARN" : "OK");
        }
        catch (Exception ex) { Log($"Import failed: {ex.Message}", "ERROR"); }
    }

    // ---- Scan pause / abort (ports the Python progress-panel controls) ----

    [RelayCommand]
    private async Task PauseResumeScan()
    {
        ParallelScanner? scanner;
        lock (_scannerGate) scanner = _activeScanner;
        if (scanner is null) return;
        if (ScanPaused) { scanner.Resume(); }
        else { scanner.Pause(); }
        await _dispatcher.InvokeAsync(() =>
        {
            ScanPaused = scanner.IsPaused;
            ProgressText = ScanPaused ? "Paused — workers halted between directories" : "Resumed…";
        });
    }

    [RelayCommand]
    private void AbortScan()
    {
        ParallelScanner? scanner;
        lock (_scannerGate) scanner = _activeScanner;
        if (scanner is null) return;
        Log("Abort requested — workers will stop between directories.", "WARN");
        scanner.Stop();
    }

    // ================= Schedule =================

    [RelayCommand]
    private void AddScheduleTime()
    {
        if (!SchedulerService.TryParseHm(NewTimeText, out _, out _))
        {
            Log($"Invalid time '{NewTimeText}' — use HH:MM (24h).", "ERROR");
            return;
        }
        string norm = NewTimeText.Trim();
        if (!ScheduleTimes.Contains(norm)) ScheduleTimes.Add(norm);
        NewTimeText = string.Empty;
        PersistSchedule();
        UpdateCountdown();
    }

    [RelayCommand]
    private void RemoveScheduleTime(string? time)
    {
        if (time is not null && ScheduleTimes.Remove(time)) { PersistSchedule(); UpdateCountdown(); }
    }

    private void PersistSchedule()
    {
        if (SelectedJob is null || _suspendSave) return;
        SelectedJob.Job.ScheduleTimes = ScheduleTimes.ToList();
        int idx = _store.Jobs.ToList().FindIndex(j => j.JobId == SelectedJob.Job.JobId);
        if (idx >= 0) _store.UpdateAt(idx, SelectedJob.Job);
    }

    private void UpdateCountdown()
    {
        if (SelectedJob is null)
        {
            NextRunText = string.Empty; CountdownText = string.Empty;
            return;
        }
        var next = SchedulerService.NextRun(SelectedJob.Job, DateTime.Now);
        if (next is null)
        {
            NextRunText = "No schedule set"; CountdownText = string.Empty;
            return;
        }
        NextRunText = $"Next run: {next:ddd HH:mm}";
        var left = next.Value - DateTime.Now;
        CountdownText = $"⏳ {left.Days}d {left.Hours:D2}h {left.Minutes:D2}m {left.Seconds:D2}s";
    }

    // ================= History =================

    public async Task RefreshHistoryAsync()
    {
        if (SelectedJob is null || _disposed) return;
        var jobId = SelectedJob.Job.JobId;
        List<ScanRecord> records;
        try { records = await Task.Run(() => _history.Get(jobId)); }
        catch (Exception ex) { Log($"History load failed: {ex.Message}", "ERROR"); return; }
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || SelectedJob?.Job.JobId != jobId) return;
            HistoryRecords = new ObservableCollection<ScanRecord>(records);
            HistoryStatus = records.Count == 0 ? "No runs recorded yet." : $"{records.Count} run(s)";
        });
    }

    [RelayCommand]
    private async Task ExportHistoryCsv()
    {
        if (SelectedJob is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{SelectedJob.Job.Name}-history.csv",
            Filter = "CSV files (*.csv)|*.csv",
        };
        if (dlg.ShowDialog() == true)
        {
            var csv = await Task.Run(() => _history.ExportCsv(SelectedJob.Job.JobId));
            await File.WriteAllTextAsync(dlg.FileName, csv);
            Log($"Exported history to {dlg.FileName}", "OK");
        }
    }

    [RelayCommand]
    private async Task ClearHistory()
    {
        if (SelectedJob is null) return;
        var jobId = SelectedJob.Job.JobId;
        await Task.Run(() => _history.Clear(jobId));
        await RefreshHistoryAsync();
        Log("Cleared scan history.", "WARN");
    }

    // ================= Guardian =================

    [RelayCommand]
    private void GuardianStart()
    {
        if (SelectedJob is null) return;
        PersistSelection();
        _guardian.Start(GuardianFolder);
        GuardianRunning = _guardian.IsMonitoring;
        UpdateGuardianStatus();
        GuardianStateChanged?.Invoke(GuardianStatus);
    }

    [RelayCommand]
    private void GuardianStop()
    {
        _guardian.Stop();
        GuardianRunning = false;
        GuardianStatus = "Guardian: idle";
        GuardianStateChanged?.Invoke(GuardianStatus);
    }

    [RelayCommand]
    private void GuardianPauseResume()
    {
        if (!_guardian.IsMonitoring) return;
        _guardian.ManualPaused = !_guardian.ManualPaused;
        UpdateGuardianStatus();
        GuardianStateChanged?.Invoke(GuardianStatus);
    }

    public string GuardianPauseText => _guardian.ManualPaused ? "Resume" : "Pause";

    private void UpdateGuardianStatus()
    {
        if (SelectedJob is null) return;
        GuardianRunning = _guardian.IsMonitoring;
        GuardianStatus = !_guardian.IsMonitoring ? "Guardian: idle"
            : _guardian.ManualPaused ? "⏸ Protection paused (manual)"
            : _guardian.AutoPaused ? "⏸ Auto-paused (FreeFileSync running)"
            : $"Guardian: monitoring {GuardianFolder}";
        OnPropertyChanged(nameof(GuardianPauseText));
    }

    /// <summary>Ports the Python auto-pause poll: pause protection while FFS runs.</summary>
    private void PollGuardianAutoPause()
    {
        try
        {
            if (!_guardian.IsMonitoring) return;
            if (!(SelectedJob?.Job.GuardianAutoPause == true))
            {
                if (_guardian.AutoPaused) { _guardian.AutoPaused = false; UpdateGuardianStatus(); }
                return;
            }
            bool ffsRunning = GuardianService.IsFreeFileSyncRunning();
            if (ffsRunning != _guardian.AutoPaused)
            {
                _guardian.AutoPaused = ffsRunning;
                UpdateGuardianStatus();
                GuardianStateChanged?.Invoke(GuardianStatus);
            }
        }
        catch (Exception ex) { Log($"Guardian poll failed: {ex.Message}", "ERROR"); }
    }

    // ================= Notifications =================

    [RelayCommand]
    private void SaveNotifications()
    {
        var n = _settings.Notification;
        n.Enabled = NtfyEnabled; n.Server = NtfyServer; n.Topic = NtfyTopic;
        n.AccessToken = NtfyToken; n.NotifyOk = NtfyOk; n.NotifyWarn = NtfyWarn; n.NotifyError = NtfyError;
        _settings.Save();
        NtfyStatus = "Notification settings saved.";
    }

    [RelayCommand]
    private async Task TestNotification()
    {
        SaveNotifications();
        NtfyStatus = "Sending test notification…";
        bool ok = await NotificationService.SendAsync("SyncGuard test notification — your topic is reachable.",
            NtfyTopic, "SyncGuard — test", 3, new[] { "bell" }, NtfyServer, NtfyToken);
        NtfyStatus = ok ? "Test notification sent ✓" : "Test notification failed ✗";
    }

    // ================= Log =================

    private void RefreshLog() =>
        _dispatcher.InvokeAsync(() =>
        {
            var snap = AppLog.Snapshot();
            LogLines = new ObservableCollection<LogLine>(snap);
        });

    public void Log(string message, string level = "INFO") => AppLog.Write(message, level);

    [RelayCommand]
    private void ClearLog() => AppLog.ClearView(); // widget only, like Python; file log retained

    // ================= Browse helpers =================

    private static string? BrowseFolder(string? current)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = current ?? string.Empty };
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    private static string? BrowseFile(string? current, string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { FileName = current ?? string.Empty, Filter = filter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    [RelayCommand] private void BrowseSource() { var p = BrowseFolder(SourcePath); if (p is not null) SourcePath = p; }
    [RelayCommand] private void BrowseFfs() { var p = BrowseFile(FfsExe, "FreeFileSync|FreeFileSync.exe|All files|*.*"); if (p is not null) FfsExe = p; }
    [RelayCommand] private void BrowseBatch() { var p = BrowseFile(BatchFile, "FFS configs|*.ffs_batch;*.ffs_gui|All files|*.*"); if (p is not null) BatchFile = p; }
    [RelayCommand] private void BrowseLogFile() { var p = BrowseFile(LogFile, "Log files|*.log;*.txt|All files|*.*"); if (p is not null) LogFile = p; }
    [RelayCommand] private void BrowseGuardian() { var p = BrowseFolder(GuardianFolder); if (p is not null) GuardianFolder = p; }
    [RelayCommand] private void BrowseDestination() { var p = BrowseFolder(DestinationPath); if (p is not null) DestinationPath = p; }

    // ================= FFS excludes =================

    [RelayCommand]
    private void FfsBrowseFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "FreeFileSync configs|*.ffs_batch;*.ffs_gui|Batch files|*.ffs_batch|GUI configs|*.ffs_gui|All files|*.*",
        };
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FileNames)
                if (!FfsFiles.Contains(f)) FfsFiles.Add(f);
    }

    [RelayCommand]
    private void FfsScanFolder()
    {
        var folder = BrowseFolder(null);
        if (folder is null) return;
        int added = 0;
        foreach (var f in FfsExcludeService.ScanFolder(folder))
            if (!FfsFiles.Contains(f)) { FfsFiles.Add(f); added++; }
        AppendFfs(added == 0 ? "No .ffs_batch/.ffs_gui files found.\n" : $"Found {added} file(s).\n");
    }

    [RelayCommand]
    private void FfsClear() => FfsFiles.Clear();

    [RelayCommand]
    private void FfsResetFilters() => FfsFilterText = string.Join("\n", FfsExcludeService.WindowsExcludes);

    [RelayCommand]
    private async Task FfsPreview() => await FfsApplyAsync(dryRun: true);

    [RelayCommand]
    private async Task FfsApply() => await FfsApplyAsync(dryRun: false);

    private async Task FfsApplyAsync(bool dryRun)
    {
        if (FfsFiles.Count == 0) { AppendFfs("Select at least one config file.\n"); return; }
        var filters = FfsFilterText.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (filters.Count == 0) { AppendFfs("Filter list is empty.\n"); return; }
        var files = FfsFiles.ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine((dryRun ? "[DRY RUN] " : "") + $"Processing {files.Count} file(s) with {filters.Count} filter(s)…");
        await Task.Run(() =>
        {
            int changed = 0, added = 0;
            foreach (var f in files)
            {
                try
                {
                    var r = FfsExcludeService.ProcessFile(f, filters, dryRun);
                    if (r.Error is not null) sb.AppendLine($"  SKIP: {Path.GetFileName(f)} - {r.Error}");
                    else if (r.Changed) { changed++; added += r.Added; sb.AppendLine($"  OK: {Path.GetFileName(f)} - +{r.Added} filter(s)"); }
                    else sb.AppendLine($"  {Path.GetFileName(f)} - already up to date");
                }
                catch (Exception ex) { sb.AppendLine($"  ERROR: {Path.GetFileName(f)} - {ex.Message}"); }
            }
            sb.AppendLine(dryRun
                ? $"DRY RUN: {changed} file(s) would be modified, {added} filter(s) total."
                : $"Done: {changed} file(s) modified, {added} filter(s) added.");
        });
        FfsOutput = AppendFfsText(FfsOutput, sb.ToString());
    }

    private void AppendFfs(string text) => FfsOutput = AppendFfsText(FfsOutput, text);

    private static string AppendFfsText(string current, string addition)
    {
        // Keep the output box bounded (last ~300 lines).
        var lines = (current + addition).Split('\n');
        return lines.Length > 300 ? string.Join("\n", lines[^300..]) : current + addition;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PersistSelection();
        SaveNotifications();
        _countdownTimer.Stop();
        _guardianPollTimer.Stop();
        _scheduler.Dispose();
        _guardian.Dispose();
        AppLog.LinesChanged -= RefreshLog;
    }
}
