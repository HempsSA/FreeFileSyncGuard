using System.Runtime.InteropServices;
using System.Windows;
using SyncGuard.Services;
using SyncGuard.ViewModels;
using SyncGuard.Views;

namespace SyncGuard;

/// <summary>
/// Entry point: single-instance mutex, headless CLI modes
/// (--rollback, --ffs-exclude), otherwise the WPF shell.
/// </summary>
public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private MainViewModel? _vm;
    private AppDatabase? _db;

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        if (args.Contains("--rollback") || args.Contains("--ffs-exclude"))
        {
            AllocConsole();
            int code = RunCli(args);
            Shutdown(code);
            return;
        }

        // Local\ (per-session) needs no privileges; Global\ would throw
        // for non-admin users.
        _mutex = new Mutex(true, @"Local\SyncGuard_SingleInstance", out bool isNew);
        if (!isNew)
        {
            System.Windows.MessageBox.Show("SyncGuard is already running.\nCheck the system tray.",
                "SyncGuard", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            Shutdown(0);
            return;
        }

        AppLog.Init();
        try
        {
            _db = new AppDatabase();
            var store = new JobStore();
            var settings = new SettingsStore();
            _vm = new MainViewModel(store, settings, _db);
            MainWindow = new MainWindow(_vm);
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"SyncGuard failed to start:\n{ex.Message}",
                "SyncGuard", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static int RunCli(string[] args)
    {
        try
        {
            if (args.Contains("--rollback"))
            {
                var store = new JobStore();
                return RollbackRunner.Run(args, store);
            }
            if (args.Contains("--ffs-exclude"))
                return FfsExcludeRunner.Run(args.Where(a => a != "--ffs-exclude").ToArray());
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try { _vm?.Dispose(); } catch { }
        try { _db?.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { }
        AppLog.Close();
        base.OnExit(e);
    }
}
