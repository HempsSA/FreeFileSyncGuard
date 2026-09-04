# SyncGuard — C# / .NET / WPF Port

**FreeFileSync Job Manager** — MVVM WPF desktop app with dark theme,
change-rate guard, ransomware protection, scheduling, folder guardian,
ntfy notifications, and the FreeFileSync exclude-filter tool.

This is a complete port of the Python (`customtkinter`) implementation
in the repository root. Behavioural parity is covered by
`tests/SyncGuard.Tests` (18 tests, all passing).

| Requirement | Implementation |
|---|---|
| Language / Runtime | C# on .NET 10 (`net10.0-windows`) |
| Platform | **Windows-only** — Windows 10/11 x64 (dark title bar needs Win10 20H1+) |
| Interface | WPF, MVVM (`CommunityToolkit.Mvvm`) |
| Theme | Dark (GitHub-dark palette from `constants.py`) |
| Distribution | Self-contained single-file EXE; MSIX-ready |
| Configuration | JSON (`%LocalAppData%\SyncGuard\syncguard_jobs.json`, schema-compatible with the Python build) |
| History + indexing | SQLite (`syncguard.db`, WAL mode) |
| Logging | Serilog (rolling daily file) + in-app activity log |

## Projects

```
csharp/
├── SyncGuard.slnx
├── src/SyncGuard/            # WPF app (MVVM)
│   ├── AppPaths.cs            # ports constants.py (paths, defaults)
│   ├── Models/                # JobConfig, NotificationSettings, ScanModels
│   ├── Services/
│   │   ├── AppLog.cs          # Serilog + in-memory ring for the UI log
│   │   ├── JsonFileStore.cs   # atomic JSON + .bak corruption recovery
│   │   ├── Stores.cs          # JobStore, SettingsStore
│   │   ├── AppDatabase.cs     # SQLite schema (history + file index)
│   │   ├── ScanHistoryService.cs  # last 200 runs/job, CSV export
│   │   ├── FileIndexService.cs    # warm mtime cache (ports ScanCache)
│   │   ├── ParallelScanner.cs     # parallel scanner (ports scanner.py)
│   │   ├── RansomwareService.cs   # entropy + 200 extensions + anomaly score
│   │   ├── SnapshotService.cs     # destination manifests + compare
│   │   ├── ChangeGuardService.cs  # scan → decide → launch FreeFileSync
│   │   ├── GuardianService.cs     # FileSystemWatcher rename-deny (ports guardian.py)
│   │   ├── NotificationsAndScheduler.cs  # ntfy client + HH:MM scheduler
│   │   ├── FfsExcludeService.cs   # ports ffs_exclude_setup.py (34 filters)
│   │   └── CliRunners.cs      # --rollback / --ffs-exclude headless CLIs
│   ├── ViewModels/MainViewModel.cs  # ports SyncGuardApp
│   ├── Views/                 # MainWindow, ThresholdDialog
│   └── Themes/DarkTheme.xaml  # GitHub-dark styles
└── tests/SyncGuard.Tests/     # xUnit parity tests
```

## Build & run

```powershell
# Build
dotnet build csharp/SyncGuard.slnx -c Release

# Run (dev)
dotnet run --project csharp/src/SyncGuard

# Tests
dotnet test csharp/SyncGuard.slnx -c Release
```

## Distribution

```powershell
# Self-contained single-file EXE (~170 MB, includes .NET + SQLite native)
dotnet publish csharp/src/SyncGuard/SyncGuard.csproj -c Release -r win-x64 `
  --self-contained /p:PublishSingleFile=true -o publish/win-x64-single

# Framework-dependent single file (small, needs .NET 10 runtime)
dotnet publish csharp/src/SyncGuard/SyncGuard.csproj -c Release -r win-x64 `
  --no-self-contained /p:PublishSingleFile=true -o publish/win-x64-fd
```

**MSIX:** add a *Windows Application Packaging Project* in Visual Studio,
reference the `SyncGuard` project output, and build the `.msix` — no code
changes needed (all data paths already use `%LocalAppData%`).

## Application icon

`src/SyncGuard/Assets/` holds `app.ico` (exe icon, 16/32/48/256) and
`app.png` (window + tray base). The tray icon composites the logo with a
small status-colored dot (green/blue/yellow/red). To replace the logo,
drop a new source image in and re-run:

```powershell
csharp/tools/Build-Icon.ps1 -InputJpg C:\path\logo.jpg -OutDir csharp/src/SyncGuard/Assets
```

## Headless CLIs (ports of `rollback.py` / `ffs_exclude_setup.py --cli`)
```powershell
SyncGuard.exe --rollback --job "Job 1" --list
SyncGuard.exe --rollback --job "Job 1" --latest [--dry-run]
SyncGuard.exe --rollback --job "Job 1" -t 2026-08-31_143000
SyncGuard.exe --ffs-exclude "D:\Backups" [--dry-run]
SyncGuard.exe --ffs-exclude --create sync.ffs_batch "C:\Source" "D:\Target"
```

## Settings backup

The ⬇ Export / ⬆ Import buttons under the job list write/read a
versioned `*.syncguard.json` file containing all jobs plus notification
settings. Import merges (colliding job IDs are re-assigned) and also
accepts a raw `syncguard_jobs.json` array.

New jobs — and jobs with no saved patterns — show the 29 common Windows
exclude patterns (`*.tmp`, `$Recycle.Bin`, `Thumbs.db`, `*.log`, `.git`,
…) as a starting point; **Windows defaults** in the Config tab merges
them into the current list without touching custom entries.

## Migration from the Python build
On first launch the app auto-imports, side-by-side compatible:

- `syncguard_jobs.json` (same schema) → `%LocalAppData%\SyncGuard\`
- `syncguard_cache/history_*.json` → SQLite `scan_history`
- `syncguard_cache/cache_*.json` → SQLite `file_index` / `dir_index`

## Notes on parity

- Scanner warm-skip, 1 s mtime tolerance, reparse-point skipping,
  auto worker count (local vs network), and the same anomaly weights
  (40/25/20/15/10, block at 60) are preserved.
- First scan of a source creates a baseline and does **not** launch
  FreeFileSync — identical to the Python build.
- Threshold override is a modal WPF dialog (`ThresholdDialog`);
  scheduled/headless runs without a prompt deny the override.
- Same-source guard prevents two jobs scanning one source concurrently.
- Scans can be paused/resumed/aborted from the progress row; aborts are
  recorded as `ABORTED` and the trusted cache is retained.
- The guardian supports manual pause plus auto-pause while FreeFileSync
  runs (polled every 5 s), and reverts folder renames too.
- Snapshots use a resilient walk: unreadable subtrees are skipped rather
  than voiding the whole manifest.
