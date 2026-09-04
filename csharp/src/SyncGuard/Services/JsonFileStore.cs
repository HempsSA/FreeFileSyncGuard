using System.IO;
using System.Text.Json;

namespace SyncGuard.Services;

/// <summary>
/// Atomic JSON persistence with backup + corruption recovery.
/// Ports _atomic_write_json / _load_json_with_recovery from persistence.py.
/// </summary>
public static class JsonFileStore
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static JsonSerializerOptions Options => _json;

    public static void AtomicWrite<T>(string path, T data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        var backup = path + ".bak";
        var payload = JsonSerializer.Serialize(data, _json);
        try
        {
            File.WriteAllText(tmp, payload + "\n");
            if (File.Exists(path)) File.Copy(path, backup, overwrite: true);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    public static T LoadWithRecovery<T>(string path, Func<T> defaultFactory, string label) where T : class
    {
        if (!File.Exists(path)) return defaultFactory();
        try
        {
            var doc = JsonSerializer.Deserialize<T>(File.ReadAllText(path), _json);
            return doc ?? defaultFactory();
        }
        catch (Exception primary)
        {
            var backup = path + ".bak";
            if (File.Exists(backup))
            {
                try
                {
                    var doc = JsonSerializer.Deserialize<T>(File.ReadAllText(backup), _json);
                    if (doc is not null)
                    {
                        AppLog.Write($"{label} was corrupt; recovered from backup ({primary.Message})", "WARN");
                        return doc;
                    }
                }
                catch (Exception backupEx)
                {
                    AppLog.Write($"{label} and backup are unreadable: {backupEx.Message}", "ERROR");
                }
            }
            else
            {
                AppLog.Write($"{label} is unreadable: {primary.Message}", "ERROR");
            }
            try
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(path, $"{path}.corrupt-{stamp}");
                AppLog.Write($"Preserved unreadable file as {Path.GetFileName(path)}.corrupt-{stamp}", "WARN");
            }
            catch (Exception moveEx)
            {
                AppLog.Write($"Could not preserve unreadable file: {moveEx.Message}", "ERROR");
            }
            return defaultFactory();
        }
    }
}
