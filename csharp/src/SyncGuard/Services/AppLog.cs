using Serilog;

namespace SyncGuard.Services;

/// <summary>
/// Serilog bootstrap (rolling file) + in-memory ring buffer that the
/// WPF activity-log view binds to. Replaces ad-hoc print()/diagnostic().
/// </summary>
public static class AppLog
{
    private static readonly object _gate = new();
    private static readonly Queue<LogLine> _lines = new();
    public const int MaxLines = AppPaths.LogMaxLines;

    public static event Action? LinesChanged;

    public static void Init()
    {
        AppPaths.EnsureCreated();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(AppPaths.LogFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    public static void Write(string message, string level = "INFO")
    {
        try
        {
            switch (level.ToUpperInvariant())
            {
                case "ERROR": Log.Error("{Msg}", message); break;
                case "WARN": Log.Warning("{Msg}", message); break;
                case "OK": Log.Information("OK: {Msg}", message); break;
                default: Log.Information("{Msg}", message); break;
            }
        }
        catch { /* never throw from logging */ }

        lock (_gate)
        {
            _lines.Enqueue(new LogLine(DateTime.Now, level.ToUpperInvariant(), message));
            while (_lines.Count > MaxLines) _lines.Dequeue();
        }
        LinesChanged?.Invoke();
    }

    public static IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate) return _lines.ToList();
    }

    /// <summary>Clears the in-app view (the Serilog file log is retained).</summary>
    public static void ClearView()
    {
        lock (_gate) _lines.Clear();
        LinesChanged?.Invoke();
    }

    public static void Close() => Log.CloseAndFlush();
}

public sealed record LogLine(DateTime Time, string Level, string Message)
{
    public override string ToString() => $"[{Time:HH:mm:ss}] [{Level}] {Message}";
}
