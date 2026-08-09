using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhoneBackup.Core;
using PhoneBackup.Desktop;

const string RemovedGoogleHome = "com.google.android.apps.chromecast.app";

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;
Console.Title = "VeXArk — продолжение Restore";

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "Usage: PhoneBackup.ResumeRestore <repository> <exact-adb-serial|auto> " +
        "[status-directory] [previous-attempt-log ...]");
    return 2;
}

var repositoryPath = Path.GetFullPath(args[0]);
var requestedSerial = args[1];
var statusDirectory = Path.GetFullPath(args.Length >= 3
    ? args[2]
    : Path.Combine(AppContext.BaseDirectory, "status"));
Directory.CreateDirectory(statusDirectory);
var continuationArguments = args.Skip(3).ToList();
var onlyDataPackages = continuationArguments
    .Where(x => x.StartsWith("--only-data=", StringComparison.Ordinal))
    .Select(x => x["--only-data=".Length..])
    .Where(x => !string.IsNullOrWhiteSpace(x))
    .ToHashSet(StringComparer.Ordinal);
var previousDataResults = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var path in continuationArguments.Where(x =>
             !x.StartsWith("--only-data=", StringComparison.Ordinal)))
foreach (var pair in ReadCompletedDataResults(path))
    previousDataResults[pair.Key] = pair.Value;
foreach (var packageName in onlyDataPackages)
    previousDataResults.Remove(packageName);

var run = new RestoreRunLog(statusDirectory);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
    run.Update("cancelling", "Останавливаю Restore безопасно…");
};

try
{
    run.Update("unlock", "Введите пароль локального репозитория в этом окне.");
    Console.WriteLine("VeXArk: продолжение незавершённого Restore");
    Console.WriteLine($"Репозиторий: {repositoryPath}");
    Console.WriteLine("Пароль не сохраняется и не попадает в командную строку или лог.");

    EncryptedRepository? repository = null;
    for (var attempt = 1; repository is null; attempt++)
    {
        var password = PromptForPassword(repositoryPath)
            ?? throw new OperationCanceledException("Ввод пароля отменён.");
        try
        {
            repository = await EncryptedRepository.OpenWithPasswordAsync(
                repositoryPath, password, cancellation.Token);
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Не удалось открыть репозиторий: пароль неверен либо header повреждён.");
            Console.ResetColor();
            run.Update("unlock", $"Неудачная попытка разблокировки #{attempt}.");
        }
        finally
        {
            password = string.Empty;
        }
    }

    var snapshots = await repository.ListSnapshotsAsync(cancellation.Token);
    var snapshot = snapshots
        .Where(x => string.Equals(x.Purpose, "manual", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(x => x.CreatedAtUtc)
        .FirstOrDefault()
        ?? throw new InvalidOperationException("В репозитории нет пользовательской копии.");
    run.SetSnapshot(snapshot.SnapshotId);
    run.Update("connect", $"Копия {snapshot.CreatedAtUtc.LocalDateTime:g}: подключаю телефон.");

    RuntimeBootstrap.EnsureExtracted();
    var adb = new AdbService();
    var devices = await adb.DiscoverAsync(cancellation.Token);
    var device = ResolveDevice(devices, requestedSerial);
    var serial = device.Transports.First(x => x.IsPreferred).AdbSerial;
    run.Update("pair", $"Телефон {device.Model} ({device.Device}). Ожидаю Agent.");

    await using var agent = await AgentClient.ConnectAsync(adb, serial, cancellation.Token);
    var paired = await agent.PairWithApprovalAsync(
        TimeSpan.FromMinutes(3),
        new InlineProgress<string>(message => run.NeedsUser("pair", message)),
        cancellation.Token);
    if (!paired)
        throw new UnauthorizedAccessException("Подключение компьютера не подтверждено на телефоне.");

    var root = await agent.RequestRootDetailsAsync(cancellation.Token);
    if (!root.Granted)
        throw new UnauthorizedAccessException(
            $"KernelSU не выдал root Agent Dev: {root.Provider}; {root.Detail}");

    var rom = await agent.GetRomIdentityAsync(cancellation.Token);
    var target = device with { Root = RootState.Granted, Rom = rom };
    var coordinator = new RestoreCoordinator(adb);
    var progress = new InlineProgress<TransferProgress>(value =>
        run.Progress(value.Stage, value.Item, value.Completed, value.Total));

    var apkComponents = snapshot.Components
        .Where(x => x.Kind == "apk" && x.Package is not null)
        .Where(x => !string.Equals(x.Package!.PackageName, RemovedGoogleHome,
            StringComparison.Ordinal))
        .ToList();
    var allPackageNames = apkComponents.Select(x => x.Package!.PackageName)
        .Concat(snapshot.Components
            .Where(x => x.Kind is "app-data" or "launcher-data" && x.Package is not null)
            .Select(x => x.Package!.PackageName))
        .Where(x => !string.Equals(x, RemovedGoogleHome, StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    run.Update("inventory", $"Сверяю {allPackageNames.Count} приложений с телефоном.");
    var installed = await agent.GetPackagesAsync(
        includeSystemApps: true,
        packageNames: allPackageNames,
        cancellationToken: cancellation.Token,
        hashArtifacts: false);
    var installedByName = installed.ToDictionary(x => x.PackageName, StringComparer.Ordinal);

    var installPackages = new List<string>();
    var packageNotes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [RemovedGoogleHome] = "skipped: удалён пользователем"
    };
    foreach (var component in apkComponents)
    {
        var metadata = component.Package!;
        if (snapshot.FormatVersion >= 2 && component.Status != ComponentStatus.Complete)
        {
            packageNotes[metadata.PackageName] = $"skipped: APK component {component.Status}";
            continue;
        }
        if (!installedByName.TryGetValue(metadata.PackageName, out var current))
        {
            installPackages.Add(metadata.PackageName);
            packageNotes[metadata.PackageName] = "pending: отсутствует";
            continue;
        }
        if (!SignatureMatches(metadata, current))
        {
            packageNotes[metadata.PackageName] = "skipped: signature mismatch";
            continue;
        }
        if (current.VersionCode < metadata.VersionCode)
        {
            installPackages.Add(metadata.PackageName);
            packageNotes[metadata.PackageName] =
                $"pending: update {current.VersionCode} -> {metadata.VersionCode}";
        }
        else if (current.VersionCode == metadata.VersionCode)
            packageNotes[metadata.PackageName] = "already installed: exact version";
        else
            packageNotes[metadata.PackageName] =
                $"APK skipped: installed version {current.VersionCode} is newer than backup {metadata.VersionCode}";
    }
    run.SetPackageNotes(packageNotes);
    run.Update(
        "compatibility",
        $"Уже подходят {apkComponents.Count - installPackages.Count}; установить/обновить {installPackages.Count}; Google Home пропущен.");

    // Even when there is nothing left to install, this call creates and validates the
    // safety snapshot for shared/personal/system state and requests one restore approval.
    var failedApks = await coordinator.RestoreApksAsync(
        serial,
        snapshot,
        target,
        installed,
        repository,
        agent,
        installPackages,
        allowDowngrade: false,
        createSafetySnapshot: true,
        progress,
        cancellation.Token);
    foreach (var packageName in installPackages)
        packageNotes[packageName] = failedApks.Contains(packageName, StringComparer.Ordinal)
            ? "failed: APK install"
            : "succeeded: APK installed";
    run.SetPackageNotes(packageNotes);

    run.Update("inventory", "Повторно проверяю пакеты после APK-этапа.");
    var currentPackages = await agent.GetPackagesAsync(
        includeSystemApps: true,
        packageNames: allPackageNames,
        cancellationToken: cancellation.Token,
        hashArtifacts: false);
    var currentByName = currentPackages.ToDictionary(x => x.PackageName, StringComparer.Ordinal);
    var eligibleDataPackages = snapshot.Components
        .Where(x => x.Kind is "app-data" or "launcher-data" && x.Package is not null)
        .Select(x => x.Package!)
        .Where(x => !string.Equals(x.PackageName, RemovedGoogleHome, StringComparison.Ordinal))
        .Where(x => currentByName.TryGetValue(x.PackageName, out var current) &&
                    SignatureMatches(x, current))
        .Select(x => x.PackageName)
        .Distinct(StringComparer.Ordinal)
        .ToList();
    var dataPackages = eligibleDataPackages
        .Where(x => !previousDataResults.ContainsKey(x))
        .Where(x => onlyDataPackages.Count == 0 || onlyDataPackages.Contains(x))
        .ToList();

    run.Update(
        "app-data",
        $"Восстанавливаю данные {dataPackages.Count} приложений транзакционно; " +
        $"уже завершено ранее {previousDataResults.Count}.");
    IReadOnlyDictionary<string, string> dataResults = dataPackages.Count == 0
        ? new Dictionary<string, string>(StringComparer.Ordinal)
        : await coordinator.RestoreAppDataAsync(
            snapshot,
            currentPackages,
            repository,
            agent,
            dataPackages,
            target.Rom,
            progress,
            cancellation.Token);
    var combinedDataResults = new Dictionary<string, string>(previousDataResults,
        StringComparer.Ordinal);
    foreach (var pair in dataResults) combinedDataResults[pair.Key] = pair.Value;
    run.SetDataResults(combinedDataResults);

    if (onlyDataPackages.Count > 0)
    {
        var targetedSucceeded = dataResults.Count(x =>
            x.Value.StartsWith("succeeded", StringComparison.Ordinal));
        run.Complete(
            $"Точечный app-data Restore завершён: {targetedSucceeded}/{dataResults.Count} успешно.");
        return targetedSucceeded == dataResults.Count ? 0 : 1;
    }

    run.Update("personal-media", "Восстанавливаю общую память и пользовательские файлы.");
    await using var sharedAgent = await ConnectPhaseAgentAsync(
        adb, serial, run, "personal-media", cancellation.Token);
    var sharedResults = await coordinator.RestoreSharedStorageAsync(
        snapshot, repository, sharedAgent, progress, cancellation.Token);
    run.SetSharedResults(sharedResults);

    IReadOnlyList<PersonalRestoreResult> personalResults = [];
    if (snapshot.Components.Any(x =>
            x.Kind == "personal-data" && x.RestoreClass == RestoreClass.Portable))
    {
        run.Update("personal", "Объединяю контакты и звонки с дедупликацией; существующее не удаляется.");
        await using var personalAgent = await ConnectPhaseAgentAsync(
            adb, serial, run, "personal", cancellation.Token);
        personalResults = await coordinator.RestorePersonalDataAsync(
            snapshot, repository, personalAgent, progress, cancellation.Token);
        run.SetPersonalResults(personalResults);
    }

    run.Update("policies", "Возвращаю разрешения, enabled state, locales и безопасные app policies.");
    await using var policyAgent = await ConnectPhaseAgentAsync(
        adb, serial, run, "policies", cancellation.Token);
    var policyFailures = await coordinator.RestorePackagePoliciesAsync(
        snapshot, policyAgent, eligibleDataPackages, progress, cancellation.Token);
    run.SetPolicyFailures(policyFailures);

    run.Update("aosp-settings", "Применяю безопасные AOSP/ROM-настройки с read-back и rollback.");
    await using var settingsAgent = await ConnectPhaseAgentAsync(
        adb, serial, run, "aosp-settings", cancellation.Token);
    var systemResult = await coordinator.RestoreSystemStateAsync(
        snapshot,
        repository,
        settingsAgent,
        confirmedSettingIds: null,
        progress,
        cancellation.Token);
    // NeedsConfirmation is intentionally not auto-approved. The safe subset has already
    // been applied; risky services, roles, VPN and lock-screen controls remain unchanged.
    run.SetSystemResult(systemResult);

    run.Update("wallpaper", "Восстанавливаю обои через Android API.");
    await using var wallpaperAgent = await ConnectPhaseAgentAsync(
        adb, serial, run, "wallpaper", cancellation.Token);
    var wallpaperResults = await coordinator.RestoreWallpapersAsync(
        snapshot, repository, wallpaperAgent, progress, cancellation.Token);
    run.SetWallpaperResults(wallpaperResults);

    var succeededData = combinedDataResults.Count(x =>
        x.Value.StartsWith("succeeded", StringComparison.Ordinal));
    var failedData = combinedDataResults.Count - succeededData;
    run.Complete(
        $"Restore завершён: app-data {succeededData}/{combinedDataResults.Count}, " +
        $"APK ошибок {failedApks.Count}, app-data пропусков/rollback {failedData}, " +
        $"опасных настроек оставлено без изменения {systemResult.NeedsConfirmation.Count}.");
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine();
    Console.WriteLine(run.Message);
    Console.ResetColor();
    Console.WriteLine($"Отчёт: {run.ReportPath}");
    Console.WriteLine("Окно можно закрыть.");
    return failedApks.Count == 0 ? 0 : 1;
}
catch (OperationCanceledException)
{
    run.Fail("Restore отменён. Уже завершённые транзакции сохранены; активная отменена безопасно.");
    Console.Error.WriteLine(run.Message);
    return 130;
}
catch (Exception error)
{
    run.Fail($"Restore остановлен: {error.Message}", error);
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine();
    Console.Error.WriteLine(run.Message);
    Console.ResetColor();
    Console.Error.WriteLine($"Отчёт: {run.ReportPath}");
    Console.Error.WriteLine("Нажмите Enter, чтобы закрыть окно.");
    Console.ReadLine();
    return 1;
}

static string? PromptForPassword(string repositoryPath)
{
    string? result = null;
    Exception? threadError = null;
    var thread = new Thread(() =>
    {
        try
        {
            var window = new Window
            {
                Title = "VeXArk — пароль копии",
                Width = 520,
                Height = 260,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                ShowInTaskbar = true,
                Background = Brushes.White,
                FontFamily = new FontFamily("Segoe UI")
            };
            var panel = new StackPanel { Margin = new Thickness(28) };
            panel.Children.Add(new TextBlock
            {
                Text = "Продолжение Restore",
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"Введите пароль локальной копии:\n{repositoryPath}",
                FontSize = 14,
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            var password = new PasswordBox
            {
                FontSize = 18,
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 16)
            };
            panel.Children.Add(password);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancel = new Button
            {
                Content = "Отмена",
                MinWidth = 100,
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 10, 0)
            };
            var unlock = new Button
            {
                Content = "Продолжить Restore",
                MinWidth = 170,
                Padding = new Thickness(12, 7, 12, 7),
                IsDefault = true
            };
            cancel.Click += (_, _) => window.DialogResult = false;
            unlock.Click += (_, _) =>
            {
                if (password.Password.Length == 0) return;
                result = password.Password;
                password.Clear();
                window.DialogResult = true;
            };
            password.KeyDown += (_, eventArgs) =>
            {
                if (eventArgs.Key != Key.Enter || password.Password.Length == 0) return;
                result = password.Password;
                password.Clear();
                window.DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(unlock);
            panel.Children.Add(buttons);
            window.Content = panel;
            window.Loaded += (_, _) =>
            {
                password.Focus();
                window.Activate();
            };
            _ = window.ShowDialog();
        }
        catch (Exception error)
        {
            threadError = error;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (threadError is not null) throw threadError;
    return result;
}

static DeviceInventory ResolveDevice(
    IReadOnlyList<DeviceInventory> devices,
    string requestedSerial)
{
    if (devices.Count == 0)
        throw new InvalidOperationException("ADB-телефон не найден.");
    if (string.Equals(requestedSerial, "auto", StringComparison.OrdinalIgnoreCase))
    {
        if (devices.Count != 1)
            throw new InvalidOperationException(
                $"Подключено устройств: {devices.Count}. Укажите точный ADB serial.");
        return devices[0];
    }
    return devices.FirstOrDefault(device => device.Transports.Any(transport =>
               string.Equals(transport.AdbSerial, requestedSerial, StringComparison.Ordinal)))
           ?? throw new InvalidOperationException(
               $"ADB-устройство {requestedSerial} не найдено.");
}

static bool SignatureMatches(PackageBackupMetadata metadata, PackageSnapshot current)
{
    var source = (metadata.SigningLineageSha256 ?? [])
        .Append(metadata.SigningCertificateSha256)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    return (current.SigningLineageSha256 ?? [])
        .Append(current.SigningCertificateSha256)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Any(source.Contains);
}

static IReadOnlyDictionary<string, string> ReadCompletedDataResults(string path)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    if (!File.Exists(path)) return result;

    if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("appData", out var appData) ||
            appData.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in appData.EnumerateObject())
        {
            var value = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value)) result[property.Name] = value;
        }
        return result;
    }

    foreach (var line in File.ReadLines(path))
    {
        var outcome = line.Contains("\trestore-app-data-done\t", StringComparison.Ordinal)
            ? "succeeded: previous attempt"
            : line.Contains("\trestore-app-data-rolled-back\t", StringComparison.Ordinal)
                ? "rolled_back: previous attempt"
                : line.Contains("\trestore-app-data-failed\t", StringComparison.Ordinal)
                    ? "failed: previous attempt"
                    : line.Contains("\trestore-app-data-skipped\t", StringComparison.Ordinal)
                        ? "skipped: previous attempt"
                        : null;
        if (outcome is null) continue;
        var markerStart = line.IndexOf("\trestore-app-data-", StringComparison.Ordinal);
        var valueStart = line.IndexOf('\t', markerStart + 1);
        if (valueStart < 0) continue;
        var value = line[(valueStart + 1)..];
        var end = value.IndexOfAny([' ', '\t', '(']);
        var packageName = end < 0 ? value : value[..end];
        if (!string.IsNullOrWhiteSpace(packageName)) result[packageName] = outcome;
    }
    return result;
}

static async Task<AgentClient> ConnectPhaseAgentAsync(
    AdbService adb,
    string serial,
    RestoreRunLog run,
    string phase,
    CancellationToken cancellationToken)
{
    var client = await AgentClient.ConnectAsync(adb, serial, cancellationToken);
    try
    {
        var paired = await client.PairWithApprovalAsync(
            TimeSpan.FromMinutes(3),
            new InlineProgress<string>(message => run.NeedsUser(phase, message)),
            cancellationToken);
        if (!paired)
            throw new UnauthorizedAccessException("Подключение компьютера не подтверждено на телефоне.");
        var root = await client.RequestRootDetailsAsync(cancellationToken);
        if (!root.Granted)
            throw new UnauthorizedAccessException(
                $"KernelSU не выдал root Agent Dev: {root.Provider}; {root.Detail}");
        return client;
    }
    catch
    {
        await client.DisposeAsync();
        throw;
    }
}

file sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

file sealed class RestoreRunLog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _packageNotes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _dataResults = new(StringComparer.Ordinal);
    private readonly List<object> _sharedResults = [];
    private readonly List<object> _personalResults = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _policyFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _wallpaperResults = new(StringComparer.Ordinal);
    private SystemStateRestoreResult? _systemResult;
    private string? _snapshotId;
    private string _state = "starting";
    private string _stage = "starting";
    private string _message = "Запуск";
    private long _completed;
    private long _total;
    private string? _error;

    public RestoreRunLog(string directory)
    {
        StatusPath = Path.Combine(directory, "resume-restore-status.json");
        LogPath = Path.Combine(directory, "resume-restore.log");
        ReportPath = Path.Combine(directory, "resume-restore-report.json");
        File.WriteAllText(LogPath, string.Empty, Encoding.UTF8);
        WriteFiles();
    }

    public string StatusPath { get; }
    public string LogPath { get; }
    public string ReportPath { get; }
    public string Message { get { lock (_gate) return _message; } }

    public void SetSnapshot(string id) { lock (_gate) { _snapshotId = id; WriteFiles(); } }

    public void Update(string stage, string message)
    {
        lock (_gate)
        {
            _state = "running";
            _stage = stage;
            _message = message;
            _completed = 0;
            _total = 0;
            AppendLine(stage, message);
            WriteFiles();
        }
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {stage}: {message}");
    }

    public void NeedsUser(string stage, string message)
    {
        lock (_gate)
        {
            _state = "needs_user";
            _stage = stage;
            _message = message;
            AppendLine(stage, message);
            WriteFiles();
        }
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] НУЖНО ДЕЙСТВИЕ НА ТЕЛЕФОНЕ: {message}");
        Console.ResetColor();
    }

    public void Progress(string stage, string item, long completed, long total)
    {
        lock (_gate)
        {
            _state = stage.Contains("needs-user", StringComparison.Ordinal)
                ? "needs_user"
                : "running";
            _stage = stage;
            _message = item;
            _completed = completed;
            _total = total;
            if (stage == "restore-app-data-done")
                _dataResults[item.Split(':', 2)[0]] = "succeeded";
            else if (stage is "restore-app-data-rolled-back" or "restore-app-data-failed" or
                     "restore-app-data-skipped")
                _dataResults[item.Split(':', 2)[0]] = stage["restore-app-data-".Length..];
            AppendLine(stage, total > 0 ? $"{item} ({completed}/{total})" : item);
            WriteFiles();
        }
        Console.WriteLine(total > 0
            ? $"[{DateTime.Now:HH:mm:ss}] {stage}: {item} ({completed}/{total})"
            : $"[{DateTime.Now:HH:mm:ss}] {stage}: {item}");
    }

    public void SetPackageNotes(IReadOnlyDictionary<string, string> values) =>
        Replace(_packageNotes, values);
    public void SetDataResults(IReadOnlyDictionary<string, string> values) =>
        Replace(_dataResults, values);
    public void SetPolicyFailures(
        IReadOnlyDictionary<string, IReadOnlyList<string>> values) =>
        Replace(_policyFailures, values);
    public void SetWallpaperResults(IReadOnlyDictionary<string, string> values) =>
        Replace(_wallpaperResults, values);

    public void SetSharedResults(IReadOnlyList<ComponentRestoreResult> values)
    {
        lock (_gate)
        {
            _sharedResults.Clear();
            _sharedResults.AddRange(values.Select(x => new { x.ComponentId, state = x.State.ToString(), x.Reason }));
            WriteFiles();
        }
    }

    public void SetPersonalResults(IReadOnlyList<PersonalRestoreResult> values)
    {
        lock (_gate)
        {
            _personalResults.Clear();
            _personalResults.AddRange(values.Select(x => new
            {
                x.Kind, x.Status, x.Inserted, x.Skipped, x.Failed, x.Reason
            }));
            WriteFiles();
        }
    }

    public void SetSystemResult(SystemStateRestoreResult value)
    {
        lock (_gate) { _systemResult = value; WriteFiles(); }
    }

    public void Complete(string message)
    {
        lock (_gate)
        {
            _state = "completed";
            _stage = "report";
            _message = message;
            AppendLine("completed", message);
            WriteFiles();
        }
    }

    public void Fail(string message, Exception? error = null)
    {
        lock (_gate)
        {
            _state = "failed";
            _message = message;
            _error = error?.ToString();
            AppendLine("failed", message);
            WriteFiles();
        }
    }

    private void Replace<T>(Dictionary<string, T> target, IReadOnlyDictionary<string, T> values)
    {
        lock (_gate)
        {
            target.Clear();
            foreach (var pair in values) target[pair.Key] = pair.Value;
            WriteFiles();
        }
    }

    private void AppendLine(string stage, string message) =>
        File.AppendAllText(
            LogPath,
            $"{DateTimeOffset.Now:O}\t{stage}\t{message}{Environment.NewLine}",
            Encoding.UTF8);

    private void WriteFiles()
    {
        var payload = new
        {
            updatedAt = DateTimeOffset.Now,
            state = _state,
            stage = _stage,
            message = _message,
            completed = _completed,
            total = _total,
            snapshotId = _snapshotId,
            error = _error,
            packages = _packageNotes,
            appData = _dataResults,
            shared = _sharedResults,
            personal = _personalResults,
            policyFailures = _policyFailures,
            system = _systemResult,
            wallpapers = _wallpaperResults
        };
        WriteAtomic(StatusPath, payload);
        WriteAtomic(ReportPath, payload);
    }

    private static void WriteAtomic(string path, object payload)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }
}
