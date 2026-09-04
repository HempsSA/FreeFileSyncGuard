using System.Diagnostics;
using SyncGuard.Models;

namespace SyncGuard.Services;

/// <summary>
/// Scan → decide → launch FreeFileSync pipeline. Ports ChangeGuard.
/// The threshold-override question is injected via callback so the
/// WPF dialog (or headless auto-deny) can answer it.
/// </summary>
public sealed class ChangeGuardService
{
    private readonly JobConfig _job;
    private readonly FileIndexService _index;
    private readonly SnapshotService _snapshots;
    private readonly Action<string, string> _log;
    private readonly Action<ScanProgress> _progress;
    private readonly Func<double, long, long, bool>? _override;
    private readonly Action<ParallelScanner>? _scannerReady;
    private ParallelScanner? _scanner;

    public ChangeGuardService(
        JobConfig job,
        FileIndexService index,
        SnapshotService snapshots,
        Action<string, string>? log = null,
        Action<ScanProgress>? progress = null,
        Func<double, long, long, bool>? overrideCallback = null,
        Action<ParallelScanner>? scannerReady = null)
    {
        _job = job;
        _index = index;
        _snapshots = snapshots;
        _log = log ?? ((_, _) => { });
        _progress = progress ?? (_ => { });
        _override = overrideCallback;
        _scannerReady = scannerReady;
    }

    public void RequestStop() => _scanner?.Stop();

    public ScanResult? ScanOnly()
    {
        var root = _job.SourcePath;
        if (string.IsNullOrWhiteSpace(root)) { _log("No source path configured.", "ERROR"); return null; }
        if (!Directory.Exists(root)) { _log($"Source path does not exist or is not accessible: {root}", "ERROR"); return null; }

        _scanner = new ParallelScanner(root, _index, _job.NumWorkers, _job.ExcludePatterns, _progress, _log);
        _scannerReady?.Invoke(_scanner);
        ScanResult result;
        try { result = _scanner.Run(); }
        finally { _scanner.Dispose(); _scanner = null; }
        if (result.Aborted) return result;
        if (result.Errors.Count > 0)
        {
            _log($"SCAN INCOMPLETE: {result.Errors.Count} directorie(s) could not be read. Cache retained; sync blocked.", "ERROR");
            return new ScanResult { Incomplete = true, Errors = result.Errors };
        }

        var known = _index.SnapshotFiles();
        long deleted = known.Keys.Count(k => !result.NewFiles.ContainsKey(k));
        long total = result.Total + deleted;
        long changed = result.Changed + deleted;
        double pct = total > 0 ? Math.Round((double)changed / total * 100, 2) : 0.0;
        bool wasCold = _index.KnownTotal == 0;
        _log($"Total: {total}  Changed: {changed}  Deleted: {deleted}  Rate: {pct}%" + (wasCold ? "  [BASELINE - first scan]" : ""), "INFO");
        return new ScanResult
        {
            Total = total, Changed = changed, Deleted = deleted,
            Percent = pct, WasCold = wasCold,
            NewFiles = result.NewFiles, NewDirs = result.NewDirs,
            ChangedPaths = result.ChangedPaths, Elapsed = result.Elapsed,
        };
    }

    public GuardOutcome CheckAndRun()
    {
        var sw = Stopwatch.StartNew();
        _scanner = null;
        _log($"=== Job: {_job.Name} ===", "INFO");

        var result = ScanOnly();
        if (result is null) return new GuardOutcome { Status = "ERROR", Duration = sw.Elapsed };
        if (result.Aborted) return new GuardOutcome { Status = "ABORTED", Duration = sw.Elapsed };
        if (result.Incomplete) return new GuardOutcome { Status = "ERROR", Total = result.Total, Duration = sw.Elapsed };

        if (result.Total == 0)
        {
            _log("No files found in source path. Cache retained; sync blocked.", "WARN");
            return new GuardOutcome { Status = "WARN", Duration = sw.Elapsed };
        }

        if (result.WasCold)
        {
            try { _index.Save(result.NewFiles, result.NewDirs); }
            catch (Exception ex)
            {
                _log($"Baseline could not be saved: {ex.Message}", "ERROR");
                return new GuardOutcome { Status = "ERROR", Total = result.Total, Changed = result.Changed, Percent = result.Percent, Duration = sw.Elapsed };
            }
            _log($"BASELINE CREATED: {result.Total} files indexed. FreeFileSync was NOT launched. Run the job again after reviewing the source.", "WARN");
            return new GuardOutcome { Status = "WARN", Total = result.Total, Changed = result.Changed, Percent = result.Percent, Duration = sw.Elapsed };
        }

        // Ransomware pre-sync checks.
        AnomalyScore? anomaly = null;
        if (_job.RansomwareProtection && result.Changed > 0)
        {
            anomaly = RunRansomwareChecks(result);
            if (anomaly.IsBlocked)
            {
                _log($"RANSOMWARE ALERT: anomaly score {anomaly.Score}/100  BLOCKED", "ERROR");
                foreach (var reason in anomaly.Reasons) _log("  > " + reason, "ERROR");
                return new GuardOutcome { Status = "ABORTED", Total = result.Total, Changed = result.Changed, Percent = result.Percent, Anomaly = anomaly, Duration = sw.Elapsed };
            }
        }

        // Threshold gate.
        if (result.Percent > _job.Threshold)
        {
            _log($"THRESHOLD EXCEEDED: {result.Percent}% changed - limit is {_job.Threshold}%.", "WARN");
            bool proceed = _override?.Invoke(result.Percent, result.Total, result.Changed) == true;
            if (!proceed)
            {
                _log("ABORTED: threshold exceeded. Trusted cache retained.", "ERROR");
                return new GuardOutcome { Status = "ABORTED", Total = result.Total, Changed = result.Changed, Percent = result.Percent, Anomaly = anomaly, Duration = sw.Elapsed };
            }
            _log("User approved threshold override. Launching FreeFileSync...", "WARN");
        }
        else
        {
            _log($"Change rate {result.Percent}% is within threshold. Launching FreeFileSync...", "INFO");
        }

        if (string.IsNullOrWhiteSpace(_job.FfsExe) || string.IsNullOrWhiteSpace(_job.BatchFile))
        {
            _log("FFS exe or batch file not configured. Trusted cache retained.", "ERROR");
            return new GuardOutcome { Status = "ERROR", Total = result.Total, Changed = result.Changed, Percent = result.Percent, Anomaly = anomaly, Duration = sw.Elapsed };
        }

        SnapshotManifest? pre = null;
        if (_job.RansomwareProtection && _job.SnapshotBeforeSync && !string.IsNullOrWhiteSpace(_job.DestinationPath))
        {
            _log("Snapshot: capturing destination manifest...", "INFO");
            pre = _snapshots.Capture(_job.DestinationPath, _job.JobId, _log.Invoke);
            if (pre is not null)
            {
                var fp = _snapshots.Save(pre, _job.MaxSnapshots);
                _log($"Snapshot saved: {Path.GetFileName(fp)} ({pre.TotalFiles} files)", "INFO");
            }
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = _job.FfsExe,
                Arguments = $"\"{_job.BatchFile}\"",
                UseShellExecute = false,
            });
            proc?.WaitForExit();
            int code = proc?.ExitCode ?? -1;
            string status, msg;
            if (code == 0) (msg, status) = ("FreeFileSync finished successfully.", "OK");
            else if (code == 1) (msg, status) = ("FreeFileSync finished with warnings (code 1).", "WARN");
            else if (code == 3) (msg, status) = ("FreeFileSync was aborted by user (code 3).", "WARN");
            else (msg, status) = ($"FreeFileSync exited with code {code}.", "ERROR");
            _log(msg, status);

            if (_job.RansomwareProtection && !string.IsNullOrWhiteSpace(_job.DestinationPath) && (code == 0 || code == 1) && pre is not null)
                PostSyncValidate(pre);

            if (code == 0 || code == 1)
            {
                try
                {
                    _index.Save(result.NewFiles, result.NewDirs);
                    _log("Trusted scan cache committed after approved FreeFileSync completion.", "INFO");
                }
                catch (Exception ex)
                {
                    _log($"FreeFileSync completed, but cache commit failed: {ex.Message}", "ERROR");
                    return new GuardOutcome { Status = "ERROR", Total = result.Total, Changed = result.Changed, Percent = result.Percent, Anomaly = anomaly, Duration = sw.Elapsed };
                }
            }
            else _log("Trusted cache retained because FreeFileSync did not complete successfully.", "WARN");

            return new GuardOutcome { Status = status, Total = result.Total, Changed = result.Changed, Percent = result.Percent, Anomaly = anomaly, Duration = sw.Elapsed };
        }
        catch (Exception ex)
        {
            _log($"Failed to launch FreeFileSync: {ex.Message}. Trusted cache retained.", "ERROR");
            return new GuardOutcome { Status = "ERROR", Total = result.Total, Changed = result.Changed, Percent = result.Percent, Anomaly = anomaly, Duration = sw.Elapsed };
        }
    }

    private AnomalyScore RunRansomwareChecks(ScanResult result)
    {
        // Sample the files that actually changed (not the whole tree).
        var pool = result.ChangedPaths.Count > 0
            ? result.ChangedPaths
            : result.NewFiles.Keys.Take((int)Math.Min(result.Changed, int.MaxValue)).ToList();
        var ent = RansomwareService.SampleEntropy(pool, threshold: _job.EntropyThreshold);
        if (ent.Sampled > 0)
        {
            _log($"Entropy check: sampled {ent.Sampled} files, avg={ent.AvgEntropy}, max={ent.MaxEntropy}, high={ent.HighEntropy}", "INFO");
            if (ent.IsSuspicious) _log($"Entropy ALERT: {ent.HighEntropy}/{ent.Sampled} files have suspicious entropy", "WARN");
        }
        var ext = RansomwareService.DetectSuspiciousExtensions(pool, _job.CustomExtensions);
        if (ext.Suspicious > 0)
        {
            _log($"Extension check: {ext.Suspicious}/{ext.TotalChanged} suspicious extensions", "WARN");
            foreach (var kv in ext.SuspiciousExts) _log($"  > {kv.Key}: {kv.Value} files", "WARN");
        }
        var score = RansomwareService.ComputeAnomalyScore(result.Percent, result.Total, result.Changed, 0,
            entropyResult: ent, extensionResult: ext, blockThreshold: _job.AnomalyBlockScore);
        _log($"Anomaly score: {score.Score}/100 (block threshold: {_job.AnomalyBlockScore})", "INFO");
        return score;
    }

    private void PostSyncValidate(SnapshotManifest pre)
    {
        _log("Post-sync validation: checking destination...", "INFO");
        var post = _snapshots.Capture(_job.DestinationPath, _job.JobId, _log.Invoke);
        if (post is null) { _log("Post-sync validation: could not read destination", "WARN"); return; }
        var diff = SnapshotService.Compare(pre, post);
        if (diff.IsClean) _log("Post-sync validation: OK - " + diff.Summary, "OK");
        else
        {
            _log("Post-sync ANOMALY: " + diff.Summary, "ERROR");
            if (diff.HashMismatch.Count > 0)
            {
                _log("  Hash mismatches (possible corruption):", "ERROR");
                foreach (var f in diff.HashMismatch.Take(5)) _log("    - " + f, "ERROR");
            }
            if (diff.SizeAnomaly.Count > 0)
            {
                _log("  Size anomalies (possible truncation):", "ERROR");
                foreach (var f in diff.SizeAnomaly.Take(5)) _log("    - " + f, "ERROR");
            }
        }
    }
}
