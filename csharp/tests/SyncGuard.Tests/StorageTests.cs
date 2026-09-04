using SyncGuard.Models;
using SyncGuard.Services;

namespace SyncGuard.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sgstoretest_" + Guid.NewGuid().ToString("N"));

    public StorageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void JobStore_RoundTrips_AllFields()
    {
        var path = Path.Combine(_dir, "jobs.json");
        var store = new JobStore(path);
        store.Clear();
        var job = new JobConfig
        {
            Name = "My Job", SourcePath = @"D:\src", Threshold = 25,
            ScheduleTimes = new List<string> { "02:00" },
            CustomExtensions = new List<string> { ".evil" },
        };
        store.Add(job);

        var reloaded = new JobStore(path);
        var loaded = Assert.Single(reloaded.Jobs);
        Assert.Equal("My Job", loaded.Name);
        Assert.Equal(25, loaded.Threshold);
        Assert.Equal(new[] { "02:00" }, loaded.ScheduleTimes.ToArray());
        Assert.Equal(job.JobId, loaded.JobId);

        reloaded.Clear();
        Assert.Empty(reloaded.Jobs);
        Assert.Empty(new JobStore(path).Jobs);
    }

    [Fact]
    public void JobStore_RecoversFromCorruptPrimary()
    {
        var path = Path.Combine(_dir, "jobs2.json");
        var store = new JobStore(path);
        store.Clear();
        store.Add(new JobConfig { Name = "Good" });
        // Corrupt the primary; the .bak from the previous save is valid.
        store.Add(new JobConfig { Name = "Good2" });
        File.WriteAllText(path, "{ not json !!!");

        var reloaded = new JobStore(path);
        Assert.NotEmpty(reloaded.Jobs);
    }

    [Fact]
    public void History_AddGetPrune_Csv()
    {
        var dbPath = Path.Combine(_dir, "hist.db");
        using var db = new AppDatabase(dbPath);
        var history = new ScanHistoryService(db);
        for (int i = 0; i < 5; i++)
            history.Add(new ScanRecord
            {
                JobId = "j1", Timestamp = DateTime.Now, Status = "OK",
                Total = 100, Changed = i, Percent = i, TriggeredBy = "manual",
            });
        var records = history.Get("j1");
        Assert.Equal(5, records.Count);
        Assert.Equal(4, records[0].Changed); // newest first
        var csv = history.ExportCsv("j1");
        Assert.Contains("timestamp,status,total", csv);
        history.Clear("j1");
        Assert.Empty(history.Get("j1"));
    }

    [Fact]
    public void Snapshot_CaptureCompare_DetectsChanges()
    {
        var dest = Path.Combine(_dir, "dest");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(dest, "gone.txt"), "gone");

        var svc = new SnapshotService();
        var before = svc.Capture(dest, "job1", sampleCount: 10);
        Assert.NotNull(before);
        Assert.Equal(2, before.TotalFiles);

        File.Delete(Path.Combine(dest, "gone.txt"));
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "keep-CHANGED!!");
        var after = svc.Capture(dest, "job1", sampleCount: 10);
        Assert.NotNull(after);

        var diff = SnapshotService.Compare(before, after);
        Assert.False(diff.IsClean);
        Assert.Equal(1, diff.Removed);
        Assert.Contains("removed", diff.Summary);
    }

    [Fact]
    public void FfsExclude_MergesFilters_Idempotently()
    {
        var file = Path.Combine(_dir, "sync.ffs_batch");
        FfsExcludeService.CreateTemplate(file, @"C:\src", @"D:\dst");
        var r1 = FfsExcludeService.ProcessFile(file, dryRun: true);
        Assert.False(r1.Changed); // template already contains all filters

        var custom = Path.Combine(_dir, "custom.ffs_batch");
        FfsExcludeService.CreateTemplate(custom, @"C:\src", @"D:\dst",
            filters: new[] { "*", }, dryRun: false);
        var r2 = FfsExcludeService.ProcessFile(custom);
        Assert.True(r2.Changed);
        Assert.Equal(FfsExcludeService.WindowsExcludes.Count, r2.Added);
        var r3 = FfsExcludeService.ProcessFile(custom);
        Assert.False(r3.Changed); // second run: nothing to add
        Assert.True(File.Exists(custom + ".bak"));
    }

    [Fact]
    public void Scheduler_ParsesAndFindsNextRun()
    {
        Assert.True(SchedulerService.TryParseHm("02:00", out int h, out int m));
        Assert.Equal((2, 0), (h, m));
        Assert.False(SchedulerService.TryParseHm("25:00", out _, out _));
        Assert.False(SchedulerService.TryParseHm("nope", out _, out _));

        var job = new JobConfig { ScheduleTimes = new List<string> { "00:00" } };
        var next = SchedulerService.NextRun(job, new DateTime(2026, 1, 1, 12, 0, 0));
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0), next);
    }
}
