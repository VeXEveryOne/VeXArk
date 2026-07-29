using System.Collections.ObjectModel;
using System.ComponentModel;
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
        ? LocalizationManager.T("Root не найден")
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
    public string Title =>
        $"{Manifest.CreatedAtUtc.ToLocalTime():g} • {Manifest.Components.Count} {LocalizationManager.T("приложений")}";
    public string Details =>
        $"{(Manifest.Purpose == "safety" ? "Safety snapshot" : Manifest.Mode.ToString())} • " +
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

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AdbService _adb = new();
    private readonly Dictionary<string, string> _mediaTransports = new(StringComparer.Ordinal);
    private string _page = "devices";
    private string _statusText = "Готово";
    private bool _isBusy;
    private DeviceViewModel? _selectedDevice;
    private SnapshotViewModel? _selectedSnapshot;
    private string _restoreReportText = "Откройте локальное хранилище и выберите копию.";
    private string _mediaExportReportText =
        "Копируются оригиналы из MediaStore. На телефоне ничего не удаляется.";
    private string _localCopyReportText =
        "Выбранный снимок можно сохранить одним зашифрованным файлом.";
    private string _mediaLiveStats =
        "Перед копированием Auto проверит ADB, Fast Wi-Fi и диск назначения.";

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<string> SnapshotLines { get; } = [];
    public ObservableCollection<SnapshotViewModel> Snapshots { get; } = [];
    public ObservableCollection<PackageSelectionViewModel> BackupPackages { get; } = [];

    public string PageTitle => LocalizationManager.T(_page switch
    {
        "backup" => "Новая резервная копия",
        "media" => "Фото и видео",
        "restore" => "Восстановить",
        "history" => "История",
        "settings" => "Настройки",
        _ => "Устройства"
    });
    public string PageSubtitle => LocalizationManager.T(_page switch
    {
        "backup" => SelectedDevice is null ? "Сначала выберите устройство" : SelectedDevice.DisplayName,
        "media" => SelectedDevice is null
            ? "Сначала выберите устройство"
            : $"{SelectedDevice.DisplayName} • root не требуется",
        "restore" => "Compatibility engine не применяет опасные данные автоматически",
        "history" => RepositoryPath,
        "settings" => "Локальный зашифрованный репозиторий",
        _ => "USB и Wireless ADB объединяются по физическому устройству"
    });

    public Visibility DevicesVisibility => Visible("devices");
    public Visibility BackupVisibility => Visible("backup");
    public Visibility MediaVisibility => Visible("media");
    public Visibility RestoreVisibility => Visible("restore");
    public Visibility HistoryVisibility => Visible("history");
    public Visibility SettingsVisibility => Visible("settings");
    public bool IsDevicesPage => _page == "devices";
    public bool IsBackupPage => _page == "backup";
    public bool IsMediaPage => _page == "media";
    public bool IsRestorePage => _page == "restore";
    public bool IsHistoryPage => _page == "history";
    public bool IsSettingsPage => _page == "settings";

    public string StatusText
    {
        get => LocalizationManager.T(_statusText);
        set => Set(ref _statusText, value);
    }
    public string AppVersion =>
        typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.7.0";
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }
    public DeviceViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            Set(ref _selectedDevice, value);
            Raise(nameof(PageSubtitle));
            Raise(nameof(SelectedMediaTransport));
        }
    }
    public SnapshotViewModel? SelectedSnapshot { get => _selectedSnapshot; set => Set(ref _selectedSnapshot, value); }
    public string RestoreReportText
    {
        get => LocalizationManager.T(_restoreReportText);
        set => Set(ref _restoreReportText, value);
    }
    public string DevicesFoundLabel =>
        LocalizationManager.IsRussian ? $"Найдено: {Devices.Count}" : $"Found: {Devices.Count}";
    public string VersionLabel =>
        $"{AppVersion} • {(LocalizationManager.IsRussian ? "полностью офлайн" : "fully offline")}";
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
    public RelayCommand SelectDeviceCommand { get; }
    public AsyncRelayCommand InstallAgentCommand { get; }
    public AsyncRelayCommand CreateRepositoryCommand { get; }
    public AsyncRelayCommand StartBackupCommand { get; }
    public AsyncRelayCommand LoadSnapshotsCommand { get; }
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
    public AsyncRelayCommand ExportSnapshotBundleCommand { get; }
    public AsyncRelayCommand ImportSnapshotBundleCommand { get; }

    public MainViewModel()
    {
        RefreshCommand = new(async _ => await RefreshAsync());
        ShowDevicesCommand = new(_ => Show("devices"));
        ShowBackupCommand = new(_ => Show("backup"));
        ShowMediaCommand = new(_ => Show("media"));
        ShowRestoreCommand = new(_ => Show("restore"));
        ShowHistoryCommand = new(_ => Show("history"));
        ShowSettingsCommand = new(_ => Show("settings"));
        SelectDeviceCommand = new(device =>
        {
            SelectedDevice = device as DeviceViewModel;
            Show("backup");
        });
        InstallAgentCommand = new(InstallAgentAsync);
        CreateRepositoryCommand = new(CreateRepositoryAsync);
        StartBackupCommand = new(StartBackupAsync);
        LoadSnapshotsCommand = new(LoadSnapshotsAsync);
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
        ExportSnapshotBundleCommand = new(ExportSnapshotBundleAsync);
        ImportSnapshotBundleCommand = new(ImportSnapshotBundleAsync);
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
            var packages = await agent.GetPackagesAsync(includeSystemApps: false);
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
        if (SelectedDevice.Inventory.Root == RootState.Unavailable && (BackupAppData || FullMode))
        {
            MessageBox.Show(
                "Root не найден. APK можно сохранить без root, но приватные данные приложений недоступны.",
                "Ограниченный режим",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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

        await BusyAsync("Подключение к Agent…", async () =>
        {
            var serial = SelectedDevice.PreferredSerial;
            if (!await _adb.IsAgentInstalledAsync(serial))
                throw new InvalidOperationException("Android Agent не установлен.");

            await using var agent = await AgentClient.ConnectAsync(_adb, serial);
            using var hello = await agent.HelloAsync();
            var pairingProgress = new Progress<string>(text => StatusText = text);
            if (!await agent.PairWithApprovalAsync(TimeSpan.FromSeconds(60), pairingProgress))
                throw new TimeoutException("Компьютер не был подтверждён на телефоне.");

            StatusText = "Инвентаризация приложений и APK…";
            var packages = await agent.GetPackagesAsync();
            if (BackupPackages.Count > 0)
            {
                var selectedPackages = BackupPackages
                    .Where(x => x.IsSelected)
                    .Select(x => x.PackageName)
                    .ToHashSet(StringComparer.Ordinal);
                packages = packages.Where(x => selectedPackages.Contains(x.PackageName)).ToList();
                if (packages.Count == 0 && (BackupApps || BackupAppData))
                    throw new InvalidOperationException("Не выбрано ни одного приложения.");
            }
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                RepositoryPath, RepositoryPassword);
            var includeRootData = false;
            if ((BackupAppData || FullMode) &&
                SelectedDevice.Inventory.Root != RootState.Unavailable)
            {
                StatusText = "Запрос root на телефоне…";
                includeRootData = await agent.RequestRootAsync();
                if (!includeRootData)
                    MessageBox.Show(
                        "Root не предоставлен. Snapshot продолжится только с APK.",
                        "Ограниченный режим",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
            }
            var transferProgress = new Progress<TransferProgress>(value =>
            {
                StatusText = value.Stage == "complete"
                    ? "Проверка и публикация снимка…"
                    : $"APK: {value.Item} ({value.Completed + 1}/{value.Total})";
            });
            var coordinator = new BackupCoordinator(_adb);
            var manifest = await coordinator.BackupPortableAsync(
                serial,
                SelectedDevice.Inventory,
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
                progress: transferProgress);
            SnapshotLines.Insert(
                0,
                $"{manifest.CreatedAtUtc.ToLocalTime():g} • {manifest.Components.Count} приложений • {manifest.SnapshotId[..8]}");
            StatusText = $"Backup завершён: {manifest.Components.Count} приложений";
        });
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

    private async Task ExportMediaAsync(object? _)
    {
        if (SelectedDevice is null)
        {
            MessageBox.Show("Сначала выберите устройство.");
            return;
        }
        await BusyAsync("Подключение к Agent…", async () =>
        {
            var serial = SelectedDevice.PreferredSerial;
            if (!await _adb.IsAgentInstalledAsync(serial))
                throw new InvalidOperationException(
                    "Android Agent не установлен. Установите его на странице «Устройства».");

            await using var agent = await AgentClient.ConnectAsync(_adb, serial);
            if (!await agent.PairWithApprovalAsync(
                    TimeSpan.FromSeconds(60),
                    new Progress<string>(text => StatusText = text)))
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
                transferMetrics);
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

    private async Task RestoreSnapshotAsync(object? _)
    {
        if (SelectedDevice is null || SelectedSnapshot is null)
        {
            MessageBox.Show("Выберите устройство и snapshot.");
            return;
        }
        await BusyAsync("Проверка совместимости Restore…", async () =>
        {
            var serial = SelectedDevice.PreferredSerial;
            var repository = await EncryptedRepository.OpenWithPasswordAsync(
                RepositoryPath, RepositoryPassword);
            await using var agent = await AgentClient.ConnectAsync(_adb, serial);
            if (!await agent.PairWithApprovalAsync(
                    TimeSpan.FromSeconds(60),
                    new Progress<string>(x => StatusText = x)))
                throw new UnauthorizedAccessException("Компьютер не подтверждён.");

            var snapshot = SelectedSnapshot.Manifest;
            var packageNames = snapshot.Components
                .Where(x => x.Kind == "apk")
                .Select(x => x.Id)
                .ToList();
            var installed = await agent.GetPackagesAsync(
                includeSystemApps: true,
                packageNames);
            var coordinator = new RestoreCoordinator(_adb);
            var report = coordinator.PlanApkRestore(
                snapshot, SelectedDevice.Inventory, installed);
            RestoreReportText = string.Join(
                Environment.NewLine,
                report.Items.Select(x => $"{x.Level}: {x.Component} — {x.Reason}"));
            if (snapshot.Components.Any(x => x.Kind == "account-inventory"))
            {
                RestoreReportText += Environment.NewLine +
                    "INFO: список аккаунтов сохранён только как зашифрованная памятка; " +
                    "пароли и авторизация Google не восстанавливаются.";
            }

            var packageItems = report.Items
                .Where(x => packageNames.Contains(x.Component, StringComparer.Ordinal))
                .ToDictionary(x => x.Component, StringComparer.Ordinal);
            var blocked = packageItems.Values
                .Where(x => x.Level == CompatibilityLevel.Blocked)
                .ToList();
            if (blocked.Count > 0)
                throw new InvalidOperationException(
                    "Restore заблокирован:\n" + string.Join("\n", blocked.Select(x => $"{x.Component}: {x.Reason}")));

            var selected = packageNames
                .Where(x => AllowDowngrade ||
                            !packageItems.TryGetValue(x, out var item) ||
                            item.Level != CompatibilityLevel.Conditional)
                .ToList();
            if (selected.Count == 0)
                throw new InvalidOperationException(
                    "Все приложения требуют downgrade. Включите его вручную после проверки отчёта.");

            var restoreProgress = new Progress<TransferProgress>(value =>
                StatusText = value.Item);
            await coordinator.RestoreApksAsync(
                serial,
                snapshot,
                SelectedDevice.Inventory,
                installed,
                repository,
                agent,
                selected,
                AllowDowngrade,
                createSafetySnapshot: true,
                progress: restoreProgress);
            if (snapshot.Components.Any(x =>
                    x.Kind == "app-data" &&
                    x.Package is not null &&
                    selected.Contains(x.Package.PackageName, StringComparer.Ordinal)))
            {
                var restoredPackages = await agent.GetPackagesAsync(
                    includeSystemApps: true,
                    packageNames: selected);
                await coordinator.RestoreAppDataAsync(
                    snapshot,
                    restoredPackages,
                    repository,
                    agent,
                    selected,
                    restoreProgress);
            }
            if (snapshot.Components.Any(x => x.Kind == "shared-storage"))
            {
                await coordinator.RestoreSharedStorageAsync(
                    snapshot,
                    repository,
                    agent,
                    restoreProgress);
            }
            var policyFailures = await coordinator.RestorePackagePoliciesAsync(
                snapshot,
                agent,
                selected,
                restoreProgress);
            if (policyFailures.Count > 0)
            {
                RestoreReportText += Environment.NewLine +
                    "Не все package policy применились:" + Environment.NewLine +
                    string.Join(
                        Environment.NewLine,
                        policyFailures.Select(x => $"{x.Key}: {string.Join(", ", x.Value)}"));
            }
            var systemFailures = await coordinator.RestoreSystemStateAsync(
                snapshot,
                repository,
                agent,
                restoreProgress);
            if (systemFailures.Count > 0)
            {
                RestoreReportText += Environment.NewLine +
                    "Настройки, которые не удалось применить: " +
                    string.Join(", ", systemFailures);
            }
            StatusText = $"Restore завершён: {selected.Count} приложений";
            PopulateSnapshots(await repository.ListSnapshotsAsync());
        });
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

    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";

    public void RefreshLocalization()
    {
        Raise(nameof(PageTitle));
        Raise(nameof(PageSubtitle));
        Raise(nameof(StatusText));
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
        Raise(nameof(IsDevicesPage));
        Raise(nameof(IsBackupPage));
        Raise(nameof(IsMediaPage));
        Raise(nameof(IsRestorePage));
        Raise(nameof(IsHistoryPage));
        Raise(nameof(IsSettingsPage));
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
            if (window is not null) LocalizationManager.Apply(window);
        });
    }

    private Visibility Visible(string page) => _page == page ? Visibility.Visible : Visibility.Collapsed;

    private async Task BusyAsync(string status, Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = status;
        try { await action(); }
        catch (Exception exception) { StatusText = exception.Message; }
        finally
        {
            IsBusy = false;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var window = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                if (window is not null) LocalizationManager.Apply(window);
            });
        }
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
