using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using PhoneBackup.Core;

namespace PhoneBackup.Desktop;

public sealed class DeviceViewModel
{
    public DeviceViewModel(DeviceInventory inventory) => Inventory = inventory;
    public DeviceInventory Inventory { get; }
    public string DisplayName => $"{Inventory.Model}  •  {Inventory.Device}";
    public string RomLine => $"Android {Inventory.AndroidVersion} (SDK {Inventory.Sdk})  •  {Inventory.Fingerprint}";
    public string RootLabel => Inventory.Root == RootState.Unavailable
        ? LocalizationManager.T("Root проверяется Android Agent")
        : $"Root: {Inventory.Root}";
    public string TransportLabel => string.Join(" + ", Inventory.Transports.Select(x => x.Kind).Distinct());
    public string StorageLabel => Inventory.DataTotalBytes == 0
        ? LocalizationManager.T("Хранилище: неизвестно")
        : $"{LocalizationManager.T("Свободно")} {FormatBytes(Inventory.DataAvailableBytes)} / {FormatBytes(Inventory.DataTotalBytes)}";
    public string PreferredSerial => Inventory.Transports.First(x => x.IsPreferred).AdbSerial;

    private static string FormatBytes(long value)
    {
        var gib = value / 1024d / 1024d / 1024d;
        return $"{gib:0.#} {LocalizationManager.T("ГБ")}";
    }
}

public sealed class SnapshotViewModel
{
    public SnapshotViewModel(SnapshotManifest manifest) => Manifest = manifest;
    public SnapshotManifest Manifest { get; }
    private int AppCount => Manifest.Components
        .Where(x => x.Package is not null)
        .Select(x => x.Package!.PackageName)
        .Distinct(StringComparer.Ordinal)
        .Count();
    public string Title =>
        $"{Manifest.CreatedAtUtc.ToLocalTime():g} • {AppCount} {LocalizationManager.T("приложений")}";
    public string Details =>
        $"{(Manifest.Purpose == "safety" ? "Safety snapshot" : Manifest.Mode.ToString())} • " +
        $"{Manifest.Components.Count} компонентов • " +
        $"{FormatBytes(Manifest.Components.Sum(x => x.PlainBytes))} • {Manifest.SnapshotId[..12]}";

    private static string FormatBytes(long value) =>
        value >= 1024L * 1024 * 1024
            ? $"{value / 1024d / 1024 / 1024:0.##} {LocalizationManager.T("ГБ")}"
            : $"{value / 1024d / 1024:0.##} {LocalizationManager.T("МБ")}";
}

public sealed class PackageSelectionViewModel(PackageSnapshot package) : INotifyPropertyChanged
{
    private bool _isSelected = true;
    public PackageSnapshot Package { get; } = package;
    public string PackageName => Package.PackageName;
    public string Label => string.IsNullOrWhiteSpace(Package.Label) ? Package.PackageName : Package.Label;
    public string Size =>
        $"{Package.ApkArtifacts.Sum(x => x.Size) / 1024d / 1024:0.##} {LocalizationManager.T("МБ")}";
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class OperationProgressItemViewModel : INotifyPropertyChanged
{
    private string _stage = string.Empty;
    private string _item = string.Empty;
    private RestoreItemState _state;
    private string _progress = string.Empty;
    private string _message = string.Empty;

    public required string Key { get; init; }
    public string Stage { get => _stage; set => Set(ref _stage, value); }
    public string Item { get => _item; set => Set(ref _item, value); }
    public RestoreItemState State { get => _state; set { if (Set(ref _state, value)) Raise(nameof(StateLabel)); } }
    public string StateLabel => State switch
    {
        RestoreItemState.Pending => "Ожидает",
        RestoreItemState.Running => "Выполняется",
        RestoreItemState.NeedsUser => "Нужно действие",
        RestoreItemState.Succeeded => "Готово",
        RestoreItemState.Skipped => "Пропущено",
        RestoreItemState.Failed => "Ошибка",
        RestoreItemState.RolledBack => "Откат выполнен",
        _ => State.ToString()
    };
    public string Progress { get => _progress; set => Set(ref _progress, value); }
    public string Message { get => _message; set => Set(ref _message, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AdbService _adb;
    private readonly IPhoneMediaImportCoordinator _iPhoneMedia = new();
    private readonly Dictionary<string, string> _mediaTransports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationProgressItemViewModel> _operationItemsByKey =
        new(StringComparer.Ordinal);
    private readonly OperationCancellationController _operationCancellation = new();
    private string _page = "devices";
    private string _statusText = "Готово";
    private bool _isBusy;
    private double _operationProgress;
    private bool _operationProgressIndeterminate;
    private DeviceViewModel? _selectedDevice;
    private SnapshotViewModel? _selectedSnapshot;
    private IPhoneMediaSourceViewModel? _selectedIPhoneSource;
    private string _restoreReportText = "Откройте локальное хранилище и выберите копию.";
    private string _mediaExportReportText =
        "Android копируется из MediaStore, iPhone — из DCIM через Windows. На телефоне ничего не удаляется.";
    private string _localCopyReportText =
        "Выбранный снимок можно сохранить одним зашифрованным файлом.";
    private string _mediaLiveStats =
        "Android использует ADB или Fast Wi-Fi. iPhone подключается по USB через системный импорт Windows.";
    private string _iPhoneSourceStatus =
        "Подключите разблокированный iPhone по USB и нажмите «Найти iPhone».";
    private ConnectionBenchmarkResult? _connectionBenchmark;
    private string _connectionTestStatus =
        "Подключите телефон и запустите тест. Реальные файлы не копируются.";
    private double _connectionTestProgress;
    private bool _isConnectionTestRunning;
    private string _diagnosticsSummary =
        "Запустите проверку Agent Dev или Root test, чтобы увидеть полную цепочку.";
    private AgentPreflightResult? _lastAgentPreflight;
    private AgentDiagnosticsSnapshot? _lastAgentDiagnostics;

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<IPhoneMediaSourceViewModel> IPhoneSources { get; } = [];
    public ObservableCollection<string> SnapshotLines { get; } = [];
    public ObservableCollection<SnapshotViewModel> Snapshots { get; } = [];
    public ObservableCollection<PackageSelectionViewModel> BackupPackages { get; } = [];
    public ObservableCollection<string> DiagnosticLines { get; } = [];
    public ObservableCollection<OperationProgressItemViewModel> OperationItems { get; } = [];

    public string PageTitle => LocalizationManager.T(_page switch
    {
        "backup" => "Новая резервная копия",
        "media" => "Фото и видео",
        "restore" => "Восстановить",
        "history" => "История",
        "settings" => "Настройки",
        "diagnostics" => "Diagnostics — DEV",
        _ => "Устройства"
    });
    public string PageSubtitle => LocalizationManager.T(_page switch
    {
        "backup" => SelectedDevice is null ? "Сначала выберите устройство" : SelectedDevice.DisplayName,
        "media" => SelectedDevice is null
            ? "Android: выберите устройство • iPhone: подключите по USB"
            : $"{SelectedDevice.DisplayName} • iPhone также поддерживается по USB",
        "restore" => "Compatibility engine не применяет опасные данные автоматически",
        "history" => RepositoryPath,
        "settings" => "Локальный зашифрованный репозиторий",
        "diagnostics" => "ADB → Agent hello → pairing → root_request → KernelSU/libsu",
        _ => "USB и Wireless ADB объединяются по физическому устройству"
    });

    public Visibility DevicesVisibility => Visible("devices");
    public Visibility BackupVisibility => Visible("backup");
    public Visibility MediaVisibility => Visible("media");
    public Visibility RestoreVisibility => Visible("restore");
    public Visibility HistoryVisibility => Visible("history");
    public Visibility SettingsVisibility => Visible("settings");
    public Visibility DiagnosticsVisibility => Visible("diagnostics");
    public Visibility DiagnosticsNavigationVisibility =>
        AppRuntimeProfile.IsDev ? Visibility.Visible : Visibility.Collapsed;
    public bool IsDevicesPage => _page == "devices";
    public bool IsBackupPage => _page == "backup";
    public bool IsMediaPage => _page == "media";
    public bool IsRestorePage => _page == "restore";
    public bool IsHistoryPage => _page == "history";
    public bool IsSettingsPage => _page == "settings";
    public bool IsDiagnosticsPage => _page == "diagnostics";

    public string StatusText
    {
        get => LocalizationManager.T(_statusText);
        set => Set(ref _statusText, value);
    }
    public string AppVersion => AppRuntimeProfile.VersionName;
    public string ProductName => AppRuntimeProfile.ProductName;
    public string DiagnosticsSummary
    {
        get => _diagnosticsSummary;
        set => Set(ref _diagnosticsSummary, value);
    }
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }
    public double OperationProgress
    {
        get => _operationProgress;
        set => Set(ref _operationProgress, Math.Clamp(value, 0, 100));
    }
    public bool OperationProgressIndeterminate
    {
        get => _operationProgressIndeterminate;
        set => Set(ref _operationProgressIndeterminate, value);
    }
    public bool CanCancelOperation => _operationCancellation.CanCancel;
    public Visibility CancelOperationVisibility =>
        _operationCancellation.IsActive ? Visibility.Visible : Visibility.Collapsed;
    public string CancelOperationText =>
        LocalizationManager.T(
            CanCancelOperation ? "Отменить копирование" : "Отмена…");
    public DeviceViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            var previousId = _selectedDevice?.Inventory.StableId;
            Set(ref _selectedDevice, value);
            if (!string.Equals(
                    previousId,
                    value?.Inventory.StableId,
                    StringComparison.Ordinal))
            {
                _connectionBenchmark = null;
                _connectionTestProgress = 0;
                _connectionTestStatus =
                    "Подключите телефон и запустите тест. Реальные файлы не копируются.";
                RaiseConnectionBenchmark();
            }
            Raise(nameof(PageSubtitle));
            Raise(nameof(SelectedMediaTransport));
        }
    }
    public SnapshotViewModel? SelectedSnapshot { get => _selectedSnapshot; set => Set(ref _selectedSnapshot, value); }
    public IPhoneMediaSourceViewModel? SelectedIPhoneSource
    {
        get => _selectedIPhoneSource;
        set
        {
            if (ReferenceEquals(_selectedIPhoneSource, value)) return;
            Set(ref _selectedIPhoneSource, value);
            if (value is not null)
            {
                IPhoneSourceStatus = $"{value.DisplayName}: " +
                    (value.IsLocked
                        ? "разблокируйте телефон перед копированием"
                        : "готов к копированию");
            }
        }
    }
    public string RestoreReportText
    {
        get => LocalizationManager.T(_restoreReportText);
        set => Set(ref _restoreReportText, value);
    }
    public string DevicesFoundLabel =>
        LocalizationManager.IsRussian ? $"Найдено: {Devices.Count}" : $"Found: {Devices.Count}";
    public string VersionLabel =>
        $"{AppVersion}{(AppRuntimeProfile.IsDev ? " DEV" : string.Empty)} • " +
        $"{(LocalizationManager.IsRussian ? "полностью офлайн" : "fully offline")}";
    public string MediaExportReportText
    {
        get => LocalizationManager.T(_mediaExportReportText);
        set => Set(ref _mediaExportReportText, value);
    }
    public string LocalCopyReportText
    {
        get => LocalizationManager.T(_localCopyReportText);
        set => Set(ref _localCopyReportText, value);
    }
    public string MediaLiveStats
    {
        get => LocalizationManager.T(_mediaLiveStats);
        set => Set(ref _mediaLiveStats, value);
    }
    public string IPhoneSourceStatus
    {
        get => LocalizationManager.T(_iPhoneSourceStatus);
        set => Set(ref _iPhoneSourceStatus, value);
    }
    public string ConnectionTestStatus
    {
        get => LocalizationManager.T(_connectionTestStatus);
        set => Set(ref _connectionTestStatus, value);
    }
    public double ConnectionTestProgress
    {
        get => _connectionTestProgress;
        set => Set(ref _connectionTestProgress, Math.Clamp(value, 0, 100));
    }
    public bool IsConnectionTestRunning
    {
        get => _isConnectionTestRunning;
        set => Set(ref _isConnectionTestRunning, value);
    }
    public string UsbLinkMetric => _connectionBenchmark is null
        ? "—"
        : FormatUsbLink(_connectionBenchmark.Usb.LinkSpeed);
    public string UsbLinkCaption => _connectionBenchmark is null
        ? LocalizationManager.T("Физический режим кабеля")
        : DescribeUsbLink(_connectionBenchmark.Usb);
    public string AdbSpeedMetric => _connectionBenchmark is null
        ? "—"
        : FormatRate(_connectionBenchmark.AdbBytesPerSecond);
    public string AdbSpeedCaption => LocalizationManager.T("Реальная скорость через ADB");
    public string FastLanSpeedMetric => _connectionBenchmark is null ||
                                        _connectionBenchmark.FastLanBytesPerSecond <= 0
        ? "—"
        : FormatRate(_connectionBenchmark.FastLanBytesPerSecond);
    public string FastLanSpeedCaption => _connectionBenchmark?.FastLanError is { Length: > 0 }
        ? LocalizationManager.T("Fast Wi-Fi сейчас недоступен")
        : LocalizationManager.T("Зашифрованный канал по локальной сети");
    public string ConnectionRecommendation => _connectionBenchmark is null
        ? LocalizationManager.T("VeXArk сравнит подключения и выберет самое быстрое.")
        : DescribeRecommendation(_connectionBenchmark);
    public string ConnectionEstimate => _connectionBenchmark is null
        ? LocalizationManager.T("После теста появится оценка времени для 10, 50 и 100 ГБ.")
        : BuildTransferEstimate(_connectionBenchmark.Assessment.EffectiveBytesPerSecond);

    public bool BackupApps { get; set; } = true;
    public bool BackupAppData { get; set; } = true;
    public bool BackupSharedStorage { get; set; } = true;
    public bool BackupPersonalData { get; set; } = true;
    public bool IncludeCaches { get; set; }
    public bool FullMode { get; set; }
    public bool AllowDowngrade { get; set; }
    public string RepositoryPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VeXArk");
    public string MediaDestination { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VeXArk Media");
    public string RepositoryPassword { get; set; } = string.Empty;
    public string NewRepositoryPassword { get; set; } = string.Empty;
    public string RecoveryInput { get; set; } = string.Empty;
    public string RecoveryCode { get; set; } = string.Empty;
    public string WirelessEndpoint { get; set; } = string.Empty;
    public string WirelessPairingCode { get; set; } = string.Empty;
    public IReadOnlyList<ChoiceOption> ThemeOptions =>
    [
        new("system", LocalizationManager.IsRussian ? "Как в системе" : "System"),
        new("light", LocalizationManager.IsRussian ? "Светлая" : "Light"),
        new("dark", LocalizationManager.IsRussian ? "Тёмная" : "Dark"),
        new("oled", "OLED")
    ];
    public IReadOnlyList<ChoiceOption> LanguageOptions =>
    [
        new("en", "English"),
        new("ru", "Русский")
    ];
    public IReadOnlyList<ChoiceOption> MediaTransportOptions =>
    [
        new("auto", LocalizationManager.IsRussian ? "Автоматически" : "Auto"),
        new("fastlan", LocalizationManager.IsRussian ? "Быстрый Wi-Fi" : "Fast Wi-Fi"),
        new("adb", "ADB")
    ];
    public string SelectedMediaTransport
    {
        get
        {
            var deviceId = SelectedDevice?.Inventory.StableId;
            return deviceId is not null && _mediaTransports.TryGetValue(deviceId, out var value)
                ? DesktopSettingsStore.NormalizeMediaTransport(value)
                : "auto";
        }
        set
        {
            var deviceId = SelectedDevice?.Inventory.StableId;
            if (deviceId is null) return;
            var normalized = DesktopSettingsStore.NormalizeMediaTransport(value);
            if (_mediaTransports.TryGetValue(deviceId, out var current) && current == normalized)
                return;
            _mediaTransports[deviceId] = normalized;
            SaveDesktopSettings();
            Raise();
        }
    }
    public string SelectedTheme
    {
        get => ThemeManager.SelectedTheme;
        set
        {
            var normalized = DesktopSettingsStore.NormalizeTheme(value);
            if (ThemeManager.SelectedTheme == normalized) return;
            ThemeManager.Apply(normalized);
            SaveDesktopSettings();
            Raise();
        }
    }
    public string SelectedLanguage
    {
        get => LocalizationManager.Language;
        set
        {
            var normalized = DesktopSettingsStore.NormalizeLanguage(value);
            if (LocalizationManager.Language == normalized) return;
            LocalizationManager.ApplyLanguage(normalized);
            SaveDesktopSettings();
            RefreshLocalization();
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ShowDevicesCommand { get; }
    public RelayCommand ShowBackupCommand { get; }
    public RelayCommand ShowMediaCommand { get; }
    public RelayCommand ShowRestoreCommand { get; }
    public RelayCommand ShowHistoryCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand ShowDiagnosticsCommand { get; }
    public RelayCommand SelectDeviceCommand { get; }
    public AsyncRelayCommand InstallAgentCommand { get; }
    public AsyncRelayCommand CreateRepositoryCommand { get; }
    public AsyncRelayCommand StartBackupCommand { get; }
    public RelayCommand CancelOperationCommand { get; }
    public AsyncRelayCommand LoadSnapshotsCommand { get; }
    public AsyncRelayCommand CompareRestoreCommand { get; }
    public AsyncRelayCommand RestoreSnapshotCommand { get; }
    public AsyncRelayCommand VerifyRepositoryCommand { get; }
    public AsyncRelayCommand GarbageCollectCommand { get; }
    public AsyncRelayCommand DeleteSnapshotCommand { get; }
    public AsyncRelayCommand ChangePasswordCommand { get; }
    public AsyncRelayCommand PairWirelessCommand { get; }
    public AsyncRelayCommand ConnectWirelessCommand { get; }
    public AsyncRelayCommand LoadBackupPackagesCommand { get; }
    public RelayCommand SelectAllPackagesCommand { get; }
    public RelayCommand SelectNoPackagesCommand { get; }
    public AsyncRelayCommand RecoverRepositoryCommand { get; }
    public RelayCommand ChooseRepositoryCommand { get; }
    public RelayCommand ChooseMediaDestinationCommand { get; }
    public AsyncRelayCommand ExportMediaCommand { get; }
    public AsyncRelayCommand RefreshIPhoneSourcesCommand { get; }
    public AsyncRelayCommand ImportIPhoneMediaCommand { get; }
    public AsyncRelayCommand TestConnectionCommand { get; }
    public AsyncRelayCommand ExportSnapshotBundleCommand { get; }
    public AsyncRelayCommand ImportSnapshotBundleCommand { get; }
    public AsyncRelayCommand CheckAgentDevCommand { get; }
    public AsyncRelayCommand InstallOrUpdateAgentDevCommand { get; }
    public AsyncRelayCommand RunRootDiagnosticCommand { get; }
    public AsyncRelayCommand RefreshAgentDiagnosticsCommand { get; }
    public RelayCommand ClearDiagnosticsCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }
    public RelayCommand OpenDiagnosticsFolderCommand { get; }
    public AsyncRelayCommand ExportSupportBundleCommand { get; }

    public MainViewModel() : this(null)
    {
    }

    internal MainViewModel(AdbService? adb)
    {
        _adb = adb ?? new AdbService();
        RefreshCommand = new(async _ => await RefreshAsync());
        ShowDevicesCommand = new(_ => Show("devices"));
        ShowBackupCommand = new(_ => Show("backup"));
        ShowMediaCommand = new(_ => Show("media"));
        ShowRestoreCommand = new(_ => Show("restore"));
        ShowHistoryCommand = new(_ => Show("history"));
        ShowSettingsCommand = new(_ => Show("settings"));
        ShowDiagnosticsCommand = new(_ => Show("diagnostics"));
        SelectDeviceCommand = new(device =>
        {
            SelectedDevice = device as DeviceViewModel;
            Show("backup");
        });
        InstallAgentCommand = new(InstallAgentAsync);
        CreateRepositoryCommand = new(CreateRepositoryAsync);
        StartBackupCommand = new(StartBackupAsync);
        CancelOperationCommand = new(
            _ => CancelActiveOperation(),
            _ => CanCancelOperation);
        LoadSnapshotsCommand = new(LoadSnapshotsAsync);
        CompareRestoreCommand = new(CompareRestoreAsync);
        RestoreSnapshotCommand = new(RestoreSnapshotAsync);
        VerifyRepositoryCommand = new(VerifyRepositoryAsync);
        GarbageCollectCommand = new(GarbageCollectAsync);
        DeleteSnapshotCommand = new(DeleteSnapshotAsync);
        ChangePasswordCommand = new(ChangePasswordAsync);
        PairWirelessCommand = new(PairWirelessAsync);
        ConnectWirelessCommand = new(ConnectWirelessAsync);
        LoadBackupPackagesCommand = new(LoadBackupPackagesAsync);
        SelectAllPackagesCommand = new(_ =>
        {
            foreach (var package in BackupPackages) package.IsSelected = true;
        });
        SelectNoPackagesCommand = new(_ =>
        {
            foreach (var package in BackupPackages) package.IsSelected = false;
        });
        RecoverRepositoryCommand = new(RecoverRepositoryAsync);
        LoadDesktopSettings();
        ExportMediaCommand = new(ExportMediaAsync);
        RefreshIPhoneSourcesCommand = new(RefreshIPhoneSourcesAsync);
        ImportIPhoneMediaCommand = new(ImportIPhoneMediaAsync);
        TestConnectionCommand = new(TestConnectionAsync);
        ExportSnapshotBundleCommand = new(ExportSnapshotBundleAsync);
        ImportSnapshotBundleCommand = new(ImportSnapshotBundleAsync);
        CheckAgentDevCommand = new(CheckAgentDevAsync);
        InstallOrUpdateAgentDevCommand = new(InstallOrUpdateAgentDevAsync);
        RunRootDiagnosticCommand = new(RunRootDiagnosticAsync);
        RefreshAgentDiagnosticsCommand = new(RefreshAgentDiagnosticsAsync);
        ClearDiagnosticsCommand = new(_ =>
        {
            DesktopDiagnostics.ClearView();
            _lastAgentDiagnostics = null;
            RefreshDiagnosticLines();
        });
        CopyDiagnosticsCommand = new(_ =>
        {
            var text = DiagnosticsSummary + Environment.NewLine + Environment.NewLine +
                       string.Join(Environment.NewLine, DiagnosticLines);
            Clipboard.SetText(text);
            StatusText = "Диагностика скопирована.";
        });
        OpenDiagnosticsFolderCommand = new(_ =>
        {
            Directory.CreateDirectory(DesktopDiagnostics.LogDirectory);
            Process.Start(new ProcessStartInfo(
                "explorer.exe",
                DesktopDiagnostics.LogDirectory)
            {
                UseShellExecute = true
            });
        });
        ExportSupportBundleCommand = new(ExportSupportBundleAsync);
        DesktopDiagnostics.EventWritten += OnDiagnosticEvent;
        RefreshDiagnosticLines();
        ChooseMediaDestinationCommand = new(_ =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Папка для фото и видео",
                InitialDirectory = MediaDestination
            };
            if (dialog.ShowDialog() == true)
            {
                MediaDestination = dialog.FolderName;
                Raise(nameof(MediaDestination));
                SaveDesktopSettings();
            }
        });
        ChooseRepositoryCommand = new(_ =>
        {
            var dialog = new OpenFolderDialog { Title = "Папка локального хранилища VeXArk" };
            if (dialog.ShowDialog() == true)
            {
                RepositoryPath = dialog.FolderName;
                Raise(nameof(RepositoryPath));
                Raise(nameof(PageSubtitle));
                SaveDesktopSettings();
            }
        });
    }

    public async Task RefreshAsync()
    {
        await BusyAsync("Поиск ADB-устройств…", async () =>
        {
            var selectedId = SelectedDevice?.Inventory.StableId;
            var found = await _adb.DiscoverAsync();
            Devices.Clear();
            foreach (var device in found) Devices.Add(new(device));
            Raise(nameof(DevicesFoundLabel));
            SelectedDevice = Devices.FirstOrDefault(x => x.Inventory.StableId == selectedId) ??
                             Devices.FirstOrDefault();
            StatusText = found.Count == 0 ? "Устройства не найдены" : $"Найдено устройств: {found.Count}";
        });
    }

    private async Task PairWirelessAsync(object? _)
    {
        await BusyAsync("Сопряжение Wireless ADB…", async () =>
        {
            await _adb.PairWirelessAsync(WirelessEndpoint, WirelessPairingCode);
            StatusText = "Wireless ADB сопряжён. Введите обычный порт подключения и нажмите «Подключить».";
        });
    }

    private async Task ConnectWirelessAsync(object? _)
    {
        await BusyAsync("Подключение Wireless ADB…", async () =>
        {
            await _adb.ConnectWirelessAsync(WirelessEndpoint);
            var found = await _adb.DiscoverAsync();
            Devices.Clear();
            foreach (var device in found) Devices.Add(new(device));
            Raise(nameof(DevicesFoundLabel));
            SelectedDevice = Devices.FirstOrDefault();
            StatusText = $"Wireless ADB подключён. Найдено устройств: {found.Count}";
        });
    }

    private async Task InstallAgentAsync(object? parameter)
    {
        if (parameter is not DeviceViewModel device) return;
        await BusyAsync("Установка Android Agent…", async () =>
        {
            await _adb.InstallAgentAsync(device.PreferredSerial, _adb.AgentApkPath);
            await using var agent = await AgentClient.ConnectAsync(_adb, device.PreferredSerial);
            var hello = await agent.HelloAsync();
            var pair = await agent.PairAsync();
            var root = pair.RootElement;
            StatusText = root.TryGetProperty("paired", out var paired) && paired.GetBoolean()
                ? "Agent установлен и подключён"
                : "Agent установлен — подтвердите компьютер на телефоне";
        });
    }

    private async Task CheckAgentDevAsync(object? _)
    {
        if (!TryGetDiagnosticDevice(out var device)) return;
        await BusyAsync("Проверка Agent Dev…", async () =>
        {
            _lastAgentPreflight = await new AgentPreflight(_adb)
                .CheckAsync(device.PreferredSerial);
            DiagnosticsSummary = FormatPreflight(_lastAgentPreflight);
            RefreshDiagnosticLines();
            StatusText = _lastAgentPreflight.Summary;
        });
    }

    private async Task InstallOrUpdateAgentDevAsync(object? _)
    {
        if (!TryGetDiagnosticDevice(out var device)) return;
        if (!AppRuntimeProfile.IsDev)
        {
            MessageBox.Show("Установка Agent Dev доступна только из VeXArk Dev.");
            return;
        }
        if (!File.Exists(_adb.AgentApkPath))
        {
            MessageBox.Show("В VeXArk Dev не встроен Agent Dev APK.");
            return;
        }
        if (MessageBox.Show(
                $"Установить или обновить {AppRuntimeProfile.AgentPackage}?\n\n" +
                "Stable Agent останется установленным и не будет изменён.",
                "VeXArk Agent Dev",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        await BusyAsync("Установка Agent Dev…", async () =>
        {
            await _adb.InstallAgentAsync(device.PreferredSerial, _adb.AgentApkPath);
            await _adb.LaunchAgentAsync(device.PreferredSerial);
            _lastAgentPreflight = await new AgentPreflight(_adb)
                .CheckAsync(device.PreferredSerial);
            DiagnosticsSummary = FormatPreflight(_lastAgentPreflight) +
                                 Environment.NewLine +
                                 "Выдайте KernelSU-доступ именно VeXArk Agent Dev и " +
                                 "подтвердите компьютер на телефоне.";
            RefreshDiagnosticLines();
            StatusText = _lastAgentPreflight.Compatible
                ? "Agent Dev установлен. Выдайте root и подтвердите компьютер."
                : _lastAgentPreflight.Summary;
        });
    }

    private async Task RunRootDiagnosticAsync(object? _)
    {
        if (!TryGetDiagnosticDevice(out var device)) return;
        await BusyAsync("Root test: проверка Agent Dev…", async () =>
        {
            var stages = new List<string>
            {
                $"✓ ADB: {device.Inventory.Model}, " +
                $"transport={device.Inventory.Transports.First(x => x.IsPreferred).Kind}."
            };
            var currentStage = "Agent preflight";
            try
            {
                _lastAgentPreflight = await new AgentPreflight(_adb)
                    .CheckAsync(device.PreferredSerial);
                if (!_lastAgentPreflight.Compatible)
                {
                    stages.Add("✗ Agent launch → hello → build preflight.");
                    DiagnosticsSummary = string.Join(Environment.NewLine, stages) +
                                         Environment.NewLine +
                                         FormatPreflight(_lastAgentPreflight);
                    RefreshDiagnosticLines();
                    throw new InvalidOperationException(
                        FormatPreflight(_lastAgentPreflight) +
                        Environment.NewLine +
                        "Нажмите «Установить/обновить Agent Dev».");
                }
                stages.Add("✓ Agent launch → hello → build preflight.");

                currentStage = "Pairing";
                StatusText = "Root test: подключение и pairing…";
                await using var agent = await AgentClient.ConnectAsync(
                    _adb,
                    device.PreferredSerial);
                if (!await agent.PairWithApprovalAsync(
                        TimeSpan.FromSeconds(60),
                        new Progress<string>(text => StatusText = text)))
                    throw new TimeoutException("Компьютер не подтверждён в Agent Dev.");
                stages.Add("✓ Pairing: Desktop подтверждён.");

                currentStage = "Root request";
                var build = await agent.GetBuildInfoAsync();
                StatusText = "Root test: KernelSU/libsu…";
                var root = await agent.RequestRootDetailsAsync();
                stages.Add(
                    $"{(root.Granted ? "✓" : "✗")} root_request: " +
                    $"granted={root.Granted}, provider={root.Provider}.");
                stages.Add(
                    $"{(root.RootDataReady ? "✓" : "✗")} root-helper: " +
                    $"ok={root.Helper?.Ok}, uid={root.Helper?.Uid?.ToString() ?? "—"}.");

                currentStage = "Agent diagnostics";
                _lastAgentDiagnostics = await agent.GetDiagnosticsSnapshotAsync();
                stages.Add($"✓ diagnostics_snapshot: {_lastAgentDiagnostics.Events.Count} events.");
                DiagnosticsSummary = string.Join(Environment.NewLine, stages) +
                                     Environment.NewLine +
                                     Environment.NewLine +
                                     FormatRootDiagnostic(build, root);
                RefreshDiagnosticLines();
                StatusText = root.RootDataReady
                    ? $"Root и helper готовы: {root.Provider}."
                    : root.Granted
                        ? "Root предоставлен, но root-helper не готов."
                        : $"Root не предоставлен: {root.Detail}";
            }
            catch (Exception error)
            {
                if (!stages.Any(x => x.StartsWith("✗", StringComparison.Ordinal)))
                    stages.Add($"✗ {currentStage}: {error.Message}");
                if (string.IsNullOrWhiteSpace(DiagnosticsSummary) ||
                    !DiagnosticsSummary.Contains(stages[0], StringComparison.Ordinal))
                    DiagnosticsSummary = string.Join(Environment.NewLine, stages);
                RefreshDiagnosticLines();
                throw;
            }
        });
    }

    private async Task RefreshAgentDiagnosticsAsync(object? _)
    {
        if (!TryGetDiagnosticDevice(out var device)) return;
        await BusyAsync("Получение логов Agent Dev…", async () =>
        {
            await using var agent = await AgentClient.ConnectAsync(
                _adb,
                device.PreferredSerial);
            if (!await agent.PairWithApprovalAsync(
                    TimeSpan.FromSeconds(60),
                    new Progress<string>(text => StatusText = text)))
                throw new TimeoutException("Компьютер не подтверждён в Agent Dev.");
            _lastAgentDiagnostics = await agent.GetDiagnosticsSnapshotAsync();
            RefreshDiagnosticLines();
            StatusText = $"Получено событий Agent: {_lastAgentDiagnostics.Events.Count}.";
        });
    }

    private async Task ExportSupportBundleAsync(object? _)
    {
        if (!TryGetDiagnosticDevice(out var device)) return;
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить VeXArk support bundle",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"VeXArk-Dev-Support-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };
        if (dialog.ShowDialog() != true) return;
        await BusyAsync("Создание support bundle…", async () =>
        {
            if (_lastAgentDiagnostics is null)
            {
                try
                {
                    await using var agent = await AgentClient.ConnectAsync(
                        _adb,
                        device.PreferredSerial);
                    if (await agent.PairWithApprovalAsync(
                            TimeSpan.FromSeconds(30),
                            new Progress<string>(text => StatusText = text)))
                        _lastAgentDiagnostics = await agent.GetDiagnosticsSnapshotAsync();
                }
                catch (Exception error)
                {
                    DesktopDiagnostics.Log(
                        "warn",
                        "diagnostics",
                        "agent_snapshot_unavailable",
                        "Support bundle will not contain Agent snapshot",
                        error: error);
                }
            }
            await new SupportBundleService(_adb).CreateAsync(
                dialog.FileName,
                device.Inventory,
                _lastAgentDiagnostics);
            StatusText = $"Support bundle сохранён: {dialog.FileName}";
        });
    }

    private bool TryGetDiagnosticDevice(out DeviceViewModel device)
    {
        device = SelectedDevice!;
        if (device is not null) return true;
        MessageBox.Show("Сначала выберите Android-устройство.");
        return false;
    }

    private void OnDiagnosticEvent(DiagnosticEvent diagnosticEvent)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            DiagnosticLines.Add("PC  " + diagnosticEvent.DisplayLine);
            while (DiagnosticLines.Count > 500) DiagnosticLines.RemoveAt(0);
        });
    }

    private void RefreshDiagnosticLines()
    {
        DiagnosticLines.Clear();
        foreach (var item in DesktopDiagnostics.Snapshot())
            DiagnosticLines.Add("PC  " + item.DisplayLine);
        if (_lastAgentDiagnostics is not null)
        {
            foreach (var item in _lastAgentDiagnostics.Events)
                DiagnosticLines.Add("ANDROID  " + item.DisplayLine);
        }
        while (DiagnosticLines.Count > 500) DiagnosticLines.RemoveAt(0);
    }

    private static string FormatPreflight(AgentPreflightResult result)
    {
        var lines = new List<string>
        {
            result.Summary,
            $"Ожидается: {AppRuntimeProfile.AgentPackage}, " +
            $"channel={AppRuntimeProfile.Channel}, port={AppRuntimeProfile.AgentPort}, " +
            $"build={AppRuntimeProfile.BuildId}.",
            result.InstalledAgent is null
                ? "Установленный Agent: отсутствует."
                : $"Установлен: {result.InstalledAgent.PackageName} " +
                  $"{result.InstalledAgent.VersionName} (code {result.InstalledAgent.VersionCode}), " +
                  $"UID {result.InstalledAgent.UserId}.",
            result.RunningBuild is null
                ? "Hello/build: недоступен."
                : $"Hello: {result.RunningBuild.Channel} " +
                  $"{result.RunningBuild.VersionName}, build={result.RunningBuild.BuildId}, " +
                  $"port={result.RunningBuild.AgentPort}."
        };
        lines.AddRange(result.Problems.Select(x => "• " + x));
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatRootDiagnostic(
        AgentBuildInfo build,
        RootRequestResult root)
    {
        var trace = root.Diagnostics;
        var lines = new List<string>
        {
            $"Desktop: {AppRuntimeProfile.VersionName} DEV, build={AppRuntimeProfile.BuildId}.",
            $"Agent: {build.PackageName}, {build.VersionName}, build={build.BuildId}, " +
            $"port={build.AgentPort}.",
            $"Root: granted={root.Granted}, available={root.Available}, " +
            $"provider={root.Provider}.",
            $"Причина: {root.Detail}",
            root.Helper is null
                ? "Root helper: не проверялся."
                : $"Root helper: ok={root.Helper.Ok}, " +
                  $"exit={root.Helper.ExitCode?.ToString() ?? "—"}, " +
                  $"uid={root.Helper.Uid?.ToString() ?? "—"}, " +
                  $"stderr={(string.IsNullOrWhiteSpace(root.Helper.Stderr) ? "—" : root.Helper.Stderr)}."
        };
        if (trace is not null)
        {
            lines.Add(
                $"Trace {trace.AttemptId}: UID {trace.AppUid}, " +
                $"grant={trace.AppGrantState}, exit={trace.ShellExitCode?.ToString() ?? "—"}, " +
                $"success={trace.ShellSuccess}, SELinux={trace.Selinux}, " +
                $"{trace.DurationMs} ms.");
            lines.Add($"stdout: {(string.IsNullOrWhiteSpace(trace.Stdout) ? "—" : trace.Stdout)}");
            lines.Add($"stderr: {(string.IsNullOrWhiteSpace(trace.Stderr) ? "—" : trace.Stderr)}");
            if (!string.IsNullOrWhiteSpace(trace.ExceptionType))
                lines.Add($"exception: {trace.ExceptionType}: {trace.ExceptionMessage}");
            lines.AddRange(trace.Steps.Select(x => "→ " + x));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private async Task LoadBackupPackagesAsync(object? _)
    {
        if (SelectedDevice is null)
        {
            MessageBox.Show("Сначала выберите устройство.");
            return;
        }
        await BusyAsync("Чтение списка приложений…", async () =>
        {
            await using var agent = await AgentClient.ConnectAsync(
                _adb, SelectedDevice.PreferredSerial);
            if (!await agent.PairWithApprovalAsync(
                    TimeSpan.FromSeconds(60),
                    new Progress<string>(x => StatusText = x)))
                throw new UnauthorizedAccessException("Компьютер не подтверждён.");
            var packages = await agent.GetPackagesAsync(
                includeSystemApps: false,
                hashArtifacts: false);
            BackupPackages.Clear();
            foreach (var package in packages.OrderBy(x => x.Label))
                BackupPackages.Add(new(package));
            var bytes = packages.SelectMany(x => x.ApkArtifacts).Sum(x => x.Size);
            StatusText = $"Приложений: {packages.Count}, APK/splits: {FormatBytes(bytes)}";
        });
    }

    private async Task CreateRepositoryAsync(object? _)
    {
        await BusyAsync("Создание зашифрованного репозитория…", async () =>
        {
            var creation = await EncryptedRepository.CreateAsync(RepositoryPath, RepositoryPassword);
            RecoveryCode = $"Recovery key — сохраните отдельно:\n{creation.RecoveryCode}";
            Raise(nameof(RecoveryCode));
            SaveDesktopSettings();
            StatusText = "Репозиторий создан";
        });
    }

    private async Task StartBackupAsync(object? _)
    {
        if (SelectedDevice is null)
        {
            MessageBox.Show("Сначала выберите устройство.");
            return;
        }
        if (!File.Exists(Path.Combine(RepositoryPath, "repository.json")))
        {
            MessageBox.Show("Сначала создайте или выберите репозиторий.");
            return;
        }
        if (string.IsNullOrWhiteSpace(RepositoryPassword))
        {
            MessageBox.Show("Введите пароль репозитория в настройках.");
            return;
        }

        await BusyAsync(
            "Подключение к Agent…",
            async cancellationToken =>
            {
            var serial = SelectedDevice.PreferredSerial;
            if (!await _adb.IsAgentInstalledAsync(serial, cancellationToken))
                throw new InvalidOperationException("Android Agent не установлен.");
            if (AppRuntimeProfile.IsDev)
            {
                _lastAgentPreflight = await new AgentPreflight(_adb).CheckAsync(
                    serial,
                    cancellationToken);
                DiagnosticsSummary = FormatPreflight(_lastAgentPreflight);
                if (!_lastAgentPreflight.Compatible)
                    throw new InvalidOperationException(
                        "Agent Dev не прошёл preflight. Откройте Diagnostics — DEV и " +
                        "установите встроенную версию.\n" +
                        string.Join("\n", _lastAgentPreflight.Problems));
            }

            await using var agent = await AgentClient.ConnectAsync(
                _adb,
                serial,
                cancellationToken);
            using var hello = await agent.HelloAsync(cancellationToken);
            var pairingProgress = new Progress<string>(text => StatusText = text);
            if (!await agent.PairWithApprovalAsync(
                    TimeSpan.FromSeconds(60),
                    pairingProgress,
                    cancellationToken))
                throw new TimeoutException("Компьютер не был подтверждён на телефоне.");

            StatusText = "Инвентаризация приложений и APK…";
            var requestedPackages = BackupPackages.Count > 0
                ? BackupPackages.Where(x => x.IsSelected)
                    .Select(x => x.PackageName)
                    .ToHashSet(StringComparer.Ordinal)
                : null;
            var packages = await agent.GetPackagesAsync(
                packageNames: requestedPackages,
                cancellationToken: cancellationToken,
                hashArtifacts: BackupApps);
            if (BackupPackages.Count > 0)
            {
                if (packages.Count == 0 && (BackupApps || BackupAppData))
                    throw new InvalidOperationException("Не выбрано ни одного приложения.");
            }
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                RepositoryPath,
                RepositoryPassword,
                cancellationToken);
            var romIdentity = await agent.GetRomIdentityAsync(cancellationToken);
            var backupDevice = SelectedDevice.Inventory with { Rom = romIdentity };
            var includeRootData = false;
            if (BackupAppData || FullMode)
            {
                StatusText = "Запрос root на телефоне…";
                var root = await agent.RequestRootDetailsAsync(cancellationToken);
                includeRootData = root.RootDataReady;
                backupDevice = backupDevice with
                {
                    Root = root.Granted
                        ? RootState.Granted
                        : root.Available
                            ? RootState.Denied
                            : RootState.Unavailable
                };
                if (!includeRootData)
                    throw new InvalidOperationException(
                        (root.Granted
                            ? "Root предоставлен, но root-helper не готов. "
                            : "Root не предоставлен. ") +
                        "Backup с выбранными приватными данными не начат, чтобы не создать " +
                        "ложную APK-only копию." +
                        (string.IsNullOrWhiteSpace(root.Detail)
                            ? string.Empty
                            : $"\n\nДиагностика Agent: {root.Detail}") +
                        (root.Helper is { Ok: false } helper
                            ? $"\n\nRoot helper: exit={helper.ExitCode?.ToString() ?? "—"}; " +
                              $"stderr={helper.Stderr}"
                            : string.Empty));
            }
            PackageSnapshot? launcherPackage = null;
            if (includeRootData && (BackupAppData || FullMode))
            {
                var homePackageName = await agent.GetHomePackageAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(homePackageName))
                {
                    launcherPackage = packages.FirstOrDefault(x =>
                        string.Equals(x.PackageName, homePackageName, StringComparison.Ordinal));
                    launcherPackage ??= (await agent.GetPackagesAsync(
                            includeSystemApps: true,
                            packageNames: [homePackageName],
                            cancellationToken: cancellationToken))
                        .SingleOrDefault();
                }
            }
            var transferProgress = new Progress<TransferProgress>(value =>
                UpdateOperationProgress(value, restore: false));
            var coordinator = new BackupCoordinator(_adb);
            var manifest = await Task.Run(() => coordinator.BackupPortableAsync(
                    serial,
                    backupDevice,
                    packages,
                    repository,
                    agent,
                    includeSystemApps: false,
                    includeApks: BackupApps,
                    includeAppData: includeRootData && BackupAppData,
                    includeSharedStorage: BackupSharedStorage,
                    includePersonalData: BackupPersonalData,
                    includeSystemState: true,
                    includeFullComponents: FullMode && includeRootData,
                    includeCaches: IncludeCaches,
                    fullHash: false,
                    launcherPackage: launcherPackage,
                    progress: transferProgress,
                    cancellationToken: cancellationToken),
                cancellationToken);
            var manifestAppCount = manifest.Components
                .Where(x => x.Package is not null)
                .Select(x => x.Package!.PackageName)
                .Distinct(StringComparer.Ordinal)
                .Count();
            SnapshotLines.Insert(
                0,
                $"{manifest.CreatedAtUtc.ToLocalTime():g} • {manifestAppCount} приложений • " +
                $"{manifest.Components.Count} компонентов • {manifest.SnapshotId[..8]}");
            StatusText = manifest.Summary?.Status switch
            {
                ComponentStatus.Partial =>
                    $"Backup частичный: " +
                    $"{manifest.Summary.PartialComponents + manifest.Summary.FailedComponents + manifest.Summary.SkippedComponents} " +
                    "компонентов неполные/пропущены",
                ComponentStatus.Failed => "Backup завершился ошибкой",
                _ => $"Backup завершён: {packages.Count} приложений, " +
                     $"{manifest.Components.Count} компонентов"
            };
            },
            cancellable: true,
            cancellationStatus:
            "Backup отменён. Незавершённый snapshot не опубликован; " +
            "уже записанные chunks будут использованы при следующем запуске.");
    }

    private async Task LoadSnapshotsAsync(object? _)
    {
        if (!File.Exists(Path.Combine(RepositoryPath, "repository.json")))
        {
            MessageBox.Show("Выберите локальное хранилище VeXArk.");
            return;
        }
        if (string.IsNullOrWhiteSpace(RepositoryPassword))
        {
            MessageBox.Show("Введите пароль репозитория.");
            return;
        }
        await BusyAsync("Открытие manifests…", async () =>
        {
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                RepositoryPath, RepositoryPassword);
            var manifests = await repository.ListSnapshotsAsync();
            PopulateSnapshots(manifests);
            RestoreReportText = manifests.Count == 0
                ? "В репозитории пока нет snapshots."
                : $"Найдено snapshots: {manifests.Count}. Выберите снимок для проверки.";
            StatusText = $"Репозиторий открыт: {manifests.Count} snapshots";
        });
    }

    private async Task RefreshIPhoneSourcesAsync(object? _)
    {
        await BusyAsync("Поиск iPhone…", async () =>
        {
            await RefreshIPhoneSourcesCoreAsync();
            StatusText = IPhoneSources.Count == 0
                ? "iPhone не найден"
                : $"Найдено Apple-устройств: {IPhoneSources.Count}";
        });
    }

    private async Task RefreshIPhoneSourcesCoreAsync(
        CancellationToken cancellationToken = default)
    {
        var selectedId = SelectedIPhoneSource?.Id;
        var sources = await _iPhoneMedia.DiscoverAsync(cancellationToken);
        IPhoneSources.Clear();
        foreach (var source in sources) IPhoneSources.Add(source);
        SelectedIPhoneSource =
            IPhoneSources.FirstOrDefault(source => source.Id == selectedId) ??
            IPhoneSources.FirstOrDefault();
        IPhoneSourceStatus = SelectedIPhoneSource is null
            ? "iPhone не найден. Установите Apple Devices, разблокируйте телефон, нажмите «Доверять» и переподключите кабель."
            : $"{SelectedIPhoneSource.DisplayName}: " +
              (SelectedIPhoneSource.IsLocked
                  ? "разблокируйте телефон перед копированием"
                  : "готов к копированию");
        Raise(nameof(PageSubtitle));
    }

    private async Task ImportIPhoneMediaAsync(object? _)
    {
        await BusyAsync(
            "Подключение к iPhone…",
            async cancellationToken =>
            {
            if (SelectedIPhoneSource is null)
                await RefreshIPhoneSourcesCoreAsync(cancellationToken);
            var source = SelectedIPhoneSource ?? throw new InvalidOperationException(
                "iPhone не найден. Установите Apple Devices, разблокируйте телефон, " +
                "нажмите «Доверять» и переподключите USB-кабель.");

            SaveDesktopSettings();
            var transferProgress = new Progress<TransferProgress>(value =>
            {
                StatusText = value.Stage switch
                {
                    "iphone-scan" => "iPhone составляет список фото и видео…",
                    "iphone-copy" when value.Total > 0 =>
                        $"Копирование с iPhone: {value.Completed}/{value.Total}",
                    "iphone-complete" => "Копирование с iPhone завершено",
                    _ => value.Item
                };
            });
            var transferMetrics = new Progress<IPhoneMediaTransferMetrics>(value =>
            {
                var eta = value.BytesPerSecond > 0 &&
                          value.TotalBytes > value.ImportedBytes
                    ? TimeSpan.FromSeconds(
                        (value.TotalBytes - value.ImportedBytes) / value.BytesPerSecond)
                    : (TimeSpan?)null;
                MediaLiveStats =
                    $"Apple USB • {FormatRate(value.BytesPerSecond)}\n" +
                    $"{FormatBytes(value.ImportedBytes)} / {FormatBytes(value.TotalBytes)} • " +
                    (eta is { } remaining
                        ? $"осталось ~{FormatDuration(remaining)}"
                        : "завершение") +
                    $" • {value.ImportedItems}/{value.TotalItems} файлов";
            });

            var report = await _iPhoneMedia.ImportNewAsync(
                source,
                MediaDestination,
                transferProgress,
                transferMetrics,
                cancellationToken);
            MediaExportReportText =
                $"iPhone: {report.SourceName}\n" +
                $"Скопировано: {report.ImportedItems} ({FormatBytes(report.ImportedBytes)})\n" +
                $"Фото: {report.Photos}, видео: {report.Videos}\n" +
                $"Уже было импортировано: {report.SkippedItems}\n" +
                $"Ошибок: {report.FailedItems}\n" +
                $"Новых данных: {FormatBytes(report.TotalBytes)}\n" +
                $"Средняя скорость: {FormatRate(report.AverageBytesPerSecond)}" +
                (report.Errors.Count == 0
                    ? string.Empty
                    : "\n\nОшибки:\n" + string.Join("\n", report.Errors.Take(10)));
            StatusText = report.FailedItems == 0
                ? $"Фото и видео с iPhone скопированы: {report.ImportedItems}, пропущено: {report.SkippedItems}"
                : $"Копирование с iPhone завершено с ошибками: {report.FailedItems}";
            IPhoneSourceStatus =
                $"{report.SourceName}: последнее копирование — {report.ImportedItems} новых файлов";
            },
            cancellable: true,
            cancellationStatus:
            "Копирование с iPhone отменено. Уже импортированные файлы сохранены.");
    }

    private async Task ExportMediaAsync(object? _)
    {
        if (SelectedDevice is null)
        {
            MessageBox.Show("Сначала выберите устройство.");
            return;
        }
        await BusyAsync(
            "Подключение к Agent…",
            async cancellationToken =>
            {
            var serial = SelectedDevice.PreferredSerial;
            if (!await _adb.IsAgentInstalledAsync(serial, cancellationToken))
                throw new InvalidOperationException(
                    "Android Agent не установлен. Установите его на странице «Устройства».");

            await using var agent = await AgentClient.ConnectAsync(
                _adb,
                serial,
                cancellationToken);
            if (!await agent.PairWithApprovalAsync(
                    TimeSpan.FromSeconds(60),
                    new Progress<string>(text => StatusText = text),
                    cancellationToken))
                throw new UnauthorizedAccessException("Компьютер не подтверждён на телефоне.");

            SaveDesktopSettings();
            var transferProgress = new Progress<TransferProgress>(value =>
            {
                StatusText = value.Stage switch
                {
                    "media-scan" => "Телефон составляет список фото и видео…",
                    "media-copy" => value.Total > 0
                        ? $"Копирование {value.Completed + 1}/{value.Total}: {value.Item}"
                        : $"Копирование: {value.Item}",
                    "media-probe" => value.Item,
                    "media-fallback" => value.Item,
                    _ => value.Item
                };
            });
            var transferMetrics = new Progress<MediaTransferMetrics>(value =>
            {
                var transport = value.Transport == MediaTransportMode.FastLan
                    ? (LocalizationManager.IsRussian ? "Быстрый Wi-Fi" : "Fast Wi-Fi")
                    : "ADB";
                var eta = value.EstimatedRemaining is { } remaining &&
                          remaining > TimeSpan.Zero
                    ? (LocalizationManager.IsRussian
                        ? $"осталось ~{FormatDuration(remaining)}"
                        : $"~{FormatDuration(remaining)} remaining")
                    : (LocalizationManager.IsRussian ? "завершение" : "finishing");
                MediaLiveStats =
                    $"{transport} • {FormatRate(value.BytesPerSecond)} • " +
                    $"{(LocalizationManager.IsRussian ? "диск" : "disk")} " +
                    $"{FormatRate(value.DiskBytesPerSecond)}\n" +
                    $"{FormatBytes(value.CompletedBytes)} / {FormatBytes(value.TotalBytes)} • " +
                    $"{eta} • {value.ActiveFiles} " +
                    $"{(LocalizationManager.IsRussian ? "активных файлов" : "active files")}";
            });
            var selectedMode = SelectedMediaTransport switch
            {
                "fastlan" => MediaTransportMode.FastLan,
                "adb" => MediaTransportMode.Adb,
                _ => MediaTransportMode.Auto
            };
            var report = await new MediaExportCoordinator().ExportAsync(
                agent,
                MediaDestination,
                new(selectedMode),
                transferProgress,
                transferMetrics,
                cancellationToken);
            var transportName = report.Transport == MediaTransportMode.FastLan
                ? (LocalizationManager.IsRussian ? "Быстрый Wi-Fi" : "Fast Wi-Fi")
                : "ADB";
            MediaExportReportText =
                $"Скопировано: {report.CopiedFiles} ({FormatBytes(report.CopiedBytes)})\n" +
                $"Уже было на ПК: {report.SkippedFiles}\n" +
                $"Продолжено: {report.ResumedFiles} ({FormatBytes(report.ResumedBytes)})\n" +
                $"Ошибок: {report.FailedFiles}\n" +
                $"Всего найдено: {FormatBytes(report.TotalBytes)}\n" +
                $"Транспорт: {transportName}, workers: {report.WorkerCount}\n" +
                $"Средняя скорость: {FormatRate(report.AverageBytesPerSecond)}\n" +
                $"Тесты: ADB {FormatRate(report.AdbProbeBytesPerSecond)}, " +
                $"Fast Wi-Fi {FormatRate(report.FastLanProbeBytesPerSecond)}, " +
                $"диск {FormatRate(report.DiskBytesPerSecond)}" +
                (report.Errors.Count == 0
                    ? string.Empty
                    : "\n\nПервые ошибки:\n" + string.Join("\n", report.Errors.Take(10)));
            StatusText = report.FailedFiles == 0
                ? $"Фото и видео скопированы: {report.CopiedFiles}, пропущено: {report.SkippedFiles}"
                : $"Копирование завершено с ошибками: {report.FailedFiles}";
            },
            cancellable: true,
            cancellationStatus:
            "Копирование с Android отменено. Готовые файлы сохранены; " +
            "незавершённые продолжатся при следующем запуске.");
    }

    private async Task TestConnectionAsync(object? _)
    {
        if (SelectedDevice is null)
        {
            MessageBox.Show(LocalizationManager.T("Сначала выберите устройство."));
            return;
        }

        await BusyAsync("Тест подключения…", async () =>
        {
            IsConnectionTestRunning = true;
            ConnectionTestProgress = 0;
            _connectionBenchmark = null;
            RaiseConnectionBenchmark();
            try
            {
                var serial = SelectedDevice.PreferredSerial;
                if (!await _adb.IsAgentInstalledAsync(serial))
                    throw new InvalidOperationException(LocalizationManager.T(
                        "Android Agent не установлен. Установите его на странице «Устройства»."));

                await using var agent = await AgentClient.ConnectAsync(_adb, serial);
                if (!await agent.PairWithApprovalAsync(
                        TimeSpan.FromSeconds(60),
                        new Progress<string>(text => ConnectionTestStatus = text)))
                    throw new UnauthorizedAccessException(LocalizationManager.T(
                        "Компьютер не подтверждён на телефоне."));

                var progress = new Progress<ConnectionBenchmarkProgress>(value =>
                {
                    var ratio = value.TotalBytes <= 0
                        ? 0
                        : Math.Clamp(
                            value.CompletedBytes / (double)value.TotalBytes,
                            0,
                            1);
                    switch (value.Stage)
                    {
                        case "usb":
                            ConnectionTestProgress = 2;
                            ConnectionTestStatus =
                                "Windows проверяет физический режим USB…";
                            break;
                        case "adb":
                            ConnectionTestProgress = 5 + ratio * 43;
                            ConnectionTestStatus =
                                $"Тест ADB: {ratio:P0}";
                            break;
                        case "fast-lan":
                            ConnectionTestProgress = 52 + ratio * 46;
                            ConnectionTestStatus =
                                $"Тест Fast Wi-Fi: {ratio:P0}";
                            break;
                        case "complete":
                            ConnectionTestProgress = 100;
                            ConnectionTestStatus = "Тест подключения завершён.";
                            break;
                    }
                });

                _connectionBenchmark = await new ConnectionBenchmarkCoordinator().RunAsync(
                    agent,
                    serial,
                    progress: progress);
                RaiseConnectionBenchmark();
                StatusText = _connectionBenchmark.Assessment.RecommendedTransport ==
                             MediaTransportMode.FastLan
                    ? $"Fast Wi-Fi быстрее: {FormatRate(_connectionBenchmark.FastLanBytesPerSecond)}"
                    : $"ADB быстрее: {FormatRate(_connectionBenchmark.AdbBytesPerSecond)}";
            }
            catch (Exception error)
            {
                ConnectionTestStatus = $"Тест не выполнен: {error.Message}";
                throw;
            }
            finally
            {
                IsConnectionTestRunning = false;
            }
        });
    }

    private async Task ExportSnapshotBundleAsync(object? _)
    {
        if (SelectedSnapshot is null)
        {
            MessageBox.Show("Сначала выберите резервную копию в истории.");
            return;
        }
        if (string.IsNullOrWhiteSpace(RepositoryPassword))
        {
            MessageBox.Show("Введите пароль хранилища в настройках.");
            return;
        }
        var suggested = $"VeXArk-{SelectedSnapshot.Manifest.Device.Device}-" +
                        $"{SelectedSnapshot.Manifest.CreatedAtUtc.ToLocalTime():yyyy-MM-dd-HHmm}.vexark";
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить локальную резервную копию",
            Filter = "Резервная копия VeXArk (*.vexark)|*.vexark",
            FileName = suggested,
            AddExtension = true,
            DefaultExt = ".vexark"
        };
        if (dialog.ShowDialog() != true)
            return;

        await BusyAsync("Создание локального файла резервной копии…", async () =>
        {
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                RepositoryPath,
                RepositoryPassword);
            var report = await repository.ExportSnapshotBundleAsync(
                SelectedSnapshot.Manifest,
                dialog.FileName);
            LocalCopyReportText =
                $"Сохранено: {dialog.FileName}\n" +
                $"Зашифрованных объектов: {report.ObjectCount}, " +
                $"размер данных: {FormatBytes(report.StoredBytes)}";
            StatusText = "Локальная резервная копия сохранена одним файлом.";
        });
    }

    private async Task ImportSnapshotBundleAsync(object? _)
    {
        var fileDialog = new OpenFileDialog
        {
            Title = "Открыть локальную резервную копию",
            Filter = "Резервная копия VeXArk (*.vexark)|*.vexark|" +
                     "Старая копия PhoneBackup (*.pbbackup)|*.pbbackup",
            CheckFileExists = true,
            Multiselect = false
        };
        if (fileDialog.ShowDialog() != true)
            return;
        var folderDialog = new OpenFolderDialog
        {
            Title = "Выберите новую или пустую папку для резервной копии"
        };
        if (folderDialog.ShowDialog() != true)
            return;

        await BusyAsync("Импорт локальной резервной копии…", async () =>
        {
            var report = await EncryptedRepository.ImportSnapshotBundleAsync(
                fileDialog.FileName,
                folderDialog.FolderName);
            RepositoryPath = folderDialog.FolderName;
            Raise(nameof(RepositoryPath));
            Raise(nameof(PageSubtitle));
            SaveDesktopSettings();
            LocalCopyReportText =
                $"Копия открыта в {RepositoryPath}\n" +
                $"Импортировано зашифрованных объектов: {report.ObjectCount}. " +
                "Введите пароль копии, чтобы увидеть снимок.";
            StatusText = "Локальная резервная копия импортирована.";
        });
    }

    private async Task CompareRestoreAsync(object? _)
    {
        if (SelectedDevice is null || SelectedSnapshot is null)
        {
            MessageBox.Show("Выберите устройство и snapshot.");
            return;
        }
        await BusyAsync("Сравнение snapshot с телефоном…", async () =>
        {
            var packageComponents = SelectedSnapshot.Manifest.Components
                .Where(x => x.Kind == "apk" && x.Package is not null)
                .ToList();
            var packageNames = packageComponents
                .Select(x => x.Package!.PackageName)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            await using var agent = await AgentClient.ConnectAsync(
                _adb,
                SelectedDevice.PreferredSerial);
            if (!await agent.PairWithApprovalAsync(
                    TimeSpan.FromSeconds(60),
                    new Progress<string>(x => StatusText = x)))
                throw new UnauthorizedAccessException("Компьютер не подтверждён.");
            var installed = (await agent.GetPackagesAsync(
                    includeSystemApps: true,
                    packageNames: packageNames,
                    hashArtifacts: false))
                .ToDictionary(x => x.PackageName, StringComparer.Ordinal);
            var missing = packageNames
                .Where(x => !installed.ContainsKey(x))
                .ToList();
            var signatureMismatch = packageComponents
                .Where(x => installed.TryGetValue(x.Package!.PackageName, out var current) &&
                            !RestoreCoordinator.SignatureMatches(x.Package, current))
                .Select(x => x.Package!.PackageName)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var okCount = packageNames.Count - missing.Count - signatureMismatch.Count;
            var lines = new List<string>
            {
                $"Совпали: {okCount}/{packageNames.Count}",
                $"Отсутствуют: {missing.Count}",
                $"Другая подпись: {signatureMismatch.Count}"
            };
            if (missing.Count > 0)
                lines.Add("Отсутствуют: " + string.Join(", ", missing));
            if (signatureMismatch.Count > 0)
                lines.Add("Другая подпись: " + string.Join(", ", signatureMismatch));
            RestoreReportText = string.Join(Environment.NewLine, lines);
            StatusText = $"Сравнение завершено: совпали {okCount}/{packageNames.Count}";
        });
    }

    private async Task RestoreSnapshotAsync(object? _)
    {
        if (SelectedDevice is null || SelectedSnapshot is null)
        {
            MessageBox.Show("Выберите устройство и snapshot.");
            return;
        }
        await BusyAsync(
            "Проверка совместимости Restore…",
            async cancellationToken =>
            {
            var serial = SelectedDevice.PreferredSerial;
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                RepositoryPath,
                RepositoryPassword,
                cancellationToken);
            await using var agent = await AgentClient.ConnectAsync(
                _adb,
                serial,
                cancellationToken);
            if (!await agent.PairWithApprovalAsync(
                    TimeSpan.FromSeconds(60),
                    new Progress<string>(x => StatusText = x),
                    cancellationToken))
                throw new UnauthorizedAccessException("Компьютер не подтверждён.");

            var currentRomIdentity = await agent.GetRomIdentityAsync(cancellationToken);
            var targetInventory = SelectedDevice.Inventory with { Rom = currentRomIdentity };

            var snapshot = SelectedSnapshot.Manifest;
            var packageNames = snapshot.Components
                .Where(x => x.Kind == "apk")
                .Select(x => x.Id)
                .ToList();
            var launcherComponents = snapshot.Components
                .Where(x => x.Kind == "launcher-data" && x.Package is not null)
                .ToList();
            var inventoryNames = packageNames
                .Concat(launcherComponents.Select(x => x.Package!.PackageName))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var installed = await agent.GetPackagesAsync(
                includeSystemApps: true,
                inventoryNames,
                cancellationToken,
                hashArtifacts: false);
            var activeHomePackage = launcherComponents.Count == 0
                ? null
                : await agent.GetHomePackageAsync(cancellationToken);
            var installedByPackage = installed.ToDictionary(
                x => x.PackageName,
                StringComparer.Ordinal);
            var compatibleLauncherPackages = launcherComponents
                .Select(x => x.Package!)
                .DistinctBy(x => x.PackageName, StringComparer.Ordinal)
                .Where(x => string.Equals(
                    x.PackageName,
                    activeHomePackage,
                    StringComparison.Ordinal))
                .Where(x => installedByPackage.ContainsKey(x.PackageName))
                .Where(x => RestoreCoordinator.SignatureMatches(
                    x,
                    installedByPackage[x.PackageName]))
                .Where(_ => snapshot.FormatVersion < 2
                    ? Math.Abs(snapshot.Device.Sdk - targetInventory.Sdk) <= 1
                    : snapshot.Rom?.IsExactRomMatch(targetInventory.Rom) == true)
                .Select(x => x.PackageName)
                .ToList();
            var coordinator = new RestoreCoordinator(_adb);
            var report = coordinator.PlanApkRestore(
                snapshot, targetInventory, installed);
            RestoreReportText = FormatRestoreReport(report);
            var settingsCoverage = await coordinator.ReadSettingsCoverageAsync(
                snapshot,
                repository,
                cancellationToken);
            if (settingsCoverage is not null)
            {
                RestoreReportText += Environment.NewLine +
                    $"Настройки: поддержано {settingsCoverage.Supported}, " +
                    $"только архив {settingsCoverage.Archived}, " +
                    $"игнорируется {settingsCoverage.Ignored}, " +
                    $"неизвестных ключей {settingsCoverage.Unknown}.";
                if (settingsCoverage.UnknownSamples.Count > 0)
                    RestoreReportText += Environment.NewLine +
                        "Примеры неизвестных (не применяются): " +
                        string.Join(", ", settingsCoverage.UnknownSamples);
            }
            var installedSnapshotPackages = installed
                .Select(x => x.PackageName)
                .ToHashSet(StringComparer.Ordinal);
            var missingBeforeRestore = packageNames
                .Where(x => !installedSnapshotPackages.Contains(x))
                .ToList();
            RestoreReportText += Environment.NewLine +
                $"На телефоне до Restore: {packageNames.Count - missingBeforeRestore.Count}/{packageNames.Count} приложений.";
            if (missingBeforeRestore.Count > 0)
                RestoreReportText += Environment.NewLine +
                    "Будут установлены: " + string.Join(", ", missingBeforeRestore);
            if (launcherComponents.Count > 0)
            {
                RestoreReportText += Environment.NewLine +
                    (compatibleLauncherPackages.Count > 0
                        ? $"Conditional: раскладка рабочего стола будет восстановлена для {compatibleLauncherPackages[0]} (совпали package и Android SDK)."
                        : "INFO: раскладка рабочего стола пропущена — активный Launcher или Android SDK не совпадают.");
            }
            if (snapshot.Components.Any(x => x.Kind == "account-inventory"))
            {
                RestoreReportText += Environment.NewLine +
                    "INFO: список аккаунтов сохранён только как зашифрованная памятка; " +
                    "пароли и авторизация Google не восстанавливаются.";
            }

            var packageItems = report.Items
                .Where(x => packageNames.Contains(x.Component, StringComparer.Ordinal))
                .ToDictionary(x => x.Component, StringComparer.Ordinal);
            var selected = packageNames
                .Where(x => !packageItems.TryGetValue(x, out var item) ||
                            (item.Level != CompatibilityLevel.Blocked &&
                             (AllowDowngrade || item.Level != CompatibilityLevel.Conditional)))
                .ToList();
            var skippedByCompatibility = packageNames.Except(selected, StringComparer.Ordinal).ToList();
            if (skippedByCompatibility.Count > 0)
                RestoreReportText += Environment.NewLine +
                    "Пропущены compatibility engine: " +
                    string.Join(", ", skippedByCompatibility);

            var restoreProgress = new Progress<TransferProgress>(UpdateRestoreProgress);
            var failedApkPackages = await Task.Run(
                () => coordinator.RestoreApksAsync(
                    serial,
                    snapshot,
                    targetInventory,
                    installed,
                    repository,
                    agent,
                    selected,
                    AllowDowngrade,
                    createSafetySnapshot: true,
                    progress: restoreProgress,
                    cancellationToken: cancellationToken),
                cancellationToken);
            if (failedApkPackages.Count > 0)
            {
                selected.RemoveAll(failedApkPackages.Contains);
                RestoreReportText += Environment.NewLine +
                    $"APK не появились после установки ({failedApkPackages.Count}): " +
                    string.Join(", ", failedApkPackages);
            }
            RestoreReportText += Environment.NewLine +
                $"APK подтверждены после установки: {selected.Count}/{packageNames.Count}.";
            var selectedDataPackages = selected
                .Concat(compatibleLauncherPackages)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (snapshot.Components.Any(x =>
                    x.Kind is "app-data" or "launcher-data" &&
                    x.Package is not null &&
                    selectedDataPackages.Contains(
                        x.Package.PackageName,
                        StringComparer.Ordinal)))
            {
                var restoredPackages = await agent.GetPackagesAsync(
                    includeSystemApps: true,
                    packageNames: selectedDataPackages,
                    cancellationToken: cancellationToken,
                    hashArtifacts: false);
                var appDataResults = await Task.Run(() => coordinator.RestoreAppDataAsync(
                        snapshot,
                        restoredPackages,
                        repository,
                        agent,
                        selectedDataPackages,
                        targetInventory.Rom,
                        restoreProgress,
                        cancellationToken),
                    cancellationToken);
                RestoreReportText += Environment.NewLine +
                    "Данные приложений:" + Environment.NewLine +
                    string.Join(
                        Environment.NewLine,
                        appDataResults.OrderBy(x => x.Key, StringComparer.Ordinal)
                            .Select(x => $"{x.Key}: {x.Value}"));
            }
            if (snapshot.Components.Any(x => x.Kind == "shared-storage"))
            {
                var sharedResults = await Task.Run(() => coordinator.RestoreSharedStorageAsync(
                        snapshot,
                        repository,
                        agent,
                        restoreProgress,
                        cancellationToken),
                    cancellationToken);
                RestoreReportText += Environment.NewLine +
                    "Общая память:" + Environment.NewLine +
                    string.Join(Environment.NewLine, sharedResults.Select(x =>
                        $"{x.ComponentId}: {x.State}" +
                        (string.IsNullOrWhiteSpace(x.Reason) ? "" : $"; {x.Reason}")));
            }
            var portablePersonal = snapshot.Components.Any(x =>
                x.Kind == "personal-data" && x.RestoreClass == RestoreClass.Portable);
            if (portablePersonal)
            {
                var mergePersonal = MessageBox.Show(
                    "Объединить контакты и журнал звонков с данными телефона?\n\n" +
                    "Существующие записи не удаляются; совпадения будут пропущены. " +
                    "SMS/MMS остаются только в архиве.",
                    "Личные данные",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (mergePersonal == MessageBoxResult.Yes)
                {
                    var personalResults = await Task.Run(() => coordinator.RestorePersonalDataAsync(
                            snapshot, repository, agent, restoreProgress, cancellationToken),
                        cancellationToken);
                    RestoreReportText += Environment.NewLine + "Личные данные:" + Environment.NewLine +
                        string.Join(Environment.NewLine, personalResults.Select(x =>
                            $"{x.Kind}: {x.Status}; добавлено {x.Inserted}, " +
                            $"дубликатов {x.Skipped}, ошибок {x.Failed}" +
                            (string.IsNullOrWhiteSpace(x.Reason) ? "" : $"; {x.Reason}")));
                }
                else RestoreReportText += Environment.NewLine +
                    "Контакты и журнал звонков пропущены пользователем.";
            }
            var policyFailures = await Task.Run(() => coordinator.RestorePackagePoliciesAsync(
                    snapshot,
                    agent,
                    selected,
                    restoreProgress,
                    cancellationToken),
                cancellationToken);
            if (policyFailures.Count > 0)
            {
                RestoreReportText += Environment.NewLine +
                    "Не все package policy применились:" + Environment.NewLine +
                    string.Join(
                        Environment.NewLine,
                        policyFailures.Select(x => $"{x.Key}: {string.Join(", ", x.Value)}"));
            }
            var systemResult = await Task.Run(() => coordinator.RestoreSystemStateAsync(
                    snapshot,
                    repository,
                    agent,
                    confirmedSettingIds: null,
                    progress: restoreProgress,
                    cancellationToken: cancellationToken),
                cancellationToken);
            if (systemResult.NeedsConfirmation.Count > 0)
            {
                var preview = string.Join(Environment.NewLine,
                    systemResult.NeedsConfirmation.Take(20));
                if (systemResult.NeedsConfirmation.Count > 20)
                    preview += $"{Environment.NewLine}…ещё {systemResult.NeedsConfirmation.Count - 20}";
                var confirmation = MessageBox.Show(
                    "Эти системные настройки требуют явного подтверждения:\n\n" + preview +
                    "\n\nПрименить их с проверкой read-back и rollback при ошибке?",
                    "Подтверждение опасных настроек",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirmation == MessageBoxResult.Yes)
                    systemResult = await Task.Run(() => coordinator.RestoreSystemStateAsync(
                            snapshot,
                            repository,
                            agent,
                            systemResult.NeedsConfirmation,
                            restoreProgress,
                            cancellationToken),
                        cancellationToken);
                else
                    RestoreReportText += Environment.NewLine +
                        $"Настройки с подтверждением пропущены: {systemResult.NeedsConfirmation.Count}.";
            }
            if (systemResult.Failures.Count > 0)
            {
                RestoreReportText += Environment.NewLine +
                    "Настройки, которые не удалось применить: " +
                    string.Join(", ", systemResult.Failures);
            }
            var rolledBackSettings = systemResult.Outcomes
                .Where(x => x.Status == "rolled_back")
                .Select(x => x.Id)
                .ToList();
            if (rolledBackSettings.Count > 0)
                RestoreReportText += Environment.NewLine +
                    "Настройки с выполненным rollback: " + string.Join(", ", rolledBackSettings);
            var wallpaperResults = await Task.Run(() => coordinator.RestoreWallpapersAsync(
                    snapshot,
                    repository,
                    agent,
                    restoreProgress,
                    cancellationToken),
                cancellationToken);
            if (wallpaperResults.Count > 0)
                RestoreReportText += Environment.NewLine +
                    "Обои: " + string.Join(", ", wallpaperResults.Select(x => $"{x.Key}={x.Value}"));
            StatusText = $"Restore завершён: {selected.Count} приложений";
            OperationProgress = 100;
            OperationProgressIndeterminate = false;
            PopulateSnapshots(await repository.ListSnapshotsAsync(cancellationToken));
            },
            cancellable: true,
            cancellationStatus:
            "Restore отменён. Телефон мог быть изменён частично; " +
            "повторите восстановление для завершения.");
    }

    internal static string FormatRestoreReport(CompatibilityReport report)
    {
        var safeCount = report.Items.Count(x => x.Level == CompatibilityLevel.Safe);
        var conditionalCount = report.Items.Count(x => x.Level == CompatibilityLevel.Conditional);
        var blockedCount = report.Items.Count(x => x.Level == CompatibilityLevel.Blocked);
        var lines = new List<string>
        {
            $"Итог: без проблем — {safeCount}; требуют внимания — {conditionalCount}; заблокированы — {blockedCount}."
        };
        lines.AddRange(report.Items
            .Where(x => x.Level != CompatibilityLevel.Safe)
            .Select(x => $"{x.Level}: {x.Component} — {x.Reason}"));
            if (safeCount > 0)
            lines.Add($"Safe: {safeCount} компонентов прошли проверку; подробности скрыты.");
        return string.Join(Environment.NewLine, lines);
    }

    private void UpdateRestoreProgress(TransferProgress value) =>
        UpdateOperationProgress(value, restore: true);

    private void UpdateOperationProgress(TransferProgress value, bool restore)
    {
        var ratio = value.Total > 0
            ? Math.Clamp((value.Completed + 1d) / value.Total, 0, 1)
            : 0;
        var (start, end, label) = value.Stage switch
        {
            "verify" => (2d, 10d, "Проверка backup"),
            "safety-snapshot" => (10d, 18d, "Safety snapshot"),
            "restore-approval" => (18d, 20d, "Подтверждение на телефоне"),
            "restore-apk-stage" => (20d, 62d, "Подготовка APK"),
            "restore-apk-stage-done" => (20d, 62d, "APK подготовлен"),
            "restore-apk" => (20d, 62d, "Установка APK"),
            "restore-apk-needs-user" => (20d, 62d, "Ожидается подтверждение Play Protect"),
            "restore-apk-done" => (20d, 62d, "APK установлен"),
            "restore-apk-failed" => (20d, 62d, "Ошибка APK"),
            "restore-app-data" => (62d, 86d, "Данные приложений"),
            "restore-app-data-done" => (62d, 86d, "Данные восстановлены"),
            "restore-app-data-skipped" => (62d, 86d, "Данные пропущены"),
            "restore-app-data-rolled-back" => (62d, 86d, "Данные откатились"),
            "restore-app-data-failed" => (62d, 86d, "Ошибка отката данных"),
            "restore-shared" => (86d, 92d, "Общая память"),
            "restore-shared-done" => (86d, 92d, "Общая память восстановлена"),
            "restore-shared-failed" => (86d, 92d, "Ошибка общей памяти"),
            "restore-policy" => (92d, 96d, "Разрешения и политики"),
            "restore-personal" => (92d, 96d, "Контакты и звонки"),
            "restore-personal-done" => (92d, 96d, "Личные данные объединены"),
            "restore-personal-failed" => (92d, 96d, "Ошибка личных данных"),
            "restore-system-state" => (96d, 100d, "Системные настройки"),
            "restore-wallpaper" => (96d, 100d, "Обои"),
            "restore-wallpaper-done" => (96d, 100d, "Обои применены"),
            "restore-wallpaper-failed" => (96d, 100d, "Ошибка обоев"),
            "apk" => (0d, 25d, "APK"),
            "app-data" => (25d, 62d, "Приватные данные"),
            "launcher-data" => (62d, 70d, "Launcher"),
            "shared-storage" => (70d, 82d, "Общие файлы"),
            "personal-data" => (82d, 88d, "Личные данные"),
            "system-state" => (88d, 94d, "Настройки системы/ROM"),
            "wallpaper" => (94d, 96d, "Обои home/lock"),
            "full" => (94d, 98d, "Архивные компоненты"),
            "complete" => (98d, 100d, "Проверка и публикация"),
            _ => (0d, 10d, value.Stage)
        };
        OperationProgressIndeterminate = false;
        var nextProgress = start + (end - start) * ratio;
        OperationProgress = restore ? Math.Max(OperationProgress, nextProgress) : nextProgress;
        StatusText = value.Total > 0
            ? $"{label}: {value.Item} ({Math.Min(value.Completed + 1, value.Total)}/{value.Total})"
            : $"{label}: {value.Item}";

        var state = ProgressState(value.Stage);
        var baseStage = NormalizeProgressStage(value.Stage);
        var itemName = ProgressItemName(value.Item, state);
        var key = $"{baseStage}|{itemName}";
        if (!_operationItemsByKey.TryGetValue(key, out var row))
        {
            foreach (var previous in OperationItems.Where(x =>
                         x.State == RestoreItemState.Running &&
                         x.Key.StartsWith(baseStage + "|", StringComparison.Ordinal)))
                previous.State = RestoreItemState.Succeeded;

            row = new OperationProgressItemViewModel
            {
                Key = key,
                Stage = label,
                Item = itemName,
                State = state
            };
            _operationItemsByKey[key] = row;
            OperationItems.Add(row);
        }
        else
        {
            row.Stage = label;
            row.State = state;
        }
        row.Progress = value.Total > 0
            ? $"{Math.Min(value.Completed + 1, value.Total)}/{value.Total}"
            : string.Empty;
        row.Message = string.Equals(itemName, value.Item, StringComparison.Ordinal)
            ? string.Empty
            : value.Item[(itemName.Length + 1)..].Trim();

        if (value.Stage == "complete")
            foreach (var pending in OperationItems.Where(x => x.State == RestoreItemState.Running))
                pending.State = RestoreItemState.Succeeded;
    }

    private static RestoreItemState ProgressState(string stage) => stage switch
    {
        var x when x.EndsWith("-done", StringComparison.Ordinal) => RestoreItemState.Succeeded,
        var x when x.EndsWith("-skipped", StringComparison.Ordinal) => RestoreItemState.Skipped,
        var x when x.EndsWith("-failed", StringComparison.Ordinal) => RestoreItemState.Failed,
        var x when x.EndsWith("-rolled-back", StringComparison.Ordinal) => RestoreItemState.RolledBack,
        var x when x.EndsWith("-needs-user", StringComparison.Ordinal) => RestoreItemState.NeedsUser,
        "restore-approval" => RestoreItemState.NeedsUser,
        _ => RestoreItemState.Running
    };

    private static string NormalizeProgressStage(string stage)
    {
        foreach (var suffix in new[]
                 { "-rolled-back", "-needs-user", "-skipped", "-failed", "-done" })
            if (stage.EndsWith(suffix, StringComparison.Ordinal))
                return stage[..^suffix.Length];
        return stage;
    }

    private static string ProgressItemName(string item, RestoreItemState state)
    {
        if (state is RestoreItemState.Failed or RestoreItemState.RolledBack &&
            item.IndexOf(':') is var separator && separator > 0)
            return item[..separator];
        return item;
    }

    private async Task VerifyRepositoryAsync(object? _)
    {
        await BusyAsync("Полная проверка репозитория…", async () =>
        {
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                RepositoryPath, RepositoryPassword);
            var report = await repository.VerifyAsync();
            var text = report.Errors.Count == 0
                ? $"Проверено объектов: {report.VerifiedObjectCount}. Ошибок нет."
                : $"Ошибок: {report.Errors.Count}\n" + string.Join("\n", report.Errors.Take(20));
            RestoreReportText = text;
            StatusText = text.Split('\n')[0];
            MessageBox.Show(
                text,
                "Проверка репозитория",
                MessageBoxButton.OK,
                report.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Error);
        });
    }

    private async Task GarbageCollectAsync(object? _)
    {
        if (MessageBox.Show(
                "Удалить все chunk-объекты, на которые не ссылается ни один snapshot?",
                "Очистка репозитория",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await BusyAsync("Очистка незадействованных chunks…", async () =>
        {
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                RepositoryPath, RepositoryPassword);
            var report = await repository.GarbageCollectAsync();
            StatusText =
                $"Удалено объектов: {report.DeletedObjects}, освобождено {FormatBytes(report.FreedBytes)}";
        });
    }

    private async Task DeleteSnapshotAsync(object? _)
    {
        if (SelectedSnapshot is null) return;
        if (MessageBox.Show(
                $"Удалить snapshot {SelectedSnapshot.Manifest.SnapshotId[..12]}?\n" +
                "Chunks будут физически удалены только после очистки репозитория.",
                "Удаление snapshot",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await BusyAsync("Удаление snapshot…", async () =>
        {
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                RepositoryPath, RepositoryPassword);
            await repository.DeleteSnapshotAsync(SelectedSnapshot.Manifest.SnapshotId);
            PopulateSnapshots(await repository.ListSnapshotsAsync());
            StatusText = "Snapshot удалён; для освобождения места запустите очистку chunks.";
        });
    }

    private async Task ChangePasswordAsync(object? _)
    {
        if (string.IsNullOrWhiteSpace(NewRepositoryPassword))
        {
            MessageBox.Show("Введите новый пароль.");
            return;
        }
        await BusyAsync("Смена оболочки master key…", async () =>
        {
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                RepositoryPath, RepositoryPassword);
            await repository.ChangePasswordAsync(NewRepositoryPassword);
            RepositoryPassword = NewRepositoryPassword;
            NewRepositoryPassword = string.Empty;
            StatusText = "Пароль изменён; chunks не перешифровывались, recovery key прежний.";
        });
    }

    private async Task RecoverRepositoryAsync(object? _)
    {
        if (string.IsNullOrWhiteSpace(RecoveryInput) ||
            string.IsNullOrWhiteSpace(NewRepositoryPassword))
        {
            MessageBox.Show("Введите 24 слова recovery key и новый пароль.");
            return;
        }
        await BusyAsync("Восстановление master key…", async () =>
        {
            var repository = await EncryptedRepository.OpenWithRecoveryCodeAsync(
                RepositoryPath, RecoveryInput);
            await repository.ChangePasswordAsync(NewRepositoryPassword);
            RepositoryPassword = NewRepositoryPassword;
            RecoveryInput = string.Empty;
            NewRepositoryPassword = string.Empty;
            StatusText = "Доступ восстановлен, master key обёрнут новым паролем.";
        });
    }

    private void LoadDesktopSettings()
    {
        var settings = DesktopSettingsStore.Load();
        RepositoryPath = settings.RepositoryPath;
        MediaDestination = settings.MediaDestination;
        _mediaTransports.Clear();
        foreach (var item in settings.MediaTransports)
            _mediaTransports[item.Key] = item.Value;
    }

    private void SaveDesktopSettings()
    {
        DesktopSettingsStore.Save(new(
            RepositoryPath,
            MediaDestination,
            ThemeManager.SelectedTheme,
            LocalizationManager.Language,
            new Dictionary<string, string>(_mediaTransports, StringComparer.Ordinal)));
    }

    private static string FormatBytes(long value) =>
        value >= 1024L * 1024 * 1024
            ? $"{value / 1024d / 1024 / 1024:0.##} {LocalizationManager.T("ГБ")}"
            : $"{value / 1024d / 1024:0.##} {LocalizationManager.T("МБ")}";

    private static string FormatRate(double value) =>
        value <= 0 ? "—" : $"{value / 1024d / 1024:0.0} MB/s";

    private static string FormatUsbLink(UsbLinkSpeed speed) => speed switch
    {
        UsbLinkSpeed.LowSpeed => "USB 1.0",
        UsbLinkSpeed.FullSpeed => "USB 1.1",
        UsbLinkSpeed.HighSpeed => "USB 2.0",
        UsbLinkSpeed.SuperSpeed => "USB 3.x",
        UsbLinkSpeed.SuperSpeedPlus => "USB 3.2+",
        _ => "USB —"
    };

    private static string DescribeUsbLink(UsbConnectionInfo usb)
    {
        if (!usb.Available)
            return LocalizationManager.IsRussian
                ? $"Режим не определён{(string.IsNullOrWhiteSpace(usb.Detail) ? string.Empty : $": {usb.Detail}")}"
                : $"Link unavailable{(string.IsNullOrWhiteSpace(usb.Detail) ? string.Empty : $": {usb.Detail}")}";
        return usb.LinkSpeed switch
        {
            UsbLinkSpeed.LowSpeed => "Low-Speed • 1.5 Mbit/s",
            UsbLinkSpeed.FullSpeed => "Full-Speed • 12 Mbit/s",
            UsbLinkSpeed.HighSpeed when usb.DeviceSuperSpeedCapable =>
                LocalizationManager.IsRussian
                    ? "High-Speed • 480 Mbit/s • телефон поддерживает USB 3.x"
                    : "High-Speed • 480 Mbit/s • phone supports USB 3.x",
            UsbLinkSpeed.HighSpeed => "High-Speed • 480 Mbit/s",
            UsbLinkSpeed.SuperSpeed => "SuperSpeed • 5 Gbit/s",
            UsbLinkSpeed.SuperSpeedPlus => "SuperSpeedPlus • 10+ Gbit/s",
            _ => LocalizationManager.IsRussian ? "Скорость не определена" : "Speed unavailable"
        };
    }

    private static string DescribeRecommendation(ConnectionBenchmarkResult result)
    {
        var recommended = result.Assessment.RecommendedTransport == MediaTransportMode.FastLan
            ? "Fast Wi-Fi"
            : "ADB";
        var gain = result.AdbBytesPerSecond > 0 && result.FastLanBytesPerSecond > 0
            ? Math.Abs(result.FastLanBytesPerSecond / result.AdbBytesPerSecond - 1) * 100
            : 0;
        if (LocalizationManager.IsRussian)
        {
            return result.Assessment.LimitReason switch
            {
                ConnectionLimitReason.Usb2Link =>
                    $"{recommended} быстрее. Кабель согласовался как USB 2.0, хотя телефон умеет USB 3.x.",
                ConnectionLimitReason.FastLanFaster =>
                    $"Рекомендуется Fast Wi-Fi — он быстрее ADB примерно на {gain:0}%.",
                ConnectionLimitReason.WirelessUnavailable =>
                    "Используйте ADB: Fast Wi-Fi сейчас недоступен.",
                ConnectionLimitReason.Comparable =>
                    $"Рекомендуется {recommended}: разница скоростей небольшая.",
                _ => $"Рекомендуемый транспорт: {recommended}."
            };
        }
        return result.Assessment.LimitReason switch
        {
            ConnectionLimitReason.Usb2Link =>
                $"{recommended} is faster. The cable negotiated USB 2.0 although the phone supports USB 3.x.",
            ConnectionLimitReason.FastLanFaster =>
                $"Fast Wi-Fi is recommended — about {gain:0}% faster than ADB.",
            ConnectionLimitReason.WirelessUnavailable =>
                "Use ADB: Fast Wi-Fi is currently unavailable.",
            ConnectionLimitReason.Comparable =>
                $"{recommended} is recommended: the speed difference is small.",
            _ => $"Recommended transport: {recommended}."
        };
    }

    private static string BuildTransferEstimate(double bytesPerSecond)
    {
        string Estimate(long gib)
        {
            var duration = ConnectionBenchmarkPolicy.Estimate(
                gib * 1024L * 1024 * 1024,
                bytesPerSecond);
            return duration is null ? "—" : $"~{FormatDuration(duration.Value)}";
        }
        return $"10 GB {Estimate(10)}  •  50 GB {Estimate(50)}  •  100 GB {Estimate(100)}";
    }

    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";

    private void RaiseConnectionBenchmark()
    {
        Raise(nameof(ConnectionTestStatus));
        Raise(nameof(ConnectionTestProgress));
        Raise(nameof(UsbLinkMetric));
        Raise(nameof(UsbLinkCaption));
        Raise(nameof(AdbSpeedMetric));
        Raise(nameof(AdbSpeedCaption));
        Raise(nameof(FastLanSpeedMetric));
        Raise(nameof(FastLanSpeedCaption));
        Raise(nameof(ConnectionRecommendation));
        Raise(nameof(ConnectionEstimate));
    }

    public void RefreshLocalization()
    {
        Raise(nameof(PageTitle));
        Raise(nameof(PageSubtitle));
        Raise(nameof(StatusText));
        Raise(nameof(CancelOperationText));
        Raise(nameof(RestoreReportText));
        Raise(nameof(MediaExportReportText));
        Raise(nameof(MediaLiveStats));
        Raise(nameof(LocalCopyReportText));
        Raise(nameof(DevicesFoundLabel));
        Raise(nameof(VersionLabel));
        Raise(nameof(ThemeOptions));
        Raise(nameof(LanguageOptions));
        Raise(nameof(MediaTransportOptions));
        Raise(nameof(SelectedMediaTransport));
        Raise(nameof(SelectedLanguage));
        Raise(nameof(SelectedTheme));
        Raise(nameof(DiagnosticsSummary));
        RaiseConnectionBenchmark();
        PopulateSnapshots(Snapshots.Select(x => x.Manifest).ToArray());
        var selectedDeviceId = SelectedDevice?.Inventory.StableId;
        var devices = Devices.Select(x => x.Inventory).ToArray();
        Devices.Clear();
        foreach (var device in devices) Devices.Add(new(device));
        SelectedDevice = Devices.FirstOrDefault(x => x.Inventory.StableId == selectedDeviceId);
    }

    private void PopulateSnapshots(IReadOnlyList<SnapshotManifest> manifests)
    {
        var selectedId = SelectedSnapshot?.Manifest.SnapshotId;
        Snapshots.Clear();
        SnapshotLines.Clear();
        foreach (var manifest in manifests)
        {
            var item = new SnapshotViewModel(manifest);
            Snapshots.Add(item);
            SnapshotLines.Add($"{item.Title} • {item.Details}");
        }
        SelectedSnapshot = Snapshots.FirstOrDefault(x => x.Manifest.SnapshotId == selectedId)
                           ?? Snapshots.FirstOrDefault();
    }

    private void Show(string page)
    {
        _page = page;
        Raise(nameof(PageTitle));
        Raise(nameof(PageSubtitle));
        Raise(nameof(DevicesVisibility));
        Raise(nameof(BackupVisibility));
        Raise(nameof(MediaVisibility));
        Raise(nameof(RestoreVisibility));
        Raise(nameof(HistoryVisibility));
        Raise(nameof(SettingsVisibility));
        Raise(nameof(DiagnosticsVisibility));
        Raise(nameof(DiagnosticsNavigationVisibility));
        Raise(nameof(IsDevicesPage));
        Raise(nameof(IsBackupPage));
        Raise(nameof(IsMediaPage));
        Raise(nameof(IsRestorePage));
        Raise(nameof(IsHistoryPage));
        Raise(nameof(IsSettingsPage));
        Raise(nameof(IsDiagnosticsPage));
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
            if (window is not null) LocalizationManager.Apply(window);
        });
    }

    private Visibility Visible(string page) => _page == page ? Visibility.Visible : Visibility.Collapsed;

    private Task BusyAsync(string status, Func<Task> action) =>
        BusyAsync(
            status,
            _ => action(),
            cancellable: false,
            cancellationStatus: "Операция отменена.");

    internal async Task BusyAsync(
        string status,
        Func<CancellationToken, Task> action,
        bool cancellable,
        string cancellationStatus)
    {
        if (IsBusy) return;
        var operationId = Guid.NewGuid().ToString("D");
        var stopwatch = Stopwatch.StartNew();
        var cancellationToken = cancellable
            ? _operationCancellation.Begin()
            : CancellationToken.None;
        IsBusy = true;
        OperationItems.Clear();
        _operationItemsByKey.Clear();
        OperationProgress = 0;
        OperationProgressIndeterminate = true;
        RaiseCancellationState();
        StatusText = status;
        DesktopDiagnostics.Log(
            "info",
            "operation",
            "started",
            status,
            operationId: operationId);
        try
        {
            await action(cancellationToken);
            OperationProgress = 100;
            OperationProgressIndeterminate = false;
            DesktopDiagnostics.Log(
                "info",
                "operation",
                "completed",
                status,
                operationId: operationId,
                durationMs: stopwatch.ElapsedMilliseconds,
                result: "ok");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            OperationProgressIndeterminate = false;
            StatusText = cancellationStatus;
            DesktopDiagnostics.Log(
                "info",
                "operation",
                "cancelled",
                status,
                operationId: operationId,
                durationMs: stopwatch.ElapsedMilliseconds,
                result: "cancelled");
        }
        catch (Exception exception)
        {
            OperationProgressIndeterminate = false;
            StatusText = exception.Message;
            DesktopDiagnostics.Log(
                "error",
                "operation",
                "failed",
                status,
                operationId: operationId,
                durationMs: stopwatch.ElapsedMilliseconds,
                result: "failed",
                error: exception);
        }
        finally
        {
            if (cancellable)
                _operationCancellation.Complete();
            IsBusy = false;
            RaiseCancellationState();
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var window = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                if (window is not null) LocalizationManager.Apply(window);
            });
        }
    }

    private void CancelActiveOperation()
    {
        if (!_operationCancellation.Cancel())
            return;
        StatusText = "Отмена… Текущий файл корректно закрывается.";
        DesktopDiagnostics.Log(
            "info",
            "operation",
            "cancel_requested",
            "User requested operation cancellation",
            result: "requested");
        RaiseCancellationState();
    }

    private void RaiseCancellationState()
    {
        Raise(nameof(CanCancelOperation));
        Raise(nameof(CancelOperationVisibility));
        Raise(nameof(CancelOperationText));
        CancelOperationCommand.RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }
}
