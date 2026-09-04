# SyncGuard — FreeFileSync Job Manager (C# / .NET / WPF)

Dark-mode desktop GUI for managing, scheduling, and safely running
multiple FreeFileSync sync jobs — with change-rate protection,
ransomware detection, destination snapshots, a folder guardian, and
the FreeFileSync exclude-filter tool.

![.NET 10](https://img.shields.io/badge/dotnet-10-blue)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)
![License](https://img.shields.io/badge/license-MIT-green)

> **Note:** the legacy Python implementation has been removed. All code
> lives under [`csharp/`](csharp/) and is covered by 26 xUnit tests.

## Quick start

```powershell
# Build + run (needs .NET 10 SDK)
dotnet run --project csharp/src/SyncGuard -c Release

# Tests
dotnet test csharp/SyncGuard.slnx -c Release

# Self-contained single-file EXE (~170 MB, no runtime needed)
dotnet publish csharp/src/SyncGuard/SyncGuard.csproj -c Release -r win-x64 `
  --self-contained /p:PublishSingleFile=true -o csharp/publish/win-x64-single
```

See [`csharp/README.md`](csharp/README.md) for full docs:
architecture, MSIX packaging, headless CLIs (`--rollback`,
`--ffs-exclude`), settings backup format, and migration notes.

## Features

- **Multi-job management** — create, duplicate, remove, and run FreeFileSync jobs
- **Change-rate protection** — blocks the sync when too many files changed, with an override dialog
- **Ransomware protection** — entropy sampling, 200+ suspicious extensions, composite anomaly score
- **Pre-sync snapshots + post-sync validation** of the destination, with rollback analysis CLI
- **Folder Guardian** — real-time rename-deny, delete logging, lockfile restore, auto-pause while FreeFileSync runs
- **Scheduling** — daily run times per job with live countdown
- **History** — last 200 runs per job in SQLite, with CSV export
- **Notifications** — ntfy.sh push alerts, plus settings export/import
- **FFS Excludes** — merge 34 Windows exclusion filters into `.ffs_batch`/`.ffs_gui` files
- **System tray** — minimize to tray with status-colored icon; single instance; dark theme throughout

## Data

Configuration is JSON, history and file index are SQLite — all under
`%LocalAppData%\SyncGuard\`. First launch auto-imports a legacy
`syncguard_jobs.json` and its cache if found next to the executable.

## License

MIT
