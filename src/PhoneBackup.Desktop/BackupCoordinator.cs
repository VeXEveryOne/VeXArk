using PhoneBackup.Core;
using Blake3;
using System.Security.Cryptography;

namespace PhoneBackup.Desktop;

public sealed class BackupCoordinator(AdbService adb)
{
    public async Task<SnapshotManifest> BackupApksAsync(
        string serial,
        DeviceInventory device,
        IReadOnlyList<PackageSnapshot> packages,
        EncryptedRepository repository,
        bool includeSystemApps,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string purpose = "manual")
    {
        await using var repositoryLease = await repository.AcquireOperationLeaseAsync(cancellationToken);
        var snapshots = await repository.ListSnapshotsAsync(cancellationToken);
        var previous = LatestForDevice(snapshots, device);
        var selected = SelectPackages(packages, includeSystemApps);
        var components = await BuildApkComponentsAsync(
            serial, selected, repository, previous, progress, cancellationToken);
        return await CommitAsync(
            device, components, repository, purpose, progress, selected.Count, cancellationToken);
    }

    public async Task<SnapshotManifest> BackupPortableAsync(
        string serial,
        DeviceInventory device,
        IReadOnlyList<PackageSnapshot> packages,
        EncryptedRepository repository,
        AgentClient agent,
        bool includeSystemApps,
        bool includeApks,
        bool includeAppData,
        bool includeSharedStorage,
        bool includePersonalData,
        bool includeSystemState,
        bool includeFullComponents,
        bool includeCaches,
        bool fullHash,
        PackageSnapshot? launcherPackage = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string purpose = "manual")
    {
        await using var repositoryLease = await repository.AcquireOperationLeaseAsync(cancellationToken);
        var snapshots = await repository.ListSnapshotsAsync(cancellationToken);
        var previous = LatestForDevice(snapshots, device);
        var selected = SelectPackages(packages, includeSystemApps);
        var failures = new List<BackupFailure>();
        var components = includeApks
            ? await BuildApkComponentsAsync(
                serial, selected, repository, previous, progress, cancellationToken)
            : new List<BackupComponentManifest>();
        if (includeAppData)
        {
            var regularPackages = launcherPackage is null
                ? selected
                : selected.Where(x => !string.Equals(
                    x.PackageName,
                    launcherPackage.PackageName,
                    StringComparison.Ordinal)).ToList();
            var appData = await BuildAppDataComponentsAsync(
                regularPackages,
                repository,
                agent,
                previous,
                includeCaches,
                fullHash,
                progress,
                failures,
                cancellationToken,
                "app-data");
            components.AddRange(appData);
            if (launcherPackage is not null)
            {
                var launcherData = await BuildAppDataComponentsAsync(
                    [launcherPackage],
                    repository,
                    agent,
                    previous,
                    includeCaches,
                    fullHash,
                    progress,
                    failures,
                    cancellationToken,
                    "launcher-data");
                components.AddRange(launcherData);
            }
        }
        var excludedMedia = new ExcludedMediaReport(0, 0, []);
        if (includeSharedStorage)
        {
            var shared = await BuildSharedStorageComponentsAsync(
                repository, agent, previous, progress, failures, cancellationToken);
            components.AddRange(shared.Components);
            excludedMedia = shared.Excluded;
        }
        if (includePersonalData)
        {
            var personal = await BuildPersonalDataComponentsAsync(
                repository, agent, progress, cancellationToken);
            components.AddRange(personal);
            components.Add(await BuildAccountInventoryComponentAsync(
                repository, agent, progress, cancellationToken));
        }
        if (includeSystemState)
        {
            components.Add(await BuildSystemStateComponentAsync(
                repository, agent, progress, cancellationToken));
            if (includeAppData)
                components.AddRange(await BuildWallpaperComponentsAsync(
                    repository, agent, progress, cancellationToken));
        }
        if (includeFullComponents)
        {
            var full = await BuildFullComponentsAsync(
                repository, agent, previous, progress, failures, cancellationToken);
            components.AddRange(full);
        }
        if (failures.Count > 0)
            components.Add(await BuildFailureReportComponentAsync(
                repository, failures, cancellationToken));
        return await CommitAsync(
            device, components, repository, purpose, progress, selected.Count,
            cancellationToken, excludedMedia,
            includeFullComponents ? BackupMode.Full : BackupMode.Portable);
    }

    private async Task<List<BackupComponentManifest>> BuildApkComponentsAsync(
        string serial,
        IReadOnlyList<PackageSnapshot> selected,
        EncryptedRepository repository,
        SnapshotManifest? previous,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var reusable = previous?.Components
            .Where(x => x.Kind == "apk")
            .SelectMany(x => x.Files)
            .Where(x => !string.IsNullOrWhiteSpace(x.ContentHash) && x.Chunks is not null)
            .ToDictionary(x => (x.RelativePath, x.ContentHash!), StringTupleComparer.Ordinal)
            ?? new Dictionary<(string, string), FileEntry>(StringTupleComparer.Ordinal);

        var components = new List<BackupComponentManifest>(selected.Count);
        var itemIndex = 0L;
        foreach (var package in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("apk", package.Label, itemIndex, selected.Count));
            var files = new List<FileEntry>();
            foreach (var artifact in package.ApkArtifacts)
            {
                var relativePath = $"{package.PackageName}/{SafeFileName(artifact.FileName)}";
                if (reusable.TryGetValue((relativePath, artifact.Sha256), out var old) &&
                    old.Size == artifact.Size)
                {
                    files.Add(old);
                    continue;
                }

                await using var input = await adb.OpenRemoteFileAsync(
                    serial, artifact.Path, cancellationToken);
                await using var hashing = new ContentHashingReadStream(input, sha256: true);
                var chunks = await repository.PutStreamAsync(hashing, cancellationToken);
                var actualHash = hashing.FinalizeHash();
                if (hashing.BytesRead != artifact.Size ||
                    !string.Equals(actualHash, artifact.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"APK changed while backing up: {package.PackageName}/{artifact.FileName}.");
                files.Add(new(
                    relativePath,
                    "file",
                    artifact.Size,
                    artifact.ModifiedUnixNanos,
                    0,
                    package.Uid,
                    package.Uid,
                    null,
                    null,
                    actualHash,
                    chunks));
            }
            components.Add(Component(package, "apk", package.PackageName, files));
            itemIndex++;
        }
        return components;
    }

    private static async Task<List<BackupComponentManifest>> BuildAppDataComponentsAsync(
        IReadOnlyList<PackageSnapshot> selected,
        EncryptedRepository repository,
        AgentClient agent,
        SnapshotManifest? previous,
        bool includeCaches,
        bool fullHash,
        IProgress<TransferProgress>? progress,
        List<BackupFailure> failures,
        CancellationToken cancellationToken,
        string componentKind)
    {
        var components = new List<BackupComponentManifest>();
        await using var scanner = await agent.ConnectSiblingAsync(cancellationToken);
        await using var reader = await agent.ConnectSiblingAsync(cancellationToken);
        var packageIndex = 0L;
        foreach (var package in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(componentKind, package.Label, packageIndex, selected.Count));
            await agent.BeginPackageSnapshotAsync(package.PackageName, cancellationToken);
            try
            {
                var roots = package.DataRoots is { Count: > 0 }
                    ? package.DataRoots
                    : package.DataPaths.Select((path, index) => new AppDataRoot(
                        index == 0 ? DataRootKind.CredentialEncrypted :
                        index == 1 ? DataRootKind.DeviceEncrypted : DataRootKind.Unknown,
                        path,
                        package.UserId)).ToList();
                for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
                {
                    var dataRoot = roots[rootIndex];
                    var root = dataRoot.Path;
                    var componentId = $"{package.PackageName}:{componentKind}:{rootIndex}";
                    var failureCountBefore = failures.Count;
                    long scannedFiles = 0;
                    long scannedBytes = 0;
                    var reusable = previous?.Components
                        .FirstOrDefault(x => x.Id == componentId && x.Kind == componentKind)
                        ?.Files.ToDictionary(x => x.RelativePath, StringComparer.Ordinal)
                        ?? new Dictionary<string, FileEntry>(StringComparer.Ordinal);
                    var files = new List<FileEntry>();
                    try
                    {
                        await foreach (var entry in scanner.ScanRootAsync(
                                           root, includeCaches, fullHash, cancellationToken))
                        {
                            if (entry.Kind == "file")
                            {
                                scannedFiles++;
                                scannedBytes += entry.Size;
                            }
                            if (!RestorePathPolicy.IsSafeRelativePath(entry.RelativePath))
                                throw new InvalidDataException(
                                    $"Agent returned unsafe path: {entry.RelativePath}");
                            if (entry.Kind == "symlink" &&
                                string.Equals(entry.RelativePath, "lib", StringComparison.Ordinal))
                            {
                                // PackageManager owns this target-ROM symlink; preserve the target copy.
                                continue;
                            }
                            if (entry.Kind is "special" or "symlink")
                            {
                                failures.Add(new(
                                    componentId,
                                    package.PackageName,
                                    entry.RelativePath,
                                    "root_scan",
                                    entry.Kind == "symlink"
                                        ? "unsupported_symlink"
                                        : "unsupported_special_file",
                                    entry.Kind == "symlink"
                                        ? "Symlink was archived in inventory only; automatic restore is disabled."
                                        : "Transient socket/device/special entry was not archived."));
                                continue;
                            }
                            if (reusable.TryGetValue(entry.RelativePath, out var old) &&
                                CanReuse(old, entry))
                            {
                                files.Add(old);
                                continue;
                            }
                            if (entry.Kind != "file")
                            {
                                files.Add(entry with { Chunks = null });
                                continue;
                            }
                            var stored = await TryPutFileAsync(
                                () => reader.OpenRootFileAsync(
                                    root, entry.RelativePath, cancellationToken),
                                repository,
                                entry,
                                new(
                                    componentId,
                                    package.PackageName,
                                    entry.RelativePath,
                                    "root_read",
                                    "",
                                    ""),
                                failures,
                                cancellationToken);
                            if (stored is null) continue;
                            files.Add(entry with
                            {
                                Chunks = stored.Chunks,
                                ContentHash = stored.ContentHash
                            });
                        }
                    }
                    catch (Exception error) when (
                        IsRecoverableScanFailure(error, cancellationToken))
                    {
                        failures.Add(new(
                            componentId,
                            package.PackageName,
                            "",
                            "root_scan",
                            error.GetType().FullName ?? error.GetType().Name,
                            error.Message));
                    }
                    var componentFailures = failures.Skip(failureCountBefore)
                        .Select(x => new ComponentFailure(
                            x.Stage,
                            string.IsNullOrWhiteSpace(x.RelativePath) ? null : x.RelativePath,
                            x.ErrorType,
                            x.Message))
                        .ToList();
                    var capturedFiles = files.LongCount(x => x.Kind == "file" && x.Chunks is not null);
                    var capturedBytes = files.Where(x => x.Kind == "file" && x.Chunks is not null)
                        .Sum(x => x.Size);
                    components.Add(Component(
                        package,
                        componentKind,
                        componentId,
                        files,
                        componentFailures.Count == 0 && capturedFiles == scannedFiles
                            ? ComponentStatus.Complete
                            : ComponentStatus.Partial,
                        componentKind == "launcher-data"
                            ? RestoreClass.ExactRom
                            : RestoreClass.Portable,
                        dataRoot.Kind,
                        componentFailures,
                        new(scannedFiles, capturedFiles, scannedBytes, capturedBytes,
                            componentFailures.Count == 0)));
                }
            }
            finally
            {
                await EndPackageSnapshotAsync(
                    agent,
                    package.PackageName,
                    cancellationToken);
            }
            packageIndex++;
        }
        return components;
    }

    private static async Task EndPackageSnapshotAsync(
        AgentClient agent,
        string packageName,
        CancellationToken operationToken)
    {
        if (!operationToken.IsCancellationRequested)
        {
            await agent.EndPackageSnapshotAsync(packageName, operationToken);
            return;
        }

        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await agent.EndPackageSnapshotAsync(packageName, cleanupTimeout.Token);
        }
        catch (Exception error)
        {
            DesktopDiagnostics.Log(
                "warn",
                "backup",
                "package_cleanup_failed",
                "Could not release package snapshot after cancellation",
                result: "failed",
                error: error,
                fields: new Dictionary<string, object?>
                {
                    ["packageName"] = packageName
                });
        }
    }

    private static bool CanReuse(FileEntry old, FileEntry current)
    {
        if (old.Kind != current.Kind || old.Size != current.Size ||
            old.ModifiedUnixNanos != current.ModifiedUnixNanos)
            return false;
        if (!string.IsNullOrWhiteSpace(current.ContentHash) &&
            !string.Equals(old.ContentHash, current.ContentHash, StringComparison.OrdinalIgnoreCase))
            return false;
        return current.Kind != "file" ||
               old.Chunks is not null && !string.IsNullOrWhiteSpace(old.ContentHash);
    }

    private static async Task<SharedBuildResult> BuildSharedStorageComponentsAsync(
        EncryptedRepository repository,
        AgentClient agent,
        SnapshotManifest? previous,
        IProgress<TransferProgress>? progress,
        List<BackupFailure> failures,
        CancellationToken cancellationToken)
    {
        var capability = await agent.GetSharedStorageAsync(cancellationToken);
        if (!capability.AccessGranted)
            throw new UnauthorizedAccessException(
                "На Agent не выдан «Доступ ко всем файлам». Откройте Agent и нажмите «Доступ к файлам».");

        var components = new List<BackupComponentManifest>();
        await using var scanner = await agent.ConnectSiblingAsync(cancellationToken);
        await using var reader = await agent.ConnectSiblingAsync(cancellationToken);
        long excludedCount = 0;
        long excludedBytes = 0;
        var samples = new List<string>();
        var index = 0L;
        foreach (var root in capability.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("shared-storage", root, index, capability.Roots.Count));
            var componentId = $"shared:{root.Replace('\\', '/').Trim('/')}";
            var failureCountBefore = failures.Count;
            long scannedFiles = 0;
            long scannedBytes = 0;
            var reusable = previous?.Components
                .FirstOrDefault(x => x.Id == componentId && x.Kind == "shared-storage")
                ?.Files.ToDictionary(x => x.RelativePath, StringComparer.Ordinal)
                ?? new Dictionary<string, FileEntry>(StringComparer.Ordinal);
            var files = new List<FileEntry>();
            try
            {
                await foreach (var entry in scanner.ScanSharedAsync(root, cancellationToken))
                {
                    if (!RestorePathPolicy.IsSafeRelativePath(entry.RelativePath))
                        throw new InvalidDataException(
                            $"Agent returned unsafe shared path: {entry.RelativePath}");
                    if (entry.Kind == "excluded")
                    {
                        excludedCount++;
                        excludedBytes += entry.Size;
                        if (samples.Count < 20) samples.Add($"{root}/{entry.RelativePath}");
                        continue;
                    }
                    if (entry.Kind == "file")
                    {
                        scannedFiles++;
                        scannedBytes += entry.Size;
                    }
                    if (reusable.TryGetValue(entry.RelativePath, out var old) &&
                        CanReuse(old, entry))
                    {
                        files.Add(old);
                        continue;
                    }
                    if (entry.Kind != "file")
                    {
                        files.Add(entry with { Chunks = null });
                        continue;
                    }
                    var stored = await TryPutFileAsync(
                        () => reader.OpenSharedFileAsync(
                            root, entry.RelativePath, cancellationToken),
                        repository,
                        entry,
                        new(
                            componentId,
                            "shared-storage",
                            entry.RelativePath,
                            "shared_read",
                            "",
                            ""),
                        failures,
                        cancellationToken);
                    if (stored is null) continue;
                    files.Add(entry with
                    {
                        Chunks = stored.Chunks,
                        ContentHash = stored.ContentHash
                    });
                }
            }
            catch (Exception error) when (
                IsRecoverableScanFailure(error, cancellationToken))
            {
                failures.Add(new(
                    componentId,
                    "shared-storage",
                    "",
                    "shared_scan",
                    error.GetType().FullName ?? error.GetType().Name,
                    error.Message));
            }
            var componentFailures = failures.Skip(failureCountBefore)
                .Select(x => new ComponentFailure(
                    x.Stage,
                    string.IsNullOrWhiteSpace(x.RelativePath) ? null : x.RelativePath,
                    x.ErrorType,
                    x.Message))
                .ToList();
            var capturedFiles = files.LongCount(x => x.Kind == "file" && x.Chunks is not null);
            var capturedBytes = files.Where(x => x.Kind == "file" && x.Chunks is not null)
                .Sum(x => x.Size);
            components.Add(new(
                componentId,
                "shared-storage",
                files,
                capturedBytes,
                files.SelectMany(x => x.Chunks ?? []).Sum(x => (long)x.StoredLength),
                Status: componentFailures.Count == 0 && capturedFiles == scannedFiles
                    ? ComponentStatus.Complete
                    : ComponentStatus.Partial,
                RestoreClass: RestoreClass.Portable,
                SchemaId: "vexark.shared-storage",
                SchemaVersion: 2,
                Failures: componentFailures,
                Verification: new(
                    scannedFiles, capturedFiles, scannedBytes, capturedBytes,
                    componentFailures.Count == 0 && capturedFiles == scannedFiles)));
            index++;
        }
        return new(
            components,
            new(excludedCount, excludedBytes, samples));
    }

    private static async Task<IReadOnlyList<BackupComponentManifest>> BuildPersonalDataComponentsAsync(
        EncryptedRepository repository,
        AgentClient agent,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var capability = await agent.GetPersonalDataAsync(cancellationToken);
        var exports = new[]
        {
            (Kind: "contacts", FileName: "contacts.vcf", Granted: capability.Contacts),
            (Kind: "messages", FileName: "messages.jsonl", Granted: capability.Messages),
            (Kind: "calls", FileName: "calls.jsonl", Granted: capability.Calls)
        };
        var components = new List<BackupComponentManifest>();
        for (var index = 0; index < exports.Length; index++)
        {
            var item = exports[index];
            progress?.Report(new("personal-data", item.Kind, index, exports.Length));
            if (!item.Granted)
            {
                components.Add(new(
                    $"personal:{item.Kind}",
                    "personal-data",
                    [],
                    0,
                    0,
                    Status: ComponentStatus.Skipped,
                    RestoreClass: item.Kind == "messages"
                        ? RestoreClass.ArchiveOnly
                        : RestoreClass.Portable,
                    SchemaId: $"vexark.personal.{item.Kind}",
                    SchemaVersion: 2,
                    Failures:
                    [
                        new("permission", null, "permission_not_granted",
                            $"Android permission for {item.Kind} was not granted.")
                    ],
                    Verification: new(0, 0, 0, 0, false)));
                continue;
            }
            try
            {
                await using var input = await agent.OpenPersonalDataAsync(item.Kind, cancellationToken);
                var chunks = await repository.PutStreamAsync(input, cancellationToken);
                var plainBytes = chunks.Sum(x => (long)x.PlainLength);
                var file = new FileEntry(
                    item.FileName,
                    "file",
                    plainBytes,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    chunks);
                components.Add(new(
                    $"personal:{item.Kind}",
                    "personal-data",
                    [file],
                    plainBytes,
                    chunks.Sum(x => (long)x.StoredLength),
                    Status: ComponentStatus.Complete,
                    RestoreClass: item.Kind == "messages"
                        ? RestoreClass.ArchiveOnly
                        : RestoreClass.Portable,
                    SchemaId: $"vexark.personal.{item.Kind}",
                    SchemaVersion: 2,
                    Verification: new(1, 1, plainBytes, plainBytes, true)));
            }
            catch (Exception error) when (
                error is not OperationCanceledException &&
                !cancellationToken.IsCancellationRequested)
            {
                components.Add(new(
                    $"personal:{item.Kind}",
                    "personal-data",
                    [],
                    0,
                    0,
                    Status: ComponentStatus.Failed,
                    RestoreClass: item.Kind == "messages"
                        ? RestoreClass.ArchiveOnly
                        : RestoreClass.Portable,
                    SchemaId: $"vexark.personal.{item.Kind}",
                    SchemaVersion: 2,
                    Failures: [new("personal_export", null, "export_failed", error.Message)],
                    Verification: new(1, 0, 0, 0, false)));
            }
        }

        var permissionReport = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            contacts = capability.Contacts,
            messages = capability.Messages,
            calls = capability.Calls
        });
        await using var reportStream = new MemoryStream(permissionReport, writable: false);
        var reportChunks = await repository.PutStreamAsync(reportStream, cancellationToken);
        components.Add(new(
            "personal:permissions",
            "personal-data-report",
            [
                new(
                    "permissions.json",
                    "file",
                    permissionReport.LongLength,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    reportChunks)
            ],
            permissionReport.LongLength,
            reportChunks.Sum(x => (long)x.StoredLength)));
        return components;
    }

    private static async Task<BackupComponentManifest> BuildSystemStateComponentAsync(
        EncryptedRepository repository,
        AgentClient agent,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new("system-state", "Безопасные настройки и роли", 0, 1));
        var state = await agent.ExportSystemStateAsync(cancellationToken);
        await using var input = new MemoryStream(state, writable: false);
        var chunks = await repository.PutStreamAsync(input, cancellationToken);
        return new(
            "portable.system_state",
            "system-state",
            [
                new(
                    "system-state.json",
                    "file",
                    state.LongLength,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                    0, 0, 0, null, null, null, chunks)
            ],
            state.LongLength,
            chunks.Sum(x => (long)x.StoredLength));
    }

    private static async Task<IReadOnlyList<BackupComponentManifest>> BuildWallpaperComponentsAsync(
        EncryptedRepository repository,
        AgentClient agent,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var components = new List<BackupComponentManifest>();
        var kinds = new[] { "system", "lock" };
        for (var index = 0; index < kinds.Length; index++)
        {
            var kind = kinds[index];
            progress?.Report(new("wallpaper", kind, index, kinds.Length));
            try
            {
                await using var source = await agent.OpenWallpaperAsync(kind, cancellationToken);
                await using var hashing = new ContentHashingReadStream(source, sha256: false);
                var chunks = await repository.PutStreamAsync(hashing, cancellationToken);
                if (hashing.BytesRead == 0) continue;
                var hash = hashing.FinalizeHash();
                var file = new FileEntry(
                    $"{kind}.wallpaper",
                    "file",
                    hashing.BytesRead,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                    0, 0, 0, null, null, hash, chunks);
                components.Add(new(
                    $"wallpaper:{kind}",
                    "wallpaper",
                    [file],
                    file.Size,
                    chunks.Sum(x => (long)x.StoredLength)));
            }
            catch (Exception error) when (
                error is IOException or InvalidOperationException &&
                !cancellationToken.IsCancellationRequested)
            {
                var optionalLock = kind == "lock";
                components.Add(new(
                    $"wallpaper:{kind}",
                    "wallpaper",
                    [],
                    0,
                    0,
                    Status: optionalLock ? ComponentStatus.Skipped : ComponentStatus.Failed,
                    RestoreClass: RestoreClass.Portable,
                    SchemaId: "vexark.wallpaper",
                    SchemaVersion: 2,
                    Failures:
                    [
                        new(
                            "wallpaper_read",
                            null,
                            optionalLock ? "not_independently_available" : "read_failed",
                            error.Message)
                    ],
                    Verification: new(0, 0, 0, 0, optionalLock)));
            }
        }
        return components;
    }

    private static async Task<BackupComponentManifest> BuildAccountInventoryComponentAsync(
        EncryptedRepository repository,
        AgentClient agent,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(
            "account-inventory",
            "Список аккаунтов без паролей и токенов",
            0,
            1));
        var inventory = await agent.ExportAccountInventoryAsync(cancellationToken);
        await using var input = new MemoryStream(inventory, writable: false);
        var chunks = await repository.PutStreamAsync(input, cancellationToken);
        return new(
            "portable.account_inventory",
            "account-inventory",
            [
                new(
                    "accounts.json",
                    "file",
                    inventory.LongLength,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                    0, 0, 0, null, null, null, chunks)
            ],
            inventory.LongLength,
            chunks.Sum(x => (long)x.StoredLength));
    }

    private static async Task<IReadOnlyList<BackupComponentManifest>> BuildFullComponentsAsync(
        EncryptedRepository repository,
        AgentClient agent,
        SnapshotManifest? previous,
        IProgress<TransferProgress>? progress,
        List<BackupFailure> failures,
        CancellationToken cancellationToken)
    {
        var roots = new[]
        {
            (Id: "full.wifi", Root: "/data/misc/apexdata/com.android.wifi/"),
            (Id: "full.root_modules", Root: "/data/adb/modules/")
        };
        var components = new List<BackupComponentManifest>();
        await using var scanner = await agent.ConnectSiblingAsync(cancellationToken);
        await using var reader = await agent.ConnectSiblingAsync(cancellationToken);
        for (var index = 0; index < roots.Length; index++)
        {
            var source = roots[index];
            var failureCountBefore = failures.Count;
            long scannedFiles = 0;
            long scannedBytes = 0;
            progress?.Report(new("full", source.Id, index, roots.Length));
            var reusable = previous?.Components
                .FirstOrDefault(x => x.Id == source.Id && x.Kind == "full")
                ?.Files.ToDictionary(x => x.RelativePath, StringComparer.Ordinal)
                ?? new Dictionary<string, FileEntry>(StringComparer.Ordinal);
            var files = new List<FileEntry>();
            try
            {
                await foreach (var entry in scanner.ScanRootAsync(
                                   source.Root,
                                   includeCaches: false,
                                   fullHash: true,
                                   cancellationToken))
                {
                    if (entry.Kind == "file")
                    {
                        scannedFiles++;
                        scannedBytes += entry.Size;
                    }
                    if (!RestorePathPolicy.IsSafeRelativePath(entry.RelativePath))
                        throw new InvalidDataException(
                            $"Agent returned unsafe Full path: {entry.RelativePath}");
                    if (reusable.TryGetValue(entry.RelativePath, out var old) &&
                        CanReuse(old, entry))
                    {
                        files.Add(old);
                        continue;
                    }
                    if (entry.Kind != "file")
                    {
                        files.Add(entry with { Chunks = null });
                        continue;
                    }
                    var stored = await TryPutFileAsync(
                        () => reader.OpenRootFileAsync(
                            source.Root, entry.RelativePath, cancellationToken),
                        repository,
                        entry,
                        new(
                            source.Id,
                            "full",
                            entry.RelativePath,
                            "root_read",
                            "",
                            ""),
                        failures,
                        cancellationToken);
                    if (stored is null) continue;
                    files.Add(entry with
                    {
                        Chunks = stored.Chunks,
                        ContentHash = stored.ContentHash
                    });
                }
            }
            catch (Exception error) when (
                IsRecoverableScanFailure(error, cancellationToken))
            {
                failures.Add(new(
                    source.Id,
                    "full",
                    "",
                    "root_scan",
                    error.GetType().FullName ?? error.GetType().Name,
                    error.Message));
            }
            var componentFailures = failures.Skip(failureCountBefore)
                .Select(x => new ComponentFailure(
                    x.Stage,
                    string.IsNullOrWhiteSpace(x.RelativePath) ? null : x.RelativePath,
                    x.ErrorType,
                    x.Message))
                .ToList();
            var capturedFiles = files.LongCount(x => x.Kind == "file" && x.Chunks is not null);
            var capturedBytes = files.Where(x => x.Kind == "file" && x.Chunks is not null)
                .Sum(x => x.Size);
            components.Add(new(
                source.Id,
                "full",
                files,
                capturedBytes,
                files.SelectMany(x => x.Chunks ?? []).Sum(x => (long)x.StoredLength),
                Status: componentFailures.Count == 0 && capturedFiles == scannedFiles
                    ? ComponentStatus.Complete
                    : ComponentStatus.Partial,
                RestoreClass: RestoreClass.ArchiveOnly,
                SchemaId: $"vexark.{source.Id}",
                SchemaVersion: 2,
                Failures: componentFailures,
                Verification: new(
                    scannedFiles, capturedFiles, scannedBytes, capturedBytes,
                    componentFailures.Count == 0 && capturedFiles == scannedFiles)));
        }
        return components;
    }

    private static async Task<StoredFile?> TryPutFileAsync(
        Func<Task<Stream>> open,
        EncryptedRepository repository,
        FileEntry entry,
        BackupFailure failure,
        List<BackupFailure> failures,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await using var input = await open();
                await using var hashing = new ContentHashingReadStream(input, sha256: false);
                var chunks = await repository.PutStreamAsync(hashing, cancellationToken);
                var copiedBytes = hashing.BytesRead;
                if (copiedBytes != entry.Size)
                    throw new IOException(
                        $"File changed during backup: scanned={entry.Size}, read={copiedBytes}.");
                var actualHash = hashing.FinalizeHash();
                if (!string.IsNullOrWhiteSpace(entry.ContentHash) &&
                    !string.Equals(actualHash, entry.ContentHash,
                            StringComparison.OrdinalIgnoreCase))
                    throw new IOException(
                        $"File content changed during backup: {entry.RelativePath}.");
                return new(chunks, actualHash);
            }
            catch (IOException error) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = error;
                if (attempt == 1)
                    await Task.Delay(100, cancellationToken);
            }
        }

        failures.Add(failure with
        {
            ErrorType = lastError?.GetType().FullName ?? "System.IO.IOException",
            Message = lastError?.Message ?? "File could not be read after two attempts."
        });
        return null;
    }

    private sealed record StoredFile(
        IReadOnlyList<ChunkReference> Chunks,
        string ContentHash);

    private static bool IsRecoverableScanFailure(
        Exception error,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        (error is InvalidOperationException ||
         error is IOException);

    private static async Task<BackupComponentManifest> BuildFailureReportComponentAsync(
        EncryptedRepository repository,
        IReadOnlyList<BackupFailure> failures,
        CancellationToken cancellationToken)
    {
        var report = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                count = failures.Count,
                failures
            },
            new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web));
        await using var input = new MemoryStream(report, writable: false);
        var chunks = await repository.PutStreamAsync(input, cancellationToken);
        return new(
            "backup.failures",
            "backup-report",
            [
                new(
                    "backup-failures.json",
                    "file",
                    report.LongLength,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null,
                    chunks)
            ],
            report.LongLength,
            chunks.Sum(x => (long)x.StoredLength),
            Status: ComponentStatus.Complete,
            RestoreClass: RestoreClass.ArchiveOnly,
            SchemaId: "vexark.backup-failures",
            SchemaVersion: 2,
            Verification: new(1, 1, report.LongLength, report.LongLength, true));
    }

    private static BackupComponentManifest Component(
        PackageSnapshot package,
        string kind,
        string id,
        IReadOnlyList<FileEntry> files,
        ComponentStatus status = ComponentStatus.Complete,
        RestoreClass restoreClass = RestoreClass.Portable,
        DataRootKind dataRootKind = DataRootKind.Unknown,
        IReadOnlyList<ComponentFailure>? failures = null,
        ComponentVerification? verification = null) =>
        new(
            id,
            kind,
            files,
            files.Where(x => x.Kind == "file" && x.Chunks is not null).Sum(x => x.Size),
            files.SelectMany(x => x.Chunks ?? []).Sum(x => (long)x.StoredLength),
            new(
                package.PackageName,
                package.VersionCode,
                package.VersionName,
                package.SigningCertificateSha256,
                package.IsEnabled,
                package.RuntimePermissions,
                package.BatteryOptimizationExempt,
                package.SigningLineageSha256,
                package.TargetSdk,
                package.AppLocales),
            status,
            restoreClass,
            $"vexark.{kind}",
            2,
            package.UserId,
            dataRootKind,
            failures,
            verification ?? new(
                files.LongCount(x => x.Kind == "file"),
                files.LongCount(x => x.Kind == "file" && x.Chunks is not null),
                files.Where(x => x.Kind == "file").Sum(x => x.Size),
                files.Where(x => x.Kind == "file" && x.Chunks is not null).Sum(x => x.Size),
                status == ComponentStatus.Complete));

    private static async Task<SnapshotManifest> CommitAsync(
        DeviceInventory device,
        IReadOnlyList<BackupComponentManifest> components,
        EncryptedRepository repository,
        string purpose,
        IProgress<TransferProgress>? progress,
        int packageCount,
        CancellationToken cancellationToken,
        ExcludedMediaReport? excludedMedia = null,
        BackupMode mode = BackupMode.Portable)
    {
        var normalized = components.Select(NormalizeComponent).ToList();
        var complete = normalized.Count(x => x.Status == ComponentStatus.Complete);
        var partial = normalized.Count(x => x.Status is ComponentStatus.Partial or ComponentStatus.Unknown);
        var failed = normalized.Count(x => x.Status == ComponentStatus.Failed);
        var skipped = normalized.Count(x => x.Status == ComponentStatus.Skipped);
        var meaningfulSkipped = normalized.Count(x =>
            x.Status == ComponentStatus.Skipped &&
            x.Failures?.All(failure =>
                failure.ErrorCode == "not_independently_available") != true);
        var overall = failed > 0 && complete == 0
            ? ComponentStatus.Failed
            : partial > 0 || failed > 0 || meaningfulSkipped > 0
                ? ComponentStatus.Partial
                : ComponentStatus.Complete;
        var warnings = normalized.Where(x => x.Status != ComponentStatus.Complete)
            .Select(x => $"{x.Id}: {x.Status}")
            .Take(98)
            .ToList();
        warnings.Add(
            "Android Keystore/StrongBox, hardware-bound secrets, OAuth sessions, PIN/biometrics, Wallet, DRM and eSIM credentials are not portable; affected apps may require sign-in or secret re-entry after restore.");
        if (normalized.Any(x => x.Kind == "app-data"))
            warnings.Add(
                "Private app files are captured, but keys encrypted by Android Keystore cannot be restored; app-data success does not guarantee that encrypted accounts or tunnels remain usable.");
        var summary = new SnapshotSummary(
            overall,
            complete,
            partial,
            failed,
            skipped,
            warnings.Take(100).ToList());
        var manifest = new SnapshotManifest(
            2,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            mode,
            device,
            normalized,
            excludedMedia ?? new(0, 0, []),
            purpose,
            device.Rom,
            summary);
        await repository.CommitSnapshotAsync(manifest, cancellationToken);
        progress?.Report(new("complete", manifest.SnapshotId, packageCount, packageCount));
        return manifest;
    }

    private static BackupComponentManifest NormalizeComponent(BackupComponentManifest component)
    {
        if (component.Status != ComponentStatus.Unknown) return component;
        var expectedFiles = component.Files.LongCount(x => x.Kind == "file");
        var capturedFiles = component.Files.LongCount(x => x.Kind == "file" && x.Chunks is not null);
        var complete = expectedFiles == capturedFiles && component.Files.All(x =>
            x.Kind is not ("special" or "symlink") &&
            (x.Kind != "file" || x.Chunks is not null));
        var restoreClass = component.Kind switch
        {
            "apk" or "app-data" or "shared-storage" or "personal-data" or "system-state" =>
                RestoreClass.Portable,
            "wallpaper" => RestoreClass.Portable,
            "launcher-data" => RestoreClass.ExactRom,
            _ => RestoreClass.ArchiveOnly
        };
        return component with
        {
            Status = complete ? ComponentStatus.Complete : ComponentStatus.Partial,
            RestoreClass = restoreClass,
            SchemaId = component.SchemaId ?? $"vexark.{component.Kind}",
            SchemaVersion = 2,
            Verification = component.Verification ?? new(
                expectedFiles,
                capturedFiles,
                component.Files.Where(x => x.Kind == "file").Sum(x => x.Size),
                component.Files.Where(x => x.Kind == "file" && x.Chunks is not null).Sum(x => x.Size),
                complete)
        };
    }

    private sealed class ContentHashingReadStream(Stream inner, bool sha256) : Stream
    {
        private readonly IncrementalHash? _sha256 = sha256
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            : null;
        private readonly Hasher? _blake3 = sha256 ? null : Hasher.New();
        private bool _finalized;
        public long BytesRead { get; private set; }

        public string FinalizeHash()
        {
            if (_finalized) throw new InvalidOperationException("Hash already finalized.");
            _finalized = true;
            return _sha256 is not null
                ? Convert.ToHexString(_sha256.GetHashAndReset()).ToLowerInvariant()
                : _blake3!.Finalize().ToString();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Append(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Append(buffer.Span[..read]);
            return read;
        }

        private void Append(ReadOnlySpan<byte> value)
        {
            if (value.IsEmpty) return;
            BytesRead += value.Length;
            _sha256?.AppendData(value);
            _blake3?.Update(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sha256?.Dispose();
                _blake3?.Dispose();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            _sha256?.Dispose();
            _blake3?.Dispose();
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static SnapshotManifest? LatestForDevice(
        IReadOnlyList<SnapshotManifest> snapshots,
        DeviceInventory device) =>
        snapshots.FirstOrDefault(x =>
            x.Device.StableId == device.StableId && x.Purpose == "manual");

    private static List<PackageSnapshot> SelectPackages(
        IReadOnlyList<PackageSnapshot> packages,
        bool includeSystemApps) =>
        packages
            .Where(x => includeSystemApps || !x.IsSystem)
            .Where(x => x.PackageName != AdbService.AgentPackage)
            .OrderBy(x => x.PackageName)
            .ToList();

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var filtered = new string(value.Select(x => invalid.Contains(x) ? '_' : x).ToArray());
        return string.IsNullOrWhiteSpace(filtered) ? "base.apk" : filtered;
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Path, string Hash)>
    {
        public static readonly StringTupleComparer Ordinal = new();
        public bool Equals((string Path, string Hash) x, (string Path, string Hash) y) =>
            string.Equals(x.Path, y.Path, StringComparison.Ordinal) &&
            string.Equals(x.Hash, y.Hash, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Path, string Hash) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Path),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Hash));
    }

    private sealed record SharedBuildResult(
        IReadOnlyList<BackupComponentManifest> Components,
        ExcludedMediaReport Excluded);

    private sealed record BackupFailure(
        string ComponentId,
        string Source,
        string RelativePath,
        string Stage,
        string ErrorType,
        string Message);
}
