using SyncGuard.Models;

namespace SyncGuard.Services;

/// <summary>
/// Headless rollback CLI. Ports rollback.py:
/// SyncGuard --rollback --job "Job 1" [--list] [--latest] [-t stamp] [--dry-run]
/// </summary>
public static class RollbackRunner
{
    public static int Run(string[] args, JobStore store)
    {
        string? jobName = Get(args, "--job");
        bool list = args.Contains("--list");
        bool latest = args.Contains("--latest");
        string? stamp = Get(args, "--timestamp") ?? Get(args, "-t");
        bool dryRun = args.Contains("--dry-run");

        if (string.IsNullOrWhiteSpace(jobName))
        {
            Console.WriteLine("Usage: SyncGuard --rollback --job \"Job 1\" [--list] [--latest] [-t stamp] [--dry-run]");
            return 1;
        }
        var job = store.Jobs.FirstOrDefault(j => j.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
        if (job is null)
        {
            Console.WriteLine($"Job '{jobName}' not found.");
            Console.WriteLine("Available jobs:");
            foreach (var j in store.Jobs) Console.WriteLine("  - " + j.Name);
            return 1;
        }

        var stamps = SnapshotService.List(job.JobId);
        if (list)
        {
            if (stamps.Count == 0) { Console.WriteLine($"No snapshots found for '{job.Name}'."); return 0; }
            Console.WriteLine($"Snapshots for '{job.Name}':");
            foreach (var s in stamps)
            {
                var snap = SnapshotService.Load(job.JobId, s);
                Console.WriteLine(snap is null ? $"  {s}" : $"  {s}  |  {snap.TotalFiles} files  |  {snap.DestPath}");
            }
            return 0;
        }

        if (stamps.Count == 0) { Console.WriteLine($"No snapshots found for '{job.Name}'."); return 1; }
        string ts;
        if (latest) ts = stamps[^1];
        else if (stamp is not null)
        {
            if (!stamps.Contains(stamp))
            {
                Console.WriteLine($"Snapshot '{stamp}' not found. Available: {string.Join(", ", stamps)}");
                return 1;
            }
            ts = stamp;
        }
        else
        {
            Console.WriteLine($"Available snapshots for '{job.Name}':");
            for (int i = 0; i < stamps.Count; i++) Console.WriteLine($"  [{i + 1}] {stamps[i]}");
            Console.Write("Select snapshot number: ");
            if (!int.TryParse(Console.ReadLine(), out int choice) || choice < 1 || choice > stamps.Count)
            {
                Console.WriteLine("Invalid selection.");
                return 1;
            }
            ts = stamps[choice - 1];
        }

        var manifest = SnapshotService.Load(job.JobId, ts);
        if (manifest is null) { Console.WriteLine($"Failed to load snapshot '{ts}'."); return 1; }
        string dest = string.IsNullOrWhiteSpace(manifest.DestPath) ? job.DestinationPath : manifest.DestPath;
        if (string.IsNullOrWhiteSpace(dest)) { Console.WriteLine("No destination path configured for this job."); return 1; }

        Console.WriteLine($"\nSnapshot: {ts}\nDestination: {dest}\nFiles in snapshot: {manifest.TotalFiles}");
        var snapshots = new SnapshotService(job.MaxSnapshots);
        var current = snapshots.Capture(dest, job.JobId, (m, l) => Console.WriteLine($"  [{l}] {m}"));
        if (current is null) { Console.WriteLine("Could not read current destination."); return 1; }

        var diff = SnapshotService.Compare(manifest, current);
        Console.WriteLine($"\nComparison (snapshot vs current):\n  {diff.Summary}");
        foreach (var f in diff.HashMismatch.Take(10)) Console.WriteLine("    ! hash: " + f);
        foreach (var f in diff.SizeAnomaly.Take(10)) Console.WriteLine("    ! size: " + f);

        if (dryRun) { Console.WriteLine("\nDry run — no changes made."); return 0; }
        if (!diff.IsClean)
        {
            Console.Write("\nAnomalies detected. Proceed with rollback analysis? (y/N): ");
            if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) return 0;
        }
        var (missing, anomalies) = SnapshotService.AnalyzeRollback(manifest, dest, (m, l) => Console.WriteLine($"  [{l}] {m}"));
        Console.WriteLine("\nRollback analysis complete.");
        Console.WriteLine($"  Missing files to restore: {missing}\n  Size anomalies detected: {anomalies}");
        if (missing > 0)
            Console.WriteLine("\nTo restore missing files, use FreeFileSync with the original source path as the mirror source.");
        return 0;
    }

    private static string? Get(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}

/// <summary>Headless FFS-exclude CLI. Ports ffs_exclude_setup.py --cli.</summary>
public static class FfsExcludeRunner
{
    public static int Run(string[] args)
    {
        bool dryRun = args.Contains("--dry-run");
        bool create = args.Contains("--create");
        var positional = args.Where(a => !a.StartsWith("--") && a != "SyncGuard").ToList();

        Console.WriteLine("==============================================================");
        Console.WriteLine("  FreeFileSync Exclude Filter Setup (CLI)");
        Console.WriteLine("==============================================================");
        if (dryRun) Console.WriteLine("  [DRY RUN] No files will be modified.\n");

        if (create)
        {
            if (positional.Count == 0)
            {
                Console.WriteLine("Usage: SyncGuard --ffs-exclude --create <output.ffs_batch> <src> <dst>");
                return 1;
            }
            string output = positional[0];
            string source = positional.Count > 1 ? positional[1] : @"C:\Source";
            string target = positional.Count > 2 ? positional[2] : @"D:\Target";
            FfsExcludeService.CreateTemplate(output, source, target, dryRun: dryRun);
            Console.WriteLine($"Created: {output}\n  Source: {source}\n  Target: {target}\n  Excludes: {FfsExcludeService.WindowsExcludes.Count} patterns");
            return 0;
        }

        string scanDir = positional.Count > 0 ? Path.GetFullPath(positional[0]) : Directory.GetCurrentDirectory();
        Console.WriteLine($"Scanning: {scanDir}\nPatterns: {FfsExcludeService.WindowsExcludes.Count} exclude filters\n");
        var files = FfsExcludeService.ScanFolder(scanDir);
        if (files.Count == 0) { Console.WriteLine("No .ffs_batch or .ffs_gui files found."); return 0; }
        Console.WriteLine($"Found {files.Count} file(s):\n");
        int changed = 0, added = 0;
        foreach (var f in files)
        {
            Console.WriteLine($"  {Path.GetRelativePath(scanDir, f)}");
            var r = FfsExcludeService.ProcessFile(f, dryRun: dryRun);
            if (r.Error is not null) Console.WriteLine($"    ERROR: {r.Error}");
            else if (r.Changed) { changed++; added += r.Added; Console.WriteLine($"    + {r.Added} new filter(s)"); }
            else Console.WriteLine("    = up to date");
            Console.WriteLine();
        }
        Console.WriteLine("--------------------------------------------------------------");
        Console.WriteLine($"  Done: {changed} file(s) modified, {added} new filter(s)");
        return 0;
    }
}
