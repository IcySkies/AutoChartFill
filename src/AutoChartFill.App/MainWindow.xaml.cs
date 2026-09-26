using System.Windows;
using Microsoft.Win32;

namespace AutoChartFill.App;

public partial class MainWindow : Window
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public MainWindow() => InitializeComponent();

    private async void BrowseWorkbook_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Select Excel workbook", Filter = "Excel workbook (*.xlsx)|*.xlsx", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true && ViewModel is not null) await ViewModel.SelectWorkbookAsync(dialog.FileName);
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null) await ViewModel.SaveSettingsAsync(); }
    private async void Retry_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null) await ViewModel.RetryAsync(); }
    private async void CaptureChanged(object sender, RoutedEventArgs e) { if (ViewModel is not null) await ViewModel.SetCaptureEnabledAsync(ViewModel.Settings.CaptureEnabled); }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var answer = System.Windows.MessageBox.Show(this,
            "Remove all AutoChartFill-managed rows, merges, and jackets from this workbook and clear its tracking sidecar?",
            "Reset tracking", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) await ViewModel.ResetAsync();
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not System.Windows.Controls.Button { Tag: string difficulty }) return;
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var value = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        var viewModel = ViewModel;
        var colors = viewModel.Settings.Workbook.Colors;
        switch (difficulty)
        {
            case "OPN": colors.Opn = value; break;
            case "MID": colors.Mid = value; break;
            case "FIN": colors.Fin = value; break;
            case "ENC": colors.Enc = value; break;
            case "BKS": colors.Bks = value; break;
            default: colors.Shatter = value; break;
        }
        DataContext = null;
        DataContext = viewModel;
    }
}
