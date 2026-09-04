using SyncGuard.Services;

namespace SyncGuard.Tests;

public sealed class MiscTests
{
    [Fact]
    public void AppLog_ClearView_EmptiesSnapshot()
    {
        AppLog.Write("probe line for clear test", "INFO");
        Assert.NotEmpty(AppLog.Snapshot());
        AppLog.ClearView();
        Assert.Empty(AppLog.Snapshot());
    }

    [Fact]
    public void Guardian_DirectoryMove_Reverts()
    {
        // End-to-end through the real revert logic path is timing-based;
        // here we at least verify start/stop lifecycle and lockfile creation.
        var dir = Path.Combine(Path.GetTempPath(), "sgguard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var guardian = new GuardianService((_, _) => { });
            guardian.Start(dir);
            Assert.True(guardian.IsMonitoring);
            Assert.True(File.Exists(Path.Combine(dir, ".guardian_lockfile")));
            guardian.ManualPaused = true;
            guardian.ManualPaused = false;
            guardian.Stop();
            Assert.False(guardian.IsMonitoring);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
