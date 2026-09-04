using System.Windows;
using SyncGuard.ViewModels;

namespace SyncGuard.Views;

/// <summary>Modal override prompt. Returns true when the user approves the run.</summary>
public partial class ThresholdDialog : Window
{
    public ThresholdDialog(ThresholdRequest request)
    {
        InitializeComponent();
        try
        {
            var main = System.Windows.Application.Current?.MainWindow;
            if (main is not null) Icon = main.Icon;
        }
        catch { }
        SourceInitialized += (_, _) => MainWindow.EnableDarkTitleBar(this);
        DetailText.Text =
            $"Job '{request.JobName}' changed {request.Percent}% of files " +
            $"({request.Changed:N0} of {request.Total:N0}; limit is {request.Limit}%).\n\n" +
            "This may indicate an accidental mass overwrite or ransomware activity.\n" +
            "Run FreeFileSync anyway?";
    }

    private void Override_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Abort_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
