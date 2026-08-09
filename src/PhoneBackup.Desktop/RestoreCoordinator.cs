using System.Security.Cryptography;
using PhoneBackup.Core;

namespace PhoneBackup.Desktop;

public sealed class RestoreCoordinator(AdbService adb)
{
    public async Task<SettingsCoverageSummary?> ReadSettingsCoverageAsync(
        SnapshotManifest snapshot,
        EncryptedRepository repository,
        CancellationToken cancellationToken = default)
    {
        var component = snapshot.Components.FirstOrDefault(x => x.Kind == "system-state");
        var file = component?.Files.SingleOrDefault();
        if (file?.Chunks is null) return null;
        await repository.VerifyReferencesAsync(file.Chunks, cancellationToken)
            .ConfigureAwait(false);
        await using var output = new MemoryStream();
        await repository.RestoreStreamAsync(file.Chunks, output, cancellationToken)
            .ConfigureAwait(false);
        using var state = System.Text.Json.JsonDocument.Parse(output.ToArray());
        var supported = 0;
        var archived = 0;
        var ignored = 0;
        if (state.RootElement.TryGetProperty("coverage", out var coverage) &&
            coverage.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in coverage.EnumerateArray())
            {
                var status = item.TryGetProperty("status", out var value)
                    ? value.GetString()
                    : null;
                if (status == "supported") supported++;
                else if (status == "archived") archived++;
                else if (status == "ignored") ignored++;
            }
        }
        var unknown = new List<string>();
        if (state.RootElement.TryGetProperty("unknownKeys", out var namespaces) &&
            namespaces.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var provider in namespaces.EnumerateObject())
            {
                if (provider.Value.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                unknown.AddRange(provider.Value.EnumerateArray()
                    .Select(x => $"{provider.Name}:{x.GetString()}")
                    .Where(x => !x.EndsWith(':')));
            }
        }
        return new(supported, archived, ignored, unknown.Count, unknown.Take(20).ToList());
    }

    public static void CleanupStaleStaging()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneBackup",
            "staging");
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
            DeleteStaging(directory);
    }

    public CompatibilityReport PlanApkRestore(
        SnapshotManifest snapshot,
        DeviceInventory target,
        IReadOnlyList<PackageSnapshot> installed)
    {
        var items = CompatibilityEngine.Compare(snapshot.Device, target).Items
            .Where(x => x.Component == "portable")
            .ToList();
        var current = installed.ToDictionary(x => x.PackageName, StringComparer.Ordinal);
        foreach (var component in snapshot.Components.Where(x => x.Kind == "apk"))
        {
            var metadata = component.Package;
            if (snapshot.FormatVersion >= 2 && component.Status != ComponentStatus.Complete)
            {
                items.Add(new(component.Id, CompatibilityLevel.Blocked,
                    $"APK component is {component.Status}; неполный файл не устанавливается."));
                continue;
            }
            if (metadata is null)
            {
                items.Add(new(component.Id, CompatibilityLevel.Blocked, "В snapshot нет metadata подписи."));
                continue;
            }
            if (!current.TryGetValue(metadata.PackageName, out var present))
            {
                items.Add(new(metadata.PackageName, CompatibilityLevel.Safe, "Приложение будет установлено из backup."));
                continue;
            }
            if (!SignatureMatches(metadata, present))
            {
                items.Add(new(metadata.PackageName, CompatibilityLevel.Blocked, "Подпись установленного APK не совпадает."));
            }
            else if (present.VersionCode > metadata.VersionCode)
            {
                items.Add(new(metadata.PackageName, CompatibilityLevel.Conditional, "Установлена более новая версия; downgrade выключен."));
            }
            else
            {
                items.Add(new(metadata.PackageName, CompatibilityLevel.Safe, "Подпись приложения совпадает."));
            }
        }
        var overall = items.Any(x => x.Level == CompatibilityLevel.Blocked)
            ? CompatibilityLevel.Blocked
            : items.Any(x => x.Level == CompatibilityLevel.Conditional)
                ? CompatibilityLevel.Conditional
                : CompatibilityLevel.Safe;
        return new(overall, items);
    }

    public async Task<IReadOnlyList<string>> RestoreApksAsync(
        string serial,
        SnapshotManifest snapshot,
        DeviceInventory targetDevice,
        IReadOnlyList<PackageSnapshot> installed,
        EncryptedRepository repository,
        AgentClient agent,
        IReadOnlyCollection<string>? selectedPackages = null,
        bool allowDowngrade = false,
        bool createSafetySnapshot = true,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var components = snapshot.Components
            .Where(x => x.Kind == "apk" && x.Package is not null)
            .Where(x => selectedPackages is null || selectedPackages.Contains(x.Id))
            .ToList();
        foreach (var entry in components.SelectMany(x => x.Files))
        {
            if (entry.Chunks is null)
                throw new InvalidDataException($"У {entry.RelativePath} нет chunks.");
            if (string.IsNullOrWhiteSpace(entry.ContentHash))
                throw new InvalidDataException($"У {entry.RelativePath} нет SHA-256.");
        }
        progress?.Report(new("verify", "Проверка целостности APK", 0, components.Count));
        long verifiedFileCount = 0;
        var fileCount = components.Sum(x => x.Files.Count);
        foreach (var entry in components.SelectMany(x => x.Files))
        {
            await repository.VerifyFileAsync(
                entry.Chunks!, entry.Size, entry.ContentHash!, cancellationToken);
            verifiedFileCount++;
            progress?.Report(new(
                "verify", entry.RelativePath, verifiedFileCount, fileCount));
        }
        if (createSafetySnapshot)
        {
            var selectedIds = components.Select(x => x.Id)
                .Concat(snapshot.Components
                    .Where(x => x.Kind == "launcher-data" && x.Package is not null)
                    .Select(x => x.Package!.PackageName))
                .ToHashSet(StringComparer.Ordinal);
            var currentForSafety = installed.Where(x => selectedIds.Contains(x.PackageName)).ToList();
            if (currentForSafety.Count > 0)
                currentForSafety = (await agent.GetPackagesAsync(
                    includeSystemApps: true,
                    packageNames: currentForSafety.Select(x => x.PackageName).ToList(),
                    cancellationToken: cancellationToken,
                    hashArtifacts: true)).ToList();
            var includeSystemStateSafety = snapshot.Components.Any(x =>
                x.Kind is "system-state" or "wallpaper");
            var includeSharedStorageSafety = snapshot.Components.Any(x => x.Kind == "shared-storage");
            var includePersonalDataSafety = snapshot.Components.Any(x =>
                x.Kind == "personal-data" && x.RestoreClass == RestoreClass.Portable);
            if (currentForSafety.Count > 0 || includeSystemStateSafety ||
                includeSharedStorageSafety || includePersonalDataSafety)
            {
                progress?.Report(new(
                    "safety-snapshot",
                    "Приложения, общая память и настройки перед изменениями",
                    0,
                    Math.Max(1, currentForSafety.Count)));
                var requiresAppDataSafety = snapshot.Components.Any(x =>
                    x.Kind is "app-data" or "launcher-data" &&
                    (x.Kind == "launcher-data" || selectedPackages is null ||
                     selectedPackages.Contains(x.Package?.PackageName ?? x.Id)));
                var includeAppData = await agent.RequestRootAsync(cancellationToken);
                if (requiresAppDataSafety && !includeAppData)
                    throw new UnauthorizedAccessException(
                        "Полный safety snapshot app-data невозможен без root; Restore не начат.");
                var safety = await new BackupCoordinator(adb).BackupPortableAsync(
                    serial,
                    targetDevice,
                    currentForSafety,
                    repository,
                    agent,
                    includeSystemApps: true,
                    includeApks: true,
                    includeAppData: includeAppData,
                    includeSharedStorage: includeSharedStorageSafety,
                    includePersonalData: includePersonalDataSafety,
                    includeSystemState: includeSystemStateSafety,
                    includeFullComponents: false,
                    includeCaches: false,
                    fullHash: true,
                    progress: progress,
                    cancellationToken: cancellationToken,
                    purpose: "safety");
                var requiredSafetyComponents = snapshot.Components
                    .Where(x => x.Status == ComponentStatus.Complete &&
                                (x.Kind is "system-state" or "shared-storage" or "wallpaper" ||
                                 x.Kind == "personal-data" &&
                                 x.RestoreClass == RestoreClass.Portable))
                    .Select(x => x.Id)
                    .ToHashSet(StringComparer.Ordinal);
                if (requiredSafetyComponents.Count > 0 && !requiredSafetyComponents.All(id =>
                        safety.Components.Any(x => x.Id == id &&
                            x.Status == ComponentStatus.Complete)))
                    throw new InvalidOperationException(
                        "Safety snapshot не содержит полный снимок каждого изменяемого " +
                        "shared/personal/settings/wallpaper компонента; Restore заблокирован.");

                var safetyPackages = currentForSafety.Select(x => x.PackageName)
                    .ToHashSet(StringComparer.Ordinal);
                if (safetyPackages.Any(packageName => !safety.Components.Any(x =>
                        x.Kind == "apk" &&
                        x.Package?.PackageName == packageName &&
                        x.Status == ComponentStatus.Complete)))
                    throw new InvalidOperationException(
                        "Safety snapshot не содержит полный APK каждого изменяемого приложения; " +
                        "Restore заблокирован.");

                if (requiresAppDataSafety)
                {
                    var requiredDataPackages = snapshot.Components
                        .Where(x => x.Kind is "app-data" or "launcher-data" &&
                                    x.Package is not null &&
                                    safetyPackages.Contains(x.Package.PackageName) &&
                                    (x.Kind == "launcher-data" || selectedPackages is null ||
                                     selectedPackages.Contains(x.Package.PackageName)))
                        .Select(x => x.Package!.PackageName)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    if (requiredDataPackages.Any(packageName =>
                    {
                        var roots = safety.Components.Where(x =>
                                x.Kind == "app-data" &&
                                x.Package?.PackageName == packageName)
                            .ToList();
                        return roots.Count == 0 ||
                               roots.Any(x => x.Status != ComponentStatus.Complete);
                    }))
                        throw new InvalidOperationException(
                            "Safety snapshot app-data неполон; Restore заблокирован.");
                }
            }
        }
        var selectedPackageNames = components.Select(x => x.Package!.PackageName)
            .ToHashSet(StringComparer.Ordinal);
        var approvalItemCount = snapshot.Components.Count(component =>
            component.Status == ComponentStatus.Complete &&
            component.RestoreClass is not (RestoreClass.ArchiveOnly or RestoreClass.Never) &&
            (component.Package is null || selectedPackageNames.Contains(component.Package.PackageName)));
        if (!await agent.RequestRestoreApprovalAsync(
                snapshot.SnapshotId, approvalItemCount, TimeSpan.FromMinutes(5), progress, cancellationToken))
            throw new UnauthorizedAccessException("Restore не подтверждён на телефоне.");

        var installedByPackage = installed.ToDictionary(x => x.PackageName, StringComparer.Ordinal);
        var failedPackages = new List<string>();
        Task<PreparedApkPackage>? pendingPreparation = components.Count == 0
            ? null
            : PrepareApkPackageAsync(
                components[0], installedByPackage, allowDowngrade, repository,
                progress, 0, components.Count, cancellationToken);
        try
        {
            for (var componentIndex = 0; componentIndex < components.Count; componentIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var component = components[componentIndex];
                var metadata = component.Package!;
                var currentPreparation = pendingPreparation
                    ?? throw new InvalidOperationException("APK staging pipeline lost its current item.");
                pendingPreparation = null;
                PreparedApkPackage? prepared = null;
                try
                {
                    prepared = await currentPreparation.ConfigureAwait(false);
                    if (componentIndex + 1 < components.Count)
                    {
                        pendingPreparation = PrepareApkPackageAsync(
                            components[componentIndex + 1], installedByPackage, allowDowngrade,
                            repository, progress, componentIndex + 1, components.Count,
                            cancellationToken);
                    }

                    progress?.Report(new(
                        "restore-apk", metadata.PackageName, componentIndex, components.Count));
                    var installTask = adb.InstallMultipleAsync(
                        serial,
                        prepared.ApkPaths,
                        allowDowngrade,
                        cancellationToken);
                    if (await Task.WhenAny(
                            installTask,
                            Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None))
                        .ConfigureAwait(false) != installTask)
                    {
                        progress?.Report(new(
                            "restore-apk-needs-user",
                            $"{metadata.PackageName}: проверьте экран телефона и подтвердите Play Protect",
                            componentIndex,
                            components.Count));
                    }
                    await installTask.ConfigureAwait(false);
                    progress?.Report(new(
                        "restore-apk-done",
                        metadata.PackageName,
                        componentIndex,
                        components.Count));
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    failedPackages.Add(metadata.PackageName);
                    progress?.Report(new(
                        "restore-apk-failed",
                        $"{metadata.PackageName}: {error.Message}",
                        componentIndex,
                        components.Count));
                    DesktopDiagnostics.Log(
                        "error", "restore", "apk_restore_failed", error.Message,
                        result: "failed", error: error,
                        fields: new Dictionary<string, object?>
                        {
                            ["packageName"] = metadata.PackageName
                        });
                }
                finally
                {
                    prepared?.Dispose();
                }
                if (pendingPreparation is null && componentIndex + 1 < components.Count)
                {
                    pendingPreparation = PrepareApkPackageAsync(
                        components[componentIndex + 1], installedByPackage, allowDowngrade,
                        repository, progress, componentIndex + 1, components.Count,
                        cancellationToken);
                }
            }
        }
        finally
        {
            if (pendingPreparation is not null)
            {
                try
                {
                    var unused = await pendingPreparation.ConfigureAwait(false);
                    unused.Dispose();
                }
                catch (Exception error)
                {
                    DesktopDiagnostics.Log(
                        "warn", "restore", "apk_staging_cleanup_failed", error.Message,
                        result: "failed", error: error,
                        fields: new Dictionary<string, object?>
                    {
                        ["cancelled"] = cancellationToken.IsCancellationRequested
                    });
                }
            }
        }

        var packageNames = components.Select(x => x.Id).ToList();
        var verified = (await agent.GetPackagesAsync(
                includeSystemApps: true,
                packageNames,
                cancellationToken,
                hashArtifacts: false))
            .ToDictionary(x => x.PackageName, StringComparer.Ordinal);
        foreach (var component in components)
        {
            if (!verified.TryGetValue(component.Id, out var restored))
            {
                if (!failedPackages.Contains(component.Id, StringComparer.Ordinal))
                    failedPackages.Add(component.Id);
                continue;
            }
            if (!SignatureMatches(component.Package!, restored))
                if (!failedPackages.Contains(component.Id, StringComparer.Ordinal))
                    failedPackages.Add(component.Id);
        }
        return failedPackages;
    }

    public async Task<IReadOnlyDictionary<string, string>> RestoreAppDataAsync(
        SnapshotManifest snapshot,
        IReadOnlyList<PackageSnapshot> installed,
        EncryptedRepository repository,
        AgentClient agent,
        IReadOnlyCollection<string> selectedPackages,
        RomIdentity? targetRom = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var selected = selectedPackages.ToHashSet(StringComparer.Ordinal);
        var components = snapshot.Components
            .Where(x => x.Kind is "app-data" or "launcher-data" && x.Package is not null)
            .Where(x => selected.Contains(x.Package!.PackageName))
            .ToList();
        if (components.Count == 0) return new Dictionary<string, string>();
        if (!await agent.RequestRootAsync(cancellationToken))
            throw new UnauthorizedAccessException("Для восстановления app data нужен root.");

        var installedByPackage = installed.ToDictionary(x => x.PackageName, StringComparer.Ordinal);
        var groups = components.GroupBy(x => x.Package!.PackageName, StringComparer.Ordinal).ToList();
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        var packageIndex = 0L;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!installedByPackage.TryGetValue(group.Key, out var current))
            {
                results[group.Key] = "skipped: package_missing";
                progress?.Report(new("restore-app-data-skipped", group.Key, packageIndex++, groups.Count));
                continue;
            }
            var metadata = group.First().Package!;
            if (!SignatureMatches(metadata, current))
            {
                results[group.Key] = "skipped: signature_mismatch";
                progress?.Report(new("restore-app-data-skipped", group.Key, packageIndex++, groups.Count));
                continue;
            }

            var groupComponents = group.ToList();
            if (groupComponents.Any(component => component.RestoreClass switch
                {
                    RestoreClass.Portable => false,
                    RestoreClass.ExactRom or RestoreClass.ExactDevice =>
                        snapshot.Rom?.IsExactRomMatch(targetRom) != true,
                    RestoreClass.ArchiveOnly or RestoreClass.Never => true,
                    _ => true
                }))
            {
                results[group.Key] = "skipped: restore_class_incompatible";
                progress?.Report(new("restore-app-data-skipped", group.Key, packageIndex++, groups.Count));
                continue;
            }
            if (snapshot.FormatVersion >= 2 &&
                groupComponents.Any(x => x.Status != ComponentStatus.Complete))
            {
                results[group.Key] = "skipped: incomplete_backup_component";
                progress?.Report(new("restore-app-data-skipped", group.Key, packageIndex++, groups.Count));
                continue;
            }

            progress?.Report(new("restore-app-data", group.Key, packageIndex, groups.Count));
            var rollbackFailed = false;
            string? rollbackFailure = null;
            try
            {
                // Isolate every package transaction. A rejected root stream may leave unread
                // DATA frames on its socket; they must not shift responses for the next app.
                await using var packageAgent = await agent.ConnectSiblingAsync(cancellationToken);
                foreach (var entry in groupComponents.SelectMany(x => x.Files))
                {
                    if (!RestorePathPolicy.IsSafeRelativePath(entry.RelativePath))
                        throw new InvalidDataException($"Небезопасный путь Restore: {entry.RelativePath}");
                    if (entry.Kind == "special") continue;
                    if (entry.Kind is not ("file" or "directory"))
                        throw new NotSupportedException(
                            $"Тип {entry.Kind} не поддерживается: {entry.RelativePath}");
                    if (entry.Kind == "file" && entry.Chunks is null)
                        throw new InvalidDataException($"У {entry.RelativePath} нет chunks.");
                }
                await repository.VerifyReferencesAsync(
                    groupComponents.SelectMany(x => x.Files).SelectMany(x => x.Chunks ?? []),
                    cancellationToken);

                var componentRoots = groupComponents.ToDictionary(
                    x => x.Id,
                    x => ResolveTargetDataRoot(snapshot, x, current, group.Key),
                    StringComparer.Ordinal);
                var completeSnapshot = snapshot.FormatVersion >= 2 &&
                                       groupComponents.All(x => x.Status == ComponentStatus.Complete);
                await packageAgent.PreparePackageRestoreAsync(
                    group.Key,
                    componentRoots.Values.Distinct(StringComparer.Ordinal).ToList(),
                    completeSnapshot,
                    cancellationToken);
                var committed = false;
                var agentRolledBack = false;
                try
                {
                    foreach (var component in groupComponents)
                    {
                        var root = componentRoots[component.Id];
                    foreach (var entry in component.Files
                                 .OrderBy(x => x.Kind == "directory" ? 0 : 1)
                                 .ThenBy(x => PathDepth(x.RelativePath)))
                    {
                        if (entry.Kind == "special") continue;
                        await packageAgent.RestoreRootEntryAsync(
                            group.Key,
                            root,
                            entry,
                            current.Uid,
                            entry.Kind == "file"
                                ? (output, token) => repository.RestoreStreamAsync(
                                    entry.Chunks!, output, token)
                                : null,
                            cancellationToken);
                    }
                    }
                    try
                    {
                        await packageAgent.FinishPackageRestoreAsync(group.Key, cancellationToken);
                    }
                    catch (PackageRestoreCommitException commitError)
                        when (commitError.RolledBack)
                    {
                        agentRolledBack = true;
                        throw;
                    }
                    committed = true;
                }
                finally
                {
                    if (!committed && !agentRolledBack)
                    {
                        using var abortTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        try
                        {
                            await packageAgent.AbortPackageRestoreAsync(group.Key, abortTimeout.Token);
                        }
                        catch (Exception abortError)
                        {
                            rollbackFailed = true;
                            rollbackFailure = abortError.Message;
                        }
                    }
                }
                results[group.Key] = "succeeded";
                progress?.Report(new("restore-app-data-done", group.Key, packageIndex, groups.Count));
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                results[group.Key] = rollbackFailed
                    ? $"failed: rollback_failed: {error.Message}; {rollbackFailure}"
                    : $"rolled_back: {error.Message}";
                progress?.Report(new(
                    rollbackFailed ? "restore-app-data-failed" : "restore-app-data-rolled-back",
                    $"{group.Key}: {error.Message}" +
                    (rollbackFailed ? $"; rollback: {rollbackFailure}" : ""),
                    packageIndex,
                    groups.Count));
                DesktopDiagnostics.Log(
                    "error", "restore",
                    rollbackFailed ? "app_data_rollback_failed" : "app_data_rolled_back",
                    error.Message,
                    result: rollbackFailed ? "failed" : "rolled_back", error: error,
                    fields: new Dictionary<string, object?> { ["packageName"] = group.Key });
            }
            packageIndex++;
        }
        return results;
    }

    private static string ResolveTargetDataRoot(
        SnapshotManifest snapshot,
        BackupComponentManifest component,
        PackageSnapshot current,
        string packageName)
    {
        if (snapshot.FormatVersion >= 2 && component.DataRootKind != DataRootKind.Unknown)
        {
            var typed = current.DataRoots?.FirstOrDefault(x =>
                x.Kind == component.DataRootKind && x.UserId == component.UserId);
            if (typed is not null) return typed.Path;
            throw new InvalidDataException(
                $"{packageName}: отсутствует root {component.DataRootKind} для user {component.UserId}.");
        }
        var rootIndex = ParseRootIndex(component.Id, packageName, component.Kind);
        if (rootIndex < 0 || rootIndex >= current.DataPaths.Count)
            throw new InvalidDataException(
                $"{packageName}: legacy data root #{rootIndex} отсутствует.");
        return current.DataPaths[rootIndex];
    }

    public async Task<IReadOnlyList<ComponentRestoreResult>> RestoreSharedStorageAsync(
        SnapshotManifest snapshot,
        EncryptedRepository repository,
        AgentClient agent,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var components = snapshot.Components
            .Where(x => x.Kind == "shared-storage")
            .ToList();
        if (components.Count == 0) return [];
        var results = new List<ComponentRestoreResult>(components.Count);
        var capability = await agent.GetSharedStorageAsync(cancellationToken);
        if (!capability.AccessGranted)
            throw new UnauthorizedAccessException(
                "Для восстановления общей памяти включите «Доступ ко всем файлам» в Agent.");
        var rootsById = capability.Roots.ToDictionary(
            SharedComponentId,
            StringComparer.Ordinal);
        var index = 0L;
        foreach (var component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (component.Status != ComponentStatus.Complete ||
                    component.Verification?.HashesVerified == false)
                    throw new InvalidDataException(
                        "Компонент общей памяти неполный или не прошёл проверку хешей.");
                if (!rootsById.TryGetValue(component.Id, out var root))
                    throw new InvalidDataException(
                        $"На телефоне отсутствует разрешённый shared root: {component.Id}");
                foreach (var entry in component.Files)
                {
                    if (!RestorePathPolicy.IsSafeRelativePath(entry.RelativePath))
                        throw new InvalidDataException($"Небезопасный shared path: {entry.RelativePath}");
                    if (entry.Kind is not ("file" or "directory"))
                        throw new NotSupportedException(
                            $"Shared restore не поддерживает {entry.Kind}: {entry.RelativePath}");
                    if (entry.Kind == "file" && entry.Chunks is null)
                        throw new InvalidDataException($"У {entry.RelativePath} нет chunks.");
                }
                progress?.Report(new("verify", $"Проверка {component.Id}", index, components.Count));
                await repository.VerifyReferencesAsync(
                    component.Files.SelectMany(x => x.Chunks ?? []), cancellationToken);
                progress?.Report(new("restore-shared", component.Id, index, components.Count));
                foreach (var entry in component.Files
                             .OrderBy(x => x.Kind == "directory" ? 0 : 1)
                             .ThenBy(x => PathDepth(x.RelativePath)))
                {
                    await agent.RestoreSharedEntryAsync(
                        root,
                        entry,
                        entry.Kind == "file"
                            ? (output, token) => repository.RestoreStreamAsync(
                                entry.Chunks!, output, token)
                            : null,
                        cancellationToken);
                }
                progress?.Report(new("restore-shared-done", component.Id, index, components.Count));
                results.Add(new(component.Id, RestoreItemState.Succeeded));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                progress?.Report(new(
                    "restore-shared-failed", $"{component.Id}: {error.Message}", index, components.Count));
                results.Add(new(component.Id, RestoreItemState.Failed, error.Message));
            }
            index++;
        }
        return results;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> RestorePackagePoliciesAsync(
        SnapshotManifest snapshot,
        AgentClient agent,
        IReadOnlyCollection<string> selectedPackages,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await agent.RequestRootAsync(cancellationToken))
            return new Dictionary<string, IReadOnlyList<string>>();
        var selected = selectedPackages.ToHashSet(StringComparer.Ordinal);
        var metadata = snapshot.Components
            .Where(x => x.Kind == "apk" && x.Package is not null)
            .Select(x => x.Package!)
            .Where(x => selected.Contains(x.PackageName))
            .DistinctBy(x => x.PackageName, StringComparer.Ordinal)
            .ToList();
        var failures = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        for (var index = 0; index < metadata.Count; index++)
        {
            var item = metadata[index];
            progress?.Report(new("restore-policy", item.PackageName, index, metadata.Count));
            var itemFailures = await agent.ApplyPackagePolicyAsync(item, cancellationToken);
            if (itemFailures.Count > 0) failures[item.PackageName] = itemFailures;
        }
        return failures;
    }

    public async Task<SystemStateRestoreResult> RestoreSystemStateAsync(
        SnapshotManifest snapshot,
        EncryptedRepository repository,
        AgentClient agent,
        IReadOnlyCollection<string>? confirmedSettingIds = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var component = snapshot.Components.FirstOrDefault(x => x.Kind == "system-state");
        if (component is null) return new([], [], []);
        if (snapshot.FormatVersion >= 2 && component.Status != ComponentStatus.Complete)
            return new(["system-state:incomplete-backup-component"], [], []);
        var file = component.Files.SingleOrDefault()
            ?? throw new InvalidDataException("System state component is invalid.");
        if (file.Chunks is null)
            throw new InvalidDataException("System state component has no chunks.");
        if (!await agent.RequestRootAsync(cancellationToken))
            return new(["root-required"], [], []);
        progress?.Report(new("restore-system-state", "Безопасные настройки и роли", 0, 1));
        await repository.VerifyReferencesAsync(file.Chunks, cancellationToken);
        await using var output = new MemoryStream();
        await repository.RestoreStreamAsync(file.Chunks, output, cancellationToken);
        return await agent.RestoreSystemStateAsync(
            output.ToArray(),
            confirmedSettingIds,
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> RestoreWallpapersAsync(
        SnapshotManifest snapshot,
        EncryptedRepository repository,
        AgentClient agent,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var components = snapshot.Components.Where(x => x.Kind == "wallpaper").ToList();
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            var kind = component.Id.StartsWith("wallpaper:", StringComparison.Ordinal)
                ? component.Id["wallpaper:".Length..]
                : string.Empty;
            if (kind is not ("system" or "lock") ||
                snapshot.FormatVersion >= 2 && component.Status != ComponentStatus.Complete)
            {
                results[component.Id] = "skipped: invalid_or_incomplete_component";
                continue;
            }
            var file = component.Files.SingleOrDefault();
            if (file?.Chunks is null)
            {
                results[component.Id] = "skipped: missing_chunks";
                continue;
            }
            progress?.Report(new("restore-wallpaper", kind, index, components.Count));
            try
            {
                await repository.VerifyReferencesAsync(file.Chunks, cancellationToken);
                await agent.RestoreWallpaperAsync(
                    kind,
                    file.Size,
                    file.ContentHash,
                    (output, token) => repository.RestoreStreamAsync(file.Chunks, output, token),
                    cancellationToken);
                results[component.Id] = "succeeded";
                progress?.Report(new("restore-wallpaper-done", kind, index, components.Count));
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                results[component.Id] = $"failed: {error.Message}";
                progress?.Report(new(
                    "restore-wallpaper-failed", $"{kind}: {error.Message}", index, components.Count));
            }
        }
        return results;
    }

    public async Task<IReadOnlyList<PersonalRestoreResult>> RestorePersonalDataAsync(
        SnapshotManifest snapshot,
        EncryptedRepository repository,
        AgentClient agent,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var components = snapshot.Components.Where(x =>
            x.Kind == "personal-data" && x.RestoreClass == RestoreClass.Portable).ToList();
        var results = new List<PersonalRestoreResult>();
        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            var kind = component.Id.StartsWith("personal:", StringComparison.Ordinal)
                ? component.Id["personal:".Length..]
                : string.Empty;
            if (kind is not ("contacts" or "calls") ||
                snapshot.FormatVersion >= 2 && component.Status != ComponentStatus.Complete)
            {
                results.Add(new(kind, "skipped", 0, 0, 0, "invalid_or_incomplete_component"));
                continue;
            }
            var file = component.Files.SingleOrDefault();
            if (file?.Chunks is null)
            {
                results.Add(new(kind, "skipped", 0, 0, 0, "missing_chunks"));
                continue;
            }
            progress?.Report(new("restore-personal", kind, index, components.Count));
            try
            {
                await repository.VerifyReferencesAsync(file.Chunks, cancellationToken);
                var result = await agent.RestorePersonalDataAsync(
                    kind,
                    (output, token) => repository.RestoreStreamAsync(file.Chunks, output, token),
                    cancellationToken);
                results.Add(result);
                progress?.Report(new(
                    result.Failed == 0 ? "restore-personal-done" : "restore-personal-failed",
                    kind,
                    index,
                    components.Count));
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                results.Add(new(kind, "failed", 0, 0, 1, error.Message));
                progress?.Report(new(
                    "restore-personal-failed", $"{kind}: {error.Message}", index, components.Count));
            }
        }
        return results;
    }

    private static async Task<PreparedApkPackage> PrepareApkPackageAsync(
        BackupComponentManifest component,
        IReadOnlyDictionary<string, PackageSnapshot> installedByPackage,
        bool allowDowngrade,
        EncryptedRepository repository,
        IProgress<TransferProgress>? progress,
        int componentIndex,
        int componentCount,
        CancellationToken cancellationToken)
    {
        var metadata = component.Package
            ?? throw new InvalidDataException($"APK component {component.Id} has no package metadata.");
        if (installedByPackage.TryGetValue(metadata.PackageName, out var present))
        {
            if (!SignatureMatches(metadata, present))
                throw new InvalidOperationException(
                    $"Подпись {metadata.PackageName} не совпадает.");
            if (present.VersionCode > metadata.VersionCode && !allowDowngrade)
                throw new InvalidOperationException(
                    $"{metadata.PackageName}: downgrade требует отдельного подтверждения.");
        }

        progress?.Report(new(
            "restore-apk-stage", metadata.PackageName, componentIndex, componentCount));
        var stagingDirectory = CreateStagingDirectory();
        try
        {
            var apkPaths = new List<string>(component.Files.Count);
            for (var fileIndex = 0; fileIndex < component.Files.Count; fileIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = component.Files[fileIndex];
                if (entry.Chunks is null || string.IsNullOrWhiteSpace(entry.ContentHash))
                    throw new InvalidDataException(
                        $"APK {entry.RelativePath} has no chunks or content hash.");
                var originalName = Path.GetFileName(entry.RelativePath);
                if (string.IsNullOrWhiteSpace(originalName) ||
                    !originalName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Некорректное имя APK: {entry.RelativePath}");

                var stagedPath = Path.Combine(
                    stagingDirectory,
                    $"{fileIndex:D3}-{originalName}");
                await using var output = new FileStream(
                    stagedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var hashing = new HashingWriteStream(output);
                await repository.RestoreStreamAsync(
                        entry.Chunks,
                        hashing,
                        cancellationToken)
                    .ConfigureAwait(false);
                await hashing.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (output.Length != entry.Size)
                    throw new InvalidDataException(
                        $"Размер APK не совпал: {entry.RelativePath}");
                var hash = hashing.FinalizeHash();
                if (!string.Equals(hash, entry.ContentHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"SHA-256 не совпал: {entry.RelativePath}");
                apkPaths.Add(stagedPath);
            }
            progress?.Report(new(
                "restore-apk-stage-done", metadata.PackageName, componentIndex, componentCount));
            return new PreparedApkPackage(stagingDirectory, apkPaths);
        }
        catch
        {
            DeleteStaging(stagingDirectory);
            throw;
        }
    }

    internal static bool SignatureMatches(
        PackageBackupMetadata metadata,
        PackageSnapshot current)
    {
        var sourceLineage = (metadata.SigningLineageSha256 ?? [])
            .Append(metadata.SigningCertificateSha256)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetLineage = (current.SigningLineageSha256 ?? [])
            .Append(current.SigningCertificateSha256)
            .Where(value => !string.IsNullOrWhiteSpace(value));
        return targetLineage.Any(sourceLineage.Contains);
    }

    private static string SharedComponentId(string root) =>
        $"shared:{root.Replace('\\', '/').Trim('/')}";

    private static int ParseRootIndex(
        string componentId,
        string packageName,
        string componentKind)
    {
        var prefix = $"{packageName}:{componentKind}:";
        return componentId.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(componentId[prefix.Length..], out var index)
            ? index
            : -1;
    }

    private static int PathDepth(string path) =>
        path.Count(x => x is '/' or '\\');

    private static void DeleteStaging(string path)
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneBackup",
            "staging")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path);
        if (target.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(target, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    private static string CreateStagingDirectory()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneBackup",
            "staging");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class PreparedApkPackage(
        string stagingDirectory,
        IReadOnlyList<string> apkPaths) : IDisposable
    {
        private int _disposed;
        public IReadOnlyList<string> ApkPaths { get; } = apkPaths;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                DeleteStaging(stagingDirectory);
        }
    }

    private sealed class HashingWriteStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _finalized;
        public string FinalizeHash()
        {
            if (_finalized) throw new InvalidOperationException("Hash is already finalized.");
            _finalized = true;
            return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }
        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync()
        {
            _hash.Dispose();
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
