using SyncGuard.Models;
using SyncGuard.Services;

namespace SyncGuard.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sgbackuptest_" + Guid.NewGuid().ToString("N"));

    public BackupTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ExportImport_RoundTrips_JobsAndNotifications()
    {
        var file = Path.Combine(_dir, "backup.syncguard.json");
        var jobs = new List<JobConfig>
        {
            new() { Name = "A", SourcePath = @"D:\a", Threshold = 25 },
            new() { Name = "B", SourcePath = @"D:\b" },
        };
        var notif = new NotificationSettings { Enabled = true, Topic = "my-topic" };

        await SettingsBackupService.ExportAsync(file, jobs, notif);
        Assert.True(File.Exists(file));

        var (imported, importedNotif) = await SettingsBackupService.ImportAsync(
            file, existingJobIds: new[] { jobs[0].JobId });
        Assert.Equal(2, imported.Count);
        Assert.Equal("A", imported[0].Name);
        Assert.Equal(25, imported[0].Threshold);
        // Colliding ID must be re-assigned; the other kept.
        Assert.NotEqual(jobs[0].JobId, imported[0].JobId);
        Assert.Equal(jobs[1].JobId, imported[1].JobId);
        Assert.NotNull(importedNotif);
        Assert.True(importedNotif.Enabled);
        Assert.Equal("my-topic", importedNotif.Topic);
    }

    [Fact]
    public async Task Import_AcceptsBareJobArray()
    {
        var file = Path.Combine(_dir, "jobs.json");
        await File.WriteAllTextAsync(file, """[{"name":"Solo","threshold":10}]""");
        var (jobs, notif) = await SettingsBackupService.ImportAsync(file);
        var job = Assert.Single(jobs);
        Assert.Equal("Solo", job.Name);
        Assert.Equal(10, job.Threshold);
        Assert.False(string.IsNullOrWhiteSpace(job.JobId));
        Assert.Null(notif);
    }

    [Fact]
    public async Task Import_RejectsGarbage()
    {
        var file = Path.Combine(_dir, "bad.json");
        await File.WriteAllTextAsync(file, "{ not json");
        await Assert.ThrowsAsync<InvalidDataException>(() => SettingsBackupService.ImportAsync(file));
        await Assert.ThrowsAsync<InvalidDataException>(() => SettingsBackupService.ImportAsync(Path.Combine(_dir, "missing.json")));
    }

    [Fact]
    public void DefaultExcludes_ContainCommonWindowsEntries()
    {
        var d = JobConfig.DefaultExcludePatterns;
        Assert.Contains("*.tmp", d);
        Assert.Contains("$Recycle.Bin", d);
        Assert.Contains("*.log", d);
        Assert.Contains("Thumbs.db", d);
        Assert.Contains("desktop.ini", d);
        Assert.Contains(".git", d);
        Assert.Contains("syncguard_cache", d);
        Assert.True(d.Count >= 25);
    }
}
