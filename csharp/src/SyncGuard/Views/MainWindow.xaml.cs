using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using SyncGuard.ViewModels;
using App = System.Windows.Application;

namespace SyncGuard.Views;

/// <summary>
/// Main window: wires the threshold prompt + system-tray behavior
/// (minimize-to-tray, status-colored icon, Run All / Quit menu).
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly NotifyIcon _tray;
    private bool _quitting;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.ThresholdPrompt = req =>
            Dispatcher.Invoke(() =>
            {
                var dlg = new ThresholdDialog(req) { Owner = this };
                return dlg.ShowDialog() == true;
            });

        _tray = new NotifyIcon
        {
            Text = "SyncGuard",
            Visible = false,
        };
        _tray.Icon = BuildIcon("#8B949E");
        _tray.DoubleClick += (_, _) => Restore();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Run All Jobs", null, (_, _) => _ = vm.RunAllJobsCommand.ExecuteAsync(null));
        menu.Items.Add("Show", null, (_, _) => Restore());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsRunning) ||
                e.PropertyName == nameof(MainViewModel.Jobs))
                UpdateTrayIcon();
            else if (e.PropertyName == nameof(MainViewModel.LogLines))
                HookLogCollection();
        };
        // Watch each job's status (not just collection membership).
        vm.Jobs.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (JobItem j in e.NewItems)
                    j.PropertyChanged += OnJobPropertyChanged;
            if (e.OldItems is not null)
                foreach (JobItem j in e.OldItems)
                    j.PropertyChanged -= OnJobPropertyChanged;
            UpdateTrayIcon();
        };
        foreach (var j in vm.Jobs)
            j.PropertyChanged += OnJobPropertyChanged;

        vm.GuardianStateChanged += s =>
            Dispatcher.BeginInvoke(() =>
            {
                try { _tray.Text = s.Length > 120 ? s[..120] : s; } catch { }
            });

        HookLogCollection();
    }

    private void OnJobPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JobItem.Status))
            Dispatcher.BeginInvoke(UpdateTrayIcon);
    }

    private System.Collections.Specialized.INotifyCollectionChanged? _logHook;

    private void HookLogCollection()
    {
        if (_logHook is not null)
            _logHook.CollectionChanged -= OnLogCollectionChanged;
        if (DataContext is MainViewModel vm)
        {
            _logHook = vm.LogLines;
            _logHook.CollectionChanged += OnLogCollectionChanged;
        }
        ScrollLogToEnd();
    }

    private void OnLogCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => ScrollLogToEnd();

    private void ScrollLogToEnd()
    {
        if (LogBox.Items.Count == 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            try { LogBox.ScrollIntoView(LogBox.Items[LogBox.Items.Count - 1]); }
            catch { }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpdateTrayIcon()
    {
        string color = "#8B949E"; // idle
        var statuses = _vm.Jobs.Select(j => j.Status).ToList();
        if (statuses.Any(s => s == "RUNNING")) color = "#388BFD";
        else if (statuses.Any(s => s is "ERROR" or "ABORTED")) color = "#F85149";
        else if (statuses.Any(s => s == "WARN")) color = "#D29922";
        else if (statuses.Count > 0 && statuses.All(s => s == "OK")) color = "#3FB950";
        try
        {
            _tray.Icon?.Dispose();
            _tray.Icon = BuildIcon(color);
        }
        catch { }
    }

    private static System.Drawing.Icon BuildIcon(string htmlColor)
    {
        var logo = TrayLogo;
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            if (logo is not null)
                g.DrawImage(logo, 0, 0, 16, 16);
            else
            {
                // Fallback if the asset is missing: plain status dot.
                using var fallback = new SolidBrush(ColorTranslator.FromHtml(htmlColor));
                g.FillEllipse(fallback, 1, 1, 14, 14);
            }
            // Status dot overlay (bottom-right) preserves the old color coding.
            using var brush = new SolidBrush(ColorTranslator.FromHtml(htmlColor));
            using var outline = new Pen(System.Drawing.Color.FromArgb(13, 17, 23), 1.5f);
            g.FillEllipse(brush, 8.5f, 8.5f, 7, 7);
            g.DrawEllipse(outline, 8.5f, 8.5f, 7, 7);
        }
        // FromHandle borrows the HICON: clone the Icon, then free the handle
        // so we don't leak GDI objects on every status change.
        IntPtr hicon = bmp.GetHicon();
        try
        {
            using var tmp = System.Drawing.Icon.FromHandle(hicon);
            return (System.Drawing.Icon)tmp.Clone();
        }
        finally { DestroyIcon(hicon); }
    }

    private static System.Drawing.Bitmap? _trayLogo;

    /// <summary>App logo loaded once from WPF resources for tray compositing.</summary>
    private static System.Drawing.Bitmap? TrayLogo
    {
        get
        {
            if (_trayLogo is not null) return _trayLogo;
            try
            {
                var stream = App.GetResourceStream(
                    new Uri("pack://application:,,,/Assets/app.png"))?.Stream;
                if (stream is null) return null;
                using (stream) _trayLogo = new System.Drawing.Bitmap(stream);
                return _trayLogo;
            }
            catch { return null; }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableDarkTitleBar(this);
    }

    /// <summary>Asks Windows to draw a dark caption bar (Win10 20H1+).</summary>
    internal static void EnableDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            _tray.Visible = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting)
        {
            // Auto-minimize to tray like the Python build.
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            return;
        }
        _tray.Visible = false;
        _tray.Dispose();
        base.OnClosing(e);
    }

    private void Restore()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _tray.Visible = false;
    }

    private void Quit()
    {
        _quitting = true;
        Close();
        App.Current.Shutdown();
    }
}
