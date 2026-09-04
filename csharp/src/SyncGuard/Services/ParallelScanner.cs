using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SyncGuard.Models;

namespace SyncGuard.Services;

/// <summary>
/// Parallel directory scanner with warm-cache directory skipping.
/// Ports ParallelScanner (Python used FindFirstFileExW; .NET's
/// Directory.EnumerateFileSystemEntries is backed by the same Win32
/// primitives and avoids per-file stat overhead for skipped dirs).
/// </summary>
public sealed class ParallelScanner : IDisposable
{
    private readonly string _root;
    private readonly FileIndexService _index;
    private readonly int _workers;
    private readonly List<string> _excludes;
    private readonly Action<ScanProgress> _progress;
    private readonly Action<string, string> _log;

    private readonly BlockingCollection<(string Path, double? Mtime)> _queue = new();
    private readonly ConcurrentDictionary<string, FileRecord> _newFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, double> _newDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<string> _changedPaths = new();
    private readonly List<string> _errors = new();
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private long _total, _changed;
    private int _skipped, _active;
    private volatile bool _aborted;
    private volatile bool _disposed;
    private string _lastDir = string.Empty;
    private long _lastEmit;

    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);

    public bool IsPaused => !_pauseGate.IsSet;

    public ParallelScanner(
        string root,
        FileIndexService index,
        int numWorkers = 0,
        IEnumerable<string>? excludePatterns = null,
        Action<ScanProgress>? progress = null,
        Action<string, string>? log = null)
    {
        _root = root;
        _index = index;
        _excludes = (excludePatterns ?? Enumerable.Empty<string>()).Select(p => p.ToLowerInvariant()).ToList();
        _progress = progress ?? (_ => { });
        _log = log ?? ((_, _) => { });
        _workers = numWorkers > 0 ? numWorkers
            : IsNetworkPath(root) ? 8 : Math.Max(2, Math.Min(4, Environment.ProcessorCount / 2));
    }

    public static bool IsNetworkPath(string path)
    {
        // Windows-only build: drive-letter and UNC handling, no OS guards.
        var p = path.Trim();
        if (p.StartsWith(@"\\") || p.StartsWith("//")) return true;
        if (p.Length >= 2 && p[1] == ':')
        {
            uint type = GetDriveType(p[..3]);
            return type == 4; // DRIVE_REMOTE
        }
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveType(string rootPathName);

    public void Stop()
    {
        _aborted = true;
        _cts.Cancel();
        _pauseGate.Set();
    }

    public void Pause() => _pauseGate.Reset();
    public void Resume() => _pauseGate.Set();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        _pauseGate.Set();
        try { _queue.CompleteAdding(); } catch { }
        _queue.Dispose();
        _pauseGate.Dispose();
        _cts.Dispose();
    }

    public ScanResult Run()
    {
        _clock.Restart();
        bool warm = _index.KnownTotal > 0;
        _log($"Parallel scan: {_root}  workers={_workers}" + (warm ? " [WARM]" : " [COLD]"), "INFO");

        Interlocked.Exchange(ref _active, 1);
        _queue.Add((_root, null));

        var tasks = Enumerable.Range(0, _workers)
            .Select(_ => Task.Run(ProcessLoop, _cts.Token))
            .ToArray();
        try { Task.WaitAll(tasks); }
        catch (AggregateException) { /* cancellation on Stop */ }

        Emit(force: true);
        if (_aborted) _log("Scan aborted by user.", "WARN");

        var files = _newFiles.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var dirs = _newDirs.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return new ScanResult
        {
            Aborted = _aborted,
            Total = _total,
            Changed = _changed,
            NewFiles = files,
            NewDirs = dirs,
            ChangedPaths = _changedPaths.ToList(),
            Errors = Errors.ToList(),
            Elapsed = _clock.Elapsed,
        };
    }

    public List<string> Errors => _errors;

    private void ProcessLoop()
    {
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try { _pauseGate.Wait(_cts.Token); }
                catch (OperationCanceledException) { break; }
                ProcessDir(item.Path, item.Mtime);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void FinishOne()
    {
        if (Interlocked.Decrement(ref _active) == 0)
            _queue.CompleteAdding();
    }

    private void ProcessDir(string dirPath, double? parentMtime)
    {
        if (_cts.IsCancellationRequested) { FinishOne(); return; }
        var localFiles = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        var localDirs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        long localTot = 0, localChg = 0;
        bool localSkip = false;
        var subdirs = new List<(string, double)>();
        try
        {
            double dirMtime;
            if (parentMtime.HasValue) dirMtime = parentMtime.Value;
            else
            {
                try { dirMtime = ToUnix(Directory.GetLastWriteTimeUtc(dirPath)); }
                catch { dirMtime = 0; }
            }

            string norm = _index.NormalizePath(dirPath);
            string nameLower = Path.GetFileName(norm).ToLowerInvariant();
            if (_excludes.Count > 0 && _excludes.Any(p => GlobMatch(nameLower, p)))
            {
                FinishOne();
                return;
            }
            localDirs[norm] = dirMtime;

            if (_index.DirUnchanged(norm, dirMtime))
            {
                foreach (var kv in _index.FilesInDirectory(norm))
                {
                    localTot++;
                    localFiles[kv.Key] = kv.Value;
                }
                localSkip = true;
                try
                {
                    foreach (var sub in EnumerateDirs(dirPath)) subdirs.Add(sub);
                }
                catch (Exception ex) { Fail($"Cannot list dir: {dirPath} - {ex.Message}"); }
            }
            else
            {
                List<(string Path, double Mtime, long Size)> rawFiles;
                try
                {
                    rawFiles = EnumerateFiles(dirPath);
                    foreach (var sub in EnumerateDirs(dirPath)) subdirs.Add(sub);
                }
                catch (Exception ex) { Fail($"Cannot list dir: {dirPath} - {ex.Message}"); FinishOne(); return; }

                var cachedHere = _index.FilesInDirectory(norm);
                foreach (var (fpath, mtime, size) in rawFiles)
                {
                    if (_excludes.Count > 0)
                    {
                        var fn = Path.GetFileName(fpath).ToLowerInvariant();
                        if (_excludes.Any(p => GlobMatch(fn, p))) continue;
                    }
                    string fn2 = _index.NormalizePath(fpath);
                    localTot++;
                    localFiles[fn2] = new FileRecord(mtime, size);
                    if (!cachedHere.TryGetValue(fn2, out var cv)) { localChg++; _changedPaths.Add(fn2); }
                    else if (Math.Abs(mtime - cv.Mtime) > 1.0 || (cv.Size >= 0 && size != cv.Size)) { localChg++; _changedPaths.Add(fn2); }
                }
            }
        }
        catch (Exception ex) { Fail($"Unexpected error scanning {dirPath}: {ex.Message}"); }
        finally
        {
            Interlocked.Add(ref _total, localTot);
            Interlocked.Add(ref _changed, localChg);
            if (localSkip) Interlocked.Increment(ref _skipped);
            foreach (var kv in localFiles) _newFiles[kv.Key] = kv.Value;
            foreach (var kv in localDirs) _newDirs[kv.Key] = kv.Value;
            _lastDir = dirPath;
            foreach (var sd in subdirs)
            {
                Interlocked.Increment(ref _active);
                try { _queue.Add(sd); }
                catch (InvalidOperationException) { Interlocked.Decrement(ref _active); }
            }
            FinishOne();
            Emit();
        }
    }

    private void Fail(string message)
    {
        lock (_errors) _errors.Add(message);
        _log(message, "ERROR");
    }

    private static List<(string Path, double Mtime, long Size)> EnumerateFiles(string dir)
    {
        var list = new List<(string, double, long)>();
        var opts = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var fp in Directory.EnumerateFiles(dir, "*", opts))
        {
            try
            {
                var info = new FileInfo(fp);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (!info.Attributes.HasFlag(FileAttributes.Directory))
                    list.Add((fp, ToUnix(info.LastWriteTimeUtc), info.Length));
            }
            catch { /* race: file vanished */ }
        }
        return list;
    }

    private static IEnumerable<(string Path, double Mtime)> EnumerateDirs(string dir)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var sub in Directory.EnumerateDirectories(dir, "*", opts))
        {
            double mtime;
            try { mtime = ToUnix(Directory.GetLastWriteTimeUtc(sub)); }
            catch { continue; }
            yield return (sub, mtime);
        }
    }

    private void Emit(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastEmit) < 400) return;
        Interlocked.Exchange(ref _lastEmit, now);
        long total = Interlocked.Read(ref _total);
        double secs = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
        _progress(new ScanProgress
        {
            Scanned = total,
            Changed = Interlocked.Read(ref _changed),
            TotalHint = _index.KnownTotal,
            SkippedDirs = _skipped,
            ActiveDirs = Math.Max(0, _active),
            CurrentDir = _lastDir,
            FilesPerSec = total / secs,
            Warm = _index.KnownTotal > 0,
            Engine = "parallel",
            Workers = _workers,
        });
    }

    private static double ToUnix(DateTime dt) => new DateTimeOffset(dt).ToUnixTimeSeconds();

    private static bool GlobMatch(string name, string pattern)
    {
        // Case-insensitive glob supporting * and ? (Python fnmatch equivalent).
        int ni = 0, pi = 0, star = -1, mark = 0;
        while (ni < name.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || pattern[pi] == name[ni]))
            {
                ni++; pi++;
            }
            else if (pi < pattern.Length && pattern[pi] == '*')
            {
                star = pi++; mark = ni;
            }
            else if (star != -1)
            {
                pi = star + 1; ni = ++mark;
            }
            else return false;
        }
        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi == pattern.Length;
    }
}
