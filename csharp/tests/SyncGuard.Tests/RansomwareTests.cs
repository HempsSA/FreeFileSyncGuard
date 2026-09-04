using SyncGuard.Models;
using SyncGuard.Services;

namespace SyncGuard.Tests;

public sealed class RansomwareTests
{
    [Fact]
    public void UniformBytes_HaveZeroEntropy()
    {
        Assert.Equal(0.0, RansomwareService.ShannonEntropy(new byte[1024]));
    }

    [Fact]
    public void AllByteValues_HaveMaxEntropy()
    {
        var data = Enumerable.Range(0, 256).SelectMany(i => new[] { (byte)i }).ToArray();
        Assert.Equal(8.0, RansomwareService.ShannonEntropy(data), precision: 6);
    }

    [Fact]
    public void SuspiciousExtensions_AreDetected()
    {
        var files = new[] { @"C:\x\doc.lockbit", @"C:\x\note.txt", @"C:\x\a.akira" };
        var r = RansomwareService.DetectSuspiciousExtensions(files);
        Assert.True(r.IsSuspicious);
        Assert.Equal(2, r.Suspicious);
        Assert.True(r.SuspiciousExts.ContainsKey(".lockbit"));
    }

    [Fact]
    public void CleanExtensions_AreNotFlagged()
    {
        var files = new[] { @"C:\x\doc.txt", @"C:\x\photo.jpg" };
        var r = RansomwareService.DetectSuspiciousExtensions(files);
        Assert.False(r.IsSuspicious);
        Assert.Equal(0, r.Suspicious);
    }

    [Fact]
    public void PureChangeRate_CannotBlockAlone()
    {
        // Documented weighting: change contributes at most 40 pts,
        // so even 100% change stays below the default 60 block line.
        // (The per-job % threshold gate handles pure change-rate blocks.)
        var score = RansomwareService.ComputeAnomalyScore(
            changePct: 100, totalFiles: 10, changedFiles: 10, blockThreshold: 60);
        Assert.False(score.IsBlocked);
        Assert.Equal(40, score.Score);
        Assert.Contains(score.Reasons, r => r.Contains("High change rate"));
    }

    [Fact]
    public void LowChangeRate_DoesNotBlock()
    {
        var score = RansomwareService.ComputeAnomalyScore(
            changePct: 5, totalFiles: 100, changedFiles: 5, blockThreshold: 60);
        Assert.False(score.IsBlocked);
    }

    [Fact]
    public void EntropyPlusExtensions_Blocks()
    {
        var ent = new EntropyResult { Sampled = 10, HighEntropy = 5, IsSuspicious = true };
        var ext = new ExtensionResult
        {
            TotalChanged = 10, Suspicious = 3, IsSuspicious = true,
            SuspiciousExts = new Dictionary<string, int> { [".lockbit"] = 3 },
        };
        var score = RansomwareService.ComputeAnomalyScore(
            changePct: 50, totalFiles: 100, changedFiles: 50,
            entropyResult: ent, extensionResult: ext, blockThreshold: 60);
        Assert.True(score.IsBlocked); // 20 + 25 + 20 = 65 > 60
    }
}
