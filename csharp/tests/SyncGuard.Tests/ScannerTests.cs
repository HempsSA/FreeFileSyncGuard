using SyncGuard.Models;
using SyncGuard.Services;

namespace SyncGuard.Tests;

public sealed class ScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sgtest_" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly AppDatabase _db;

    public ScannerTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(_root, "sub", "b.txt"), "world");
        _dbPath = Path.Combine(Path.GetTempPath(), "sgtestdb_" + Guid.NewGuid().ToString("N") + ".db");
        _db = new AppDatabase(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void FirstScan_FindsAllFiles()
    {
        var index = new FileIndexService(_db, _root);
        var scanner = new ParallelScanner(_root, index, numWorkers: 2);
        var result = scanner.Run();
        Assert.False(result.Aborted);
        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Changed); // cold: everything is new
    }

    [Fact]
    public void ChangeGuard_FirstRun_CreatesBaseline()
    {
        var job = new JobConfig { Name = "T", SourcePath = _root, Threshold = 40 };
        var guard = new ChangeGuardService(job, new FileIndexService(_db, _root), new SnapshotService());
        var outcome = guard.CheckAndRun();
        Assert.Equal("WARN", outcome.Status); // baseline created, FFS not launched
        Assert.Equal(2, outcome.Total);
    }

    [Fact]
    public void ChangeGuard_SecondRun_NoChanges_MissingFfsConfig()
    {
        var job = new JobConfig { Name = "T", SourcePath = _root, Threshold = 40 };
        var index = new FileIndexService(_db, _root);
        var guard = new ChangeGuardService(job, index, new SnapshotService());
        Assert.Equal("WARN", guard.CheckAndRun().Status); // baseline

        // Second run: 0% change, but no FFS exe/batch configured -> ERROR.
        var guard2 = new ChangeGuardService(job,
            new FileIndexService(_db, _root), new SnapshotService());
        var outcome = guard2.CheckAndRun();
        Assert.Equal("ERROR", outcome.Status);
        Assert.Equal(0.0, outcome.Percent);
    }

    [Fact]
    public void ChangeGuard_MassChange_BlockedByThreshold()
    {
        var job = new JobConfig
        {
            Name = "T", SourcePath = _root, Threshold = 10,
            RansomwareProtection = false,
            FfsExe = @"C:\nonexistent\ffs.exe", BatchFile = @"C:\nonexistent\job.ffs_batch",
        };
        var index = new FileIndexService(_db, _root);
        Assert.Equal("WARN", new ChangeGuardService(job, index, new SnapshotService()).CheckAndRun().Status);

        // Change every file (+add one): 100% change, no override callback -> ABORTED.
        // NOTE: in-place content edits don't bump the directory mtime on NTFS,
        // so (like the Python warm cache) we force dir mtimes past the 1s tolerance.
        File.WriteAllText(Path.Combine(_root, "a.txt"), "CHANGED");
        File.WriteAllText(Path.Combine(_root, "sub", "b.txt"), "CHANGED");
        File.WriteAllText(Path.Combine(_root, "c.txt"), "brand new file");
        Directory.SetLastWriteTimeUtc(_root, DateTime.UtcNow.AddSeconds(5));
        Directory.SetLastWriteTimeUtc(Path.Combine(_root, "sub"), DateTime.UtcNow.AddSeconds(5));
        var outcome = new ChangeGuardService(job,
            new FileIndexService(_db, _root), new SnapshotService()).CheckAndRun();
        Assert.Equal("ABORTED", outcome.Status);
        Assert.True(outcome.Percent > 10);
    }

    [Fact]
    public void ExcludedPatterns_AreSkipped()
    {
        File.WriteAllText(Path.Combine(_root, "skip.tmp"), "x");
        var job = new JobConfig
        {
            Name = "T", SourcePath = _root, ExcludePatterns = new List<string> { "*.tmp" },
        };
        var index = new FileIndexService(_db, _root);
        var scanner = new ParallelScanner(_root, index, 2, job.ExcludePatterns);
        var result = scanner.Run();
        Assert.Equal(2, result.Total);
    }

    [Fact]
    public void ColdScan_PopulatesChangedPaths_AndErrors()
    {
        var index = new FileIndexService(_db, _root);
        using var scanner = new ParallelScanner(_root, index, numWorkers: 2);
        var result = scanner.Run();
        Assert.Equal(2, result.ChangedPaths.Count);
        Assert.Empty(result.Errors);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public void Index_FindsFilesDirectlyInDriveRoot()
    {
        // Regression: FilesInDirectory compared a trimmed root ("C:")
        // against an untrimmed dirname ("C:\") and missed root files.
        var index = new FileIndexService(_db, _root);
        index.Save(
            new Dictionary<string, FileRecord> { [@"C:\sg_probe_file.txt"] = new FileRecord(1, 2) },
            new Dictionary<string, double> { [@"C:\"] = 1 });
        var inRoot = index.FilesInDirectory(@"C:\");
        Assert.Contains(inRoot, kv => kv.Key.EndsWith("SG_PROBE_FILE.TXT"));
    }
}
