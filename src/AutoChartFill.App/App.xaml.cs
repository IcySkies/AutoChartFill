using System.Windows;
using AutoChartFill.Excel;
using AutoChartFill.GameBridge;

namespace AutoChartFill.App;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private bool _exiting;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private MainViewModel? _viewModel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, "SVC-AS.AutoChartFill", out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("Auto Chart Fill is already running.", "Auto Chart Fill", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var persistence = new AppPersistence();
        var settings = await persistence.LoadAsync();
        var renderer = new ClosedXmlWorkbookRenderer();
        var store = new JsonCaptureStore();
        var source = new RelayGameEventSource(settings.GamePath);
        _viewModel = new MainViewModel(settings, source, renderer, store, new(store, renderer), persistence);
        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        ConfigureTray(window);
        window.Show();
        await _viewModel.InitializeAsync();
    }

    private void ConfigureTray(MainWindow window)
    {
        var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Restore());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        _notifyIcon = new System.Windows.Forms.NotifyIcon { Icon = icon, Text = "Auto Chart Fill", Visible = true, ContextMenuStrip = menu };
        _notifyIcon.DoubleClick += (_, _) => Restore();
        window.Closing += (_, args) => { if (!_exiting) { args.Cancel = true; window.Hide(); } };
    }

    private void Restore()
    {
        MainWindow?.Show();
        if (MainWindow is not null) { MainWindow.WindowState = WindowState.Normal; MainWindow.Activate(); }
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        if (_viewModel is not null) await _viewModel.ShutdownAsync();
        MainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_notifyIcon is not null) { _notifyIcon.Visible = false; _notifyIcon.Dispose(); }
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
