using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using AutoChartFill.Core;
using AutoChartFill.Excel;
using AutoChartFill.GameBridge;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoChartFill.App;

public sealed class MainViewModel : ObservableObject
{
    private const string BridgeConfigDirectory = "AutoChartSwitchV2";
    private const string JacketStagingPath = "AutoChartSwitchV2/Jackets";
    private readonly IGameEventSource _gameSource;
    private readonly IWorkbookRenderer _renderer;
    private readonly ICaptureStore _store;
    private readonly CaptureService _captureService;
    private readonly AppPersistence _persistence;
    private readonly object _eventLock = new();
    private Task _eventTail = Task.CompletedTask;
    private GameChartSnapshot? _currentChart;
    private string _statusText = "Starting...";
    private string _bridgeText = "Stopped";
    private string _workbookStatus = "No workbook configured.";
    private bool _isBusy;
    private bool _isMappingLocked;
    private bool _hasPendingRender;
    private int _capturedCount;

    public AppSettings Settings { get; }
    public ObservableCollection<string> Worksheets { get; } = [];
    public GameChartSnapshot? CurrentChart { get => _currentChart; private set => SetProperty(ref _currentChart, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string BridgeText { get => _bridgeText; private set => SetProperty(ref _bridgeText, value); }
    public string WorkbookStatus { get => _workbookStatus; private set => SetProperty(ref _workbookStatus, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool IsMappingLocked { get => _isMappingLocked; private set { if (SetProperty(ref _isMappingLocked, value)) OnPropertyChanged(nameof(IsConfigurationEnabled)); } }
    public bool IsConfigurationEnabled => !IsMappingLocked && !IsBusy;
    public bool HasPendingRender { get => _hasPendingRender; private set => SetProperty(ref _hasPendingRender, value); }
    public int CapturedCount { get => _capturedCount; private set => SetProperty(ref _capturedCount, value); }
    public string CurrentSong => CurrentChart is null ? "" : $"{CurrentChart.Title} - {CurrentChart.Artist}";
    public string CurrentDifficulty => CurrentChart is null ? "" : $"{DifficultyCodes.Normalize(string.IsNullOrWhiteSpace(CurrentChart.DifficultyCode) ? CurrentChart.RawDifficultyName : CurrentChart.DifficultyCode)} {CurrentChart.DifficultyNumber:g}".Trim();
    public string CurrentCharter => CurrentChart?.Charter ?? "";
    public string CurrentStats => CurrentChart is null ? "" :
        $"CHIP {CurrentChart.TechStats.Chip:g}   TECH {CurrentChart.TechStats.Tech:g}   STREAM {CurrentChart.TechStats.Stream:g}\n" +
        $"CHORD {CurrentChart.TechStats.Chord:g}   BURST {CurrentChart.TechStats.Burst:g}   GIMMICK {CurrentChart.TechStats.Gimmick:g}";
    public string PendingText => HasPendingRender ? "Pending workbook write" : "Workbook synchronized";

    public MainViewModel(AppSettings settings, IGameEventSource gameSource, IWorkbookRenderer renderer,
        ICaptureStore store, CaptureService captureService, AppPersistence persistence)
    {
        Settings = settings;
        _gameSource = gameSource;
        _renderer = renderer;
        _store = store;
        _captureService = captureService;
        _persistence = persistence;
        _gameSource.EventReceived += OnEventReceived;
        _gameSource.StatusChanged += (_, message) => RunOnUi(() => BridgeText = message);
    }

    public async Task InitializeAsync()
    {
        await RefreshWorkbookStateAsync();
        await WriteBridgeConfigAsync();
        try
        {
            await _gameSource.StartAsync();
            BridgeText = "Relay subscriber starting";
            StatusText = Settings.CaptureEnabled ? "Capture is active." : "Capture is paused.";
        }
        catch (Exception ex) { StatusText = $"Could not start the game bridge: {ex.Message}"; }

        if (HasPendingRender) await RetryAsync();
    }

    public async Task SelectWorkbookAsync(string path)
    {
        if (IsMappingLocked) return;
        Settings.Workbook.WorkbookPath = Path.GetFullPath(path);
        try
        {
            var names = await _renderer.GetWorksheetNamesAsync(path);
            Worksheets.Clear();
            foreach (var name in names) Worksheets.Add(name);
            if (!names.Contains(Settings.Workbook.WorksheetName, StringComparer.Ordinal))
                Settings.Workbook.WorksheetName = names.FirstOrDefault() ?? "";
            WorkbookStatus = $"Loaded {names.Count} worksheet{(names.Count == 1 ? "" : "s")}.";
            OnPropertyChanged(nameof(Settings));
        }
        catch (Exception ex) { WorkbookStatus = $"Could not open workbook: {ex.Message}"; }
    }

    public async Task SaveSettingsAsync()
    {
        var errors = Settings.Workbook.Validate();
        if (errors.Count > 0)
        {
            WorkbookStatus = string.Join(" ", errors);
            return;
        }
        await _persistence.SaveAsync(Settings);
        await WriteBridgeConfigAsync();
        if (IsMappingLocked)
        {
            await RetryAsync();
            return;
        }
        WorkbookStatus = IsMappingLocked ? $"Tracking {CapturedCount} captured rows. Mapping is locked." : "Configuration saved and ready.";
    }

    public async Task SetCaptureEnabledAsync(bool enabled)
    {
        Settings.CaptureEnabled = enabled;
        await _persistence.SaveAsync(Settings);
        StatusText = enabled ? "Capture is active." : "Capture is paused; selections are previewed only.";
    }

    public async Task RetryAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _captureService.RetryAsync(Settings.Workbook, FallbackJacketPath());
            ApplyState(result.State);
            StatusText = result.Message;
        }
        catch (Exception ex) { StatusText = $"Retry failed: {ex.Message}"; }
        finally { IsBusy = false; OnPropertyChanged(nameof(IsConfigurationEnabled)); }
    }

    public async Task ResetAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _captureService.ResetAsync(Settings.Workbook);
            ApplyState(new());
            StatusText = "Tracking was reset and the workbook mapping is unlocked.";
            WorkbookStatus = "Configuration is ready to edit.";
        }
        catch (Exception ex) { StatusText = $"Reset failed: {ex.Message}"; }
        finally { IsBusy = false; OnPropertyChanged(nameof(IsConfigurationEnabled)); }
    }

    public async Task RefreshWorksheetsAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.Workbook.WorkbookPath)) return;
        await SelectWorkbookAsync(Settings.Workbook.WorkbookPath);
    }

    private void OnEventReceived(object? sender, GameEventEnvelope envelope)
    {
        if (envelope.Chart is null) return;
        if (envelope.Kind is not (GameEventKind.ChartInfo or GameEventKind.Selection)) return;
        var shouldCapture = envelope.Kind == GameEventKind.ChartInfo;
        lock (_eventLock)
        {
            _eventTail = _eventTail.ContinueWith(
                _ => HandleSelectionAsync(envelope.Chart, shouldCapture), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task HandleSelectionAsync(GameChartSnapshot chart, bool shouldCapture)
    {
        RunOnUi(() =>
        {
            CurrentChart = chart;
            OnPropertyChanged(nameof(CurrentSong));
            OnPropertyChanged(nameof(CurrentDifficulty));
            OnPropertyChanged(nameof(CurrentCharter));
            OnPropertyChanged(nameof(CurrentStats));
        });
        if (!shouldCapture || !Settings.CaptureEnabled) return;
        try
        {
            var result = await _captureService.CaptureAsync(chart, Settings.Workbook, FallbackJacketPath());
            RunOnUi(() => { ApplyState(result.State); StatusText = result.Message; });
        }
        catch (Exception ex) { RunOnUi(() => StatusText = $"Capture failed: {ex.Message}"); }
    }

    private async Task RefreshWorkbookStateAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.Workbook.WorkbookPath) || !File.Exists(Settings.Workbook.WorkbookPath)) return;
        try
        {
            await SelectWorkbookAsync(Settings.Workbook.WorkbookPath);
            var state = await _store.LoadAsync(Settings.Workbook.WorkbookPath);
            ApplyState(state);
        }
        catch (Exception ex) { WorkbookStatus = $"Workbook setup needs attention: {ex.Message}"; }
    }

    private void ApplyState(CaptureState state)
    {
        CapturedCount = state.Records.Count;
        IsMappingLocked = state.Records.Count > 0;
        HasPendingRender = state.PendingRender;
        OnPropertyChanged(nameof(PendingText));
        WorkbookStatus = IsMappingLocked
            ? $"Tracking {CapturedCount} captured row{(CapturedCount == 1 ? "" : "s")}. Mapping is locked."
            : "No captured rows; mapping may be edited.";
    }

    private async Task WriteBridgeConfigAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.GamePath) || !Directory.Exists(Settings.GamePath)) return;
        try
        {
            var directory = Path.Combine(Settings.GamePath, BridgeConfigDirectory);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "bridge.ini"),
                $"[bridge]{Environment.NewLine}port=28745{Environment.NewLine}jacket_path={JacketStagingPath}{Environment.NewLine}");
        }
        catch (Exception ex) { StatusText = $"Could not write game bridge settings: {ex.Message}"; }
    }

    private static string FallbackJacketPath() => Path.Combine(AppContext.BaseDirectory, "Memories_Sacrifice_jacket.png");
    private static void RunOnUi(Action action) => System.Windows.Application.Current?.Dispatcher.Invoke(action);

    public async Task ShutdownAsync()
    {
        await _persistence.SaveAsync(Settings);
        await _gameSource.StopAsync();
        await _gameSource.DisposeAsync();
        Task pending;
        lock (_eventLock) pending = _eventTail;
        try { await pending; } catch { }
    }
}
