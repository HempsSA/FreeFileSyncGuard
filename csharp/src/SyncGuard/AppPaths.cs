namespace SyncGuard;

/// <summary>
/// Application identity, well-known paths and defaults.
/// Ports syncguard/constants.py. Data lives in %LocalAppData%\SyncGuard;
/// legacy files next to the executable are auto-imported on first run.
/// </summary>
public static class AppPaths
{
    public const string AppName = "SyncGuard";
    public const string AppVersion = "1.2.0";

    public const string DefaultFfsExe = @"C:\Program Files\FreeFileSync\FreeFileSync.exe";

    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncGuard");

    public static string ConfigFile => Path.Combine(DataDir, "syncguard_jobs.json");
    public static string SettingsFile => Path.Combine(DataDir, "syncguard_settings.json");
    public static string DatabaseFile => Path.Combine(DataDir, "syncguard.db");
    public static string LogFile => Path.Combine(DataDir, "logs", "syncguard-.log");
    public static string SnapshotsDir => Path.Combine(DataDir, "syncguard_cache", "snapshots");

    /// <summary>Legacy side-by-side files (Python build) used for one-time import.</summary>
    public static string LegacyConfigFile =>
        Path.Combine(AppContext.BaseDirectory, "syncguard_jobs.json");

    public static string LegacyCacheDir =>
        Path.Combine(AppContext.BaseDirectory, "syncguard_cache");

    public const int LogMaxLines = 500;

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.Combine(DataDir, "logs"));
        Directory.CreateDirectory(SnapshotsDir);
    }
}
