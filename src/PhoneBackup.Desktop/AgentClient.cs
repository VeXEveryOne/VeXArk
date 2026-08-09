using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using NSec.Cryptography;
using PhoneBackup.Core;

namespace PhoneBackup.Desktop;

public sealed record RootRequestResult(
    bool Available,
    bool Granted,
    string Provider,
    string Detail,
    AgentRootDiagnostics? Diagnostics,
    AgentRootHelperDiagnostics? Helper)
{
    public bool RootDataReady => Granted && Helper is { Ok: true, Uid: 0 };
}

public sealed record AgentRootHelperDiagnostics(
    bool Ok,
    int? ExitCode,
    int? Uid,
    string Stdout,
    string Stderr,
    string? ExceptionType);

public sealed record AgentRootDiagnostics(
    string AttemptId,
    DateTimeOffset TimestampUtc,
    bool RequestGrant,
    int AppUid,
    string AppGrantState,
    bool CachedShellPresent,
    bool CachedShellAlive,
    bool CachedShellRoot,
    bool CachedShellClosed,
    int? ShellExitCode,
    bool ShellSuccess,
    string Stdout,
    string Stderr,
    string Selinux,
    string? ExceptionType,
    string? ExceptionMessage,
    long DurationMs,
    IReadOnlyList<string> Steps);

public sealed record AgentBuildInfo(
    string Channel,
    string PackageName,
    string VersionName,
    int VersionCode,
    string BuildId,
    int AgentPort,
    bool DiagnosticsEnabled,
    int ProtocolVersion);

public sealed record AgentDiagnosticsSnapshot(
    AgentBuildInfo Build,
    IReadOnlyList<DiagnosticEvent> Events,
    string RawJson);

public sealed class AgentClient : IAsyncDisposable
{
    private static readonly TimeSpan ProtocolIdleTimeout = TimeSpan.FromMinutes(2);
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly DesktopIdentity _identity;
    private readonly string _desktopKey;
    private readonly AdbService? _adb;
    private readonly string? _serial;
    private readonly int _forwardPort;
    private readonly bool _ownsForward;

    private AgentClient(
        TcpClient client,
        DesktopIdentity identity,
        AdbService? adb,
        string? serial,
        int forwardPort,
        bool ownsForward)
    {
        _client = client;
        _stream = client.GetStream();
        _identity = identity;
        _desktopKey = identity.PublicKey;
        _adb = adb;
        _serial = serial;
        _forwardPort = forwardPort;
        _ownsForward = ownsForward;
    }

    public static async Task<AgentClient> ConnectAsync(
        AdbService adb,
        string serial,
        CancellationToken cancellationToken = default)
    {
        DesktopDiagnostics.Log(
            "info",
            "protocol",
            "connect_started",
            "Connecting to Android Agent",
            fields: new Dictionary<string, object?>
            {
                ["agentPackage"] = AppRuntimeProfile.AgentPackage,
                ["agentPort"] = AppRuntimeProfile.AgentPort,
                ["serial"] = serial
            });
        await adb.LaunchAgentAsync(serial, cancellationToken);
        var port = await adb.ForwardAgentPortAsync(serial, cancellationToken);
        try
        {
            return await ConnectPortAsync(
                port,
                adb,
                serial,
                ownsForward: true,
                cancellationToken);
        }
        catch
        {
            await adb.RemoveAgentForwardAsync(serial, port, CancellationToken.None);
            throw;
        }
    }

    public Task<AgentClient> ConnectSiblingAsync(
        CancellationToken cancellationToken = default) =>
        ConnectPortAsync(
            _forwardPort,
            adb: null,
            serial: null,
            ownsForward: false,
            cancellationToken);

    private static async Task<AgentClient> ConnectPortAsync(
        int port,
        AdbService? adb,
        string? serial,
        bool ownsForward,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        Exception? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                DesktopDiagnostics.Log(
                    "info",
                    "protocol",
                    "connect_completed",
                    "Connected to Android Agent",
                    result: "ok",
                    fields: new Dictionary<string, object?>
                    {
                        ["forwardPort"] = port,
                        ["attempt"] = attempt + 1
                    });
                return new(
                    client,
                    DesktopIdentity.LoadOrCreate(),
                    adb,
                    serial,
                    port,
                    ownsForward);
            }
            catch (Exception error) when (error is SocketException or IOException)
            {
                last = error;
                DesktopDiagnostics.Log(
                    "warn",
                    "protocol",
                    "connect_retry",
                    "Agent connection attempt failed",
                    result: (attempt + 1).ToString(),
                    error: error,
                    fields: new Dictionary<string, object?>
                    {
                        ["forwardPort"] = port
                    });
                await Task.Delay(250, cancellationToken);
                client.Dispose();
                client = new TcpClient { NoDelay = true };
            }
        }
        client.Dispose();
        throw new IOException("Agent не запустил локальный сервер.", last);
    }

    public Task<JsonDocument> HelloAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("hello", cancellationToken: cancellationToken);

    public async Task<AgentBuildInfo> GetBuildInfoAsync(
        CancellationToken cancellationToken = default)
    {
        using var hello = await HelloAsync(cancellationToken);
        EnsureOk(hello.RootElement, "hello");
        if (!hello.RootElement.TryGetProperty("build", out var build) ||
            build.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "Agent не вернул build metadata. Установите актуальный VeXArk Agent Dev.");
        return ParseBuildInfo(build, hello.RootElement.GetProperty("protocolVersion").GetInt32());
    }

    public async Task<IReadOnlySet<string>> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        using var document = await HelloAsync(cancellationToken);
        if (!document.RootElement.TryGetProperty("capabilities", out var capabilities))
            return new HashSet<string>(StringComparer.Ordinal);
        return capabilities.EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToHashSet(StringComparer.Ordinal);
    }

    public Task<JsonDocument> PairAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("pair", cancellationToken: cancellationToken);

    public Task<JsonDocument> InventoryAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("inventory", cancellationToken: cancellationToken);

    public async Task<RomIdentity?> GetRomIdentityAsync(
        CancellationToken cancellationToken = default)
    {
        using var document = await InventoryAsync(cancellationToken).ConfigureAwait(false);
        EnsureOk(document.RootElement, "inventory");
        if (!document.RootElement.TryGetProperty("inventory", out var inventory) ||
            !inventory.TryGetProperty("romIdentity", out var identity) ||
            identity.ValueKind != JsonValueKind.Object)
            return null;
        return ParseRomIdentity(identity);
    }

    public Task<JsonDocument> PackagesAsync(
        bool includeSystemApps = false,
        IReadOnlyCollection<string>? packageNames = null,
        CancellationToken cancellationToken = default,
        bool hashArtifacts = true) =>
        SendCommandAsync(
            "packages", new { includeSystemApps, packageNames, hashArtifacts }, cancellationToken);

    public async Task<bool> PairWithApprovalAsync(
        TimeSpan timeout,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var result = await PairAsync(cancellationToken);
            if (result.RootElement.TryGetProperty("paired", out var paired) && paired.GetBoolean())
                return true;
            progress?.Report("Подтвердите этот компьютер на экране телефона…");
            await Task.Delay(1000, cancellationToken);
        }
        return false;
    }

    public async Task<IReadOnlyList<PackageSnapshot>> GetPackagesAsync(
        bool includeSystemApps = false,
        IReadOnlyCollection<string>? packageNames = null,
        CancellationToken cancellationToken = default,
        bool hashArtifacts = true)
    {
        using var document = await PackagesAsync(
            includeSystemApps, packageNames, cancellationToken, hashArtifacts)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException(
                document.RootElement.TryGetProperty("error", out var error)
                    ? error.GetString()
                    : "Agent не вернул список приложений.");
        var result = new List<PackageSnapshot>();
        foreach (var item in document.RootElement.GetProperty("packages").EnumerateArray())
        {
            var artifacts = item.GetProperty("apkArtifacts").EnumerateArray()
                .Select(x => new ApkArtifact(
                    x.GetProperty("path").GetString() ?? string.Empty,
                    x.GetProperty("fileName").GetString() ?? "base.apk",
                    x.GetProperty("size").GetInt64(),
                    x.GetProperty("modifiedUnixNanos").GetInt64(),
                    x.GetProperty("sha256").GetString() ?? string.Empty))
                .ToList();
            var dataPaths = item.GetProperty("dataPaths").EnumerateArray()
                .Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList();
            var dataRoots = item.TryGetProperty("dataRoots", out var rootItems) &&
                            rootItems.ValueKind == JsonValueKind.Array
                ? rootItems.EnumerateArray().Select(x => new AppDataRoot(
                    ParseDataRootKind(x.GetProperty("kind").GetString()),
                    x.GetProperty("path").GetString() ?? string.Empty,
                    x.TryGetProperty("userId", out var rootUser) ? rootUser.GetInt32() : 0))
                    .Where(x => x.Path.Length > 0)
                    .ToList()
                : dataPaths.Select((path, index) => new AppDataRoot(
                    index == 0 ? DataRootKind.CredentialEncrypted :
                    index == 1 ? DataRootKind.DeviceEncrypted : DataRootKind.Unknown,
                    path,
                    0)).ToList();
            var signingLineage = item.TryGetProperty("signingLineageSha256", out var lineage) &&
                                 lineage.ValueKind == JsonValueKind.Array
                ? lineage.EnumerateArray().Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList()
                : [];
            var runtimePermissions = item.TryGetProperty("runtimePermissions", out var permissionItems)
                ? permissionItems.EnumerateArray()
                    .Select(x => new RuntimePermissionState(
                        x.GetProperty("name").GetString() ?? string.Empty,
                        x.GetProperty("granted").GetBoolean(),
                        x.GetProperty("flags").GetInt32()))
                    .ToList()
                : [];
            result.Add(new(
                item.GetProperty("packageName").GetString() ?? string.Empty,
                item.GetProperty("label").GetString() ?? string.Empty,
                item.GetProperty("versionCode").GetInt64(),
                item.GetProperty("versionName").GetString() ?? string.Empty,
                item.GetProperty("signingCertificateSha256").GetString() ?? string.Empty,
                item.TryGetProperty("installer", out var installer) && installer.ValueKind != JsonValueKind.Null
                    ? installer.GetString()
                    : null,
                item.TryGetProperty("userId", out var userId) ? userId.GetInt32() : 0,
                item.GetProperty("uid").GetInt32(),
                item.GetProperty("isSystem").GetBoolean(),
                item.GetProperty("isEnabled").GetBoolean(),
                artifacts,
                dataPaths,
                runtimePermissions,
                item.TryGetProperty("batteryOptimizationExempt", out var battery) &&
                battery.GetBoolean(),
                dataRoots,
                signingLineage,
                item.TryGetProperty("targetSdk", out var targetSdk) ? targetSdk.GetInt32() : 0,
                item.TryGetProperty("appLocales", out var locales) &&
                locales.ValueKind == JsonValueKind.String ? locales.GetString() : null));
        }
        return result;
    }

    internal static RomIdentity ParseRomIdentity(JsonElement identity)
    {
        var schemas = identity.TryGetProperty("providerSchemaVersions", out var schemaItems) &&
                      schemaItems.ValueKind == JsonValueKind.Object
            ? schemaItems.EnumerateObject().ToDictionary(
                x => x.Name,
                x => x.Value.GetInt32(),
                StringComparer.Ordinal)
            : new Dictionary<string, int>(StringComparer.Ordinal);
        var signatures = identity.TryGetProperty("packageSignatures", out var signatureItems) &&
                         signatureItems.ValueKind == JsonValueKind.Object
            ? signatureItems.EnumerateObject().ToDictionary(
                x => x.Name,
                x => x.Value.GetString() ?? string.Empty,
                StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var capabilities = identity.TryGetProperty("capabilities", out var capabilityItems) &&
                           capabilityItems.ValueKind == JsonValueKind.Array
            ? capabilityItems.EnumerateArray().Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList()
            : [];
        var packageVersions = identity.TryGetProperty("packageVersions", out var versionItems) &&
                              versionItems.ValueKind == JsonValueKind.Object
            ? versionItems.EnumerateObject().ToDictionary(
                x => x.Name,
                x => x.Value.GetInt64(),
                StringComparer.Ordinal)
            : new Dictionary<string, long>(StringComparer.Ordinal);
        return new(
            identity.GetProperty("sdk").GetInt32(),
            identity.GetProperty("device").GetString() ?? string.Empty,
            identity.GetProperty("buildIncremental").GetString() ?? string.Empty,
            OptionalString(identity, "crDroidVersion"),
            OptionalString(identity, "crDroidBuildVersion"),
            OptionalString(identity, "lineageDevice"),
            schemas,
            signatures,
            capabilities,
            packageVersions);
    }

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DataRootKind ParseDataRootKind(string? kind) => kind switch
    {
        "credential_encrypted" => DataRootKind.CredentialEncrypted,
        "device_encrypted" => DataRootKind.DeviceEncrypted,
        "external_data" => DataRootKind.ExternalData,
        "obb" => DataRootKind.Obb,
        _ => DataRootKind.Unknown
    };

    public async Task<bool> RequestRestoreApprovalAsync(
        string snapshotId,
        int itemCount,
        TimeSpan timeout,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var request = await SendCommandAsync(
            "request_restore", new { snapshotId, itemCount }, cancellationToken);
        if (!request.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException("Agent отклонил запрос Restore.");
        var token = request.RootElement.GetProperty("approvalToken").GetString()
            ?? throw new InvalidDataException("Agent не вернул approval token.");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            progress?.Report(new("restore-approval", "Подтвердите Restore на телефоне", 0, 1));
            using var status = await SendCommandAsync(
                "restore_status", new { approvalToken = token }, cancellationToken);
            if (status.RootElement.TryGetProperty("approved", out var approved) && approved.GetBoolean())
                return true;
            if (status.RootElement.TryGetProperty("rejected", out var rejected) && rejected.GetBoolean())
                throw new UnauthorizedAccessException("Restore отклонён на телефоне.");
            await Task.Delay(1000, cancellationToken);
        }
        return false;
    }

    public async Task<bool> RequestRootAsync(CancellationToken cancellationToken = default) =>
        (await RequestRootDetailsAsync(cancellationToken)).Granted;

    public async Task<RootRequestResult> RequestRootDetailsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync("root_request", cancellationToken: cancellationToken);
        var root = response.RootElement;
        EnsureOk(root, "root_request");
        var available = ReadRequiredBoolean(root, "available", "root_request");
        var granted = ReadRequiredBoolean(root, "granted", "root_request");
        var provider = ReadRequiredString(root, "provider", "root_request");
        var detail = ReadRequiredString(root, "detail", "root_request");
        var diagnostics = root.TryGetProperty("diagnostics", out var trace) &&
                          trace.ValueKind == JsonValueKind.Object
            ? ParseRootDiagnostics(trace)
            : null;
        var helper = root.TryGetProperty("helper", out var helperElement) &&
                     helperElement.ValueKind == JsonValueKind.Object
            ? ParseRootHelperDiagnostics(helperElement)
            : null;
        if (!granted && string.IsNullOrWhiteSpace(detail))
            throw new InvalidDataException(
                "Agent отклонил root без объяснения. Откройте Diagnostics и обновите Agent Dev.");
        var result = new RootRequestResult(
            available,
            granted,
            provider,
            detail,
            diagnostics,
            helper);
        DesktopDiagnostics.Log(
            granted ? "info" : "warn",
            "root",
            "agent_result",
            detail,
            operationId: diagnostics?.AttemptId,
            durationMs: diagnostics?.DurationMs,
            result: granted ? "granted" : available ? "denied" : "unavailable",
            fields: new Dictionary<string, object?>
            {
                ["available"] = available,
                ["granted"] = granted,
                ["provider"] = provider,
                ["appUid"] = diagnostics?.AppUid,
                ["appGrantState"] = diagnostics?.AppGrantState,
                ["cachedShellPresent"] = diagnostics?.CachedShellPresent,
                ["cachedShellRoot"] = diagnostics?.CachedShellRoot,
                ["cachedShellClosed"] = diagnostics?.CachedShellClosed,
                ["shellExitCode"] = diagnostics?.ShellExitCode,
                ["shellSuccess"] = diagnostics?.ShellSuccess,
                ["stdout"] = diagnostics?.Stdout,
                ["stderr"] = diagnostics?.Stderr,
                ["selinux"] = diagnostics?.Selinux,
                ["exceptionType"] = diagnostics?.ExceptionType,
                ["exceptionMessage"] = diagnostics?.ExceptionMessage,
                ["helperOk"] = helper?.Ok,
                ["helperExitCode"] = helper?.ExitCode,
                ["helperStdout"] = helper?.Stdout,
                ["helperStderr"] = helper?.Stderr,
                ["helperExceptionType"] = helper?.ExceptionType
            });
        return result;
    }

    public async Task<AgentDiagnosticsSnapshot> GetDiagnosticsSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync(
            "diagnostics_snapshot",
            cancellationToken: cancellationToken);
        EnsureOk(response.RootElement, "diagnostics_snapshot");
        if (!response.RootElement.TryGetProperty("snapshot", out var snapshot) ||
            snapshot.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Agent не вернул diagnostics snapshot.");
        var build = ParseBuildInfo(snapshot, ProtocolConstants.ProtocolVersion);
        var events = new List<DiagnosticEvent>();
        if (snapshot.TryGetProperty("events", out var eventArray) &&
            eventArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in eventArray.EnumerateArray())
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<DiagnosticEvent>(
                        item.GetRawText(),
                        AgentJsonOptions);
                    if (parsed is not null) events.Add(parsed);
                }
                catch (JsonException error)
                {
                    DesktopDiagnostics.Log(
                        "warn",
                        "protocol",
                        "diagnostic_event_invalid",
                        "Agent diagnostic event could not be parsed",
                        error: error);
                }
            }
        }
        return new(build, events, snapshot.GetRawText());
    }

    public async Task ClearAgentDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync(
            "diagnostics_clear",
            cancellationToken: cancellationToken);
        EnsureOk(response.RootElement, "diagnostics_clear");
    }

    public async Task BeginPackageSnapshotAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync(
            "snapshot_begin", new { packageName }, cancellationToken);
        if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException($"Agent не смог остановить {packageName}.");
    }

    public async Task EndPackageSnapshotAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync(
            "snapshot_end", new { packageName }, cancellationToken);
        if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException($"Agent не восстановил stopped-state {packageName}.");
    }

    public async Task PreparePackageRestoreAsync(
        string packageName,
        IReadOnlyCollection<string> roots,
        bool completeSnapshot,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync(
            "restore_prepare", new { packageName, roots, completeSnapshot }, cancellationToken);
        if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException(
                $"Agent не смог подготовить Restore {packageName}: " +
                (response.RootElement.TryGetProperty("error", out var error)
                    ? error.GetString()
                    : "unknown_error"));
    }

    public async Task FinishPackageRestoreAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync(
            "restore_finish", new { packageName }, cancellationToken);
        if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new PackageRestoreCommitException(
                $"Agent не смог завершить Restore {packageName}: " +
                (response.RootElement.TryGetProperty("error", out var error)
                    ? error.GetString()
                    : "commit_failed"),
                response.RootElement.TryGetProperty("rolledBack", out var rolledBack) &&
                rolledBack.ValueKind == JsonValueKind.True);
    }

    public async Task AbortPackageRestoreAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync(
            "restore_abort", new { packageName }, cancellationToken);
        if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException(
                $"Agent не смог откатить незавершённый Restore {packageName}.");
    }

    public async Task<IReadOnlyList<string>> ApplyPackagePolicyAsync(
        PackageBackupMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync(
            "restore_policy",
            new
            {
                packageName = metadata.PackageName,
                enabled = metadata.WasEnabled,
                batteryOptimizationExempt = metadata.BatteryOptimizationExempt,
                runtimePermissions = metadata.RuntimePermissions ?? [],
                appLocales = metadata.AppLocales
            },
            cancellationToken);
        if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException($"Agent не смог применить policy {metadata.PackageName}.");
        return response.RootElement.GetProperty("failures").EnumerateArray()
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToList();
    }

    public async Task<JsonDocument> SendCommandAsync(
        string command,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var request = CreateSignedRequestEnvelope(command, payload);
        DesktopDiagnostics.Log(
            "info",
            "protocol",
            "command_started",
            "Agent command started",
            requestId: request.RequestId,
            fields: new Dictionary<string, object?>
            {
                ["command"] = command,
                ["payloadBytes"] = request.Payload.Length
            });
        try
        {
            await WriteFrameAsync(TransferFrameType.Command, request.Payload, cancellationToken);
            var (type, response) = await ReadFrameAsync(cancellationToken);
            if (type is TransferFrameType.Error)
                throw new InvalidOperationException(Encoding.UTF8.GetString(response));
            if (type is not TransferFrameType.Response)
                throw new InvalidDataException($"Unexpected Agent frame: {type}");
            var document = JsonDocument.Parse(response);
            ValidateResponseEnvelope(document.RootElement, request.RequestId, command);
            var result = document.RootElement.TryGetProperty("ok", out var ok) &&
                         ok.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? ok.GetBoolean() ? "ok" : ReadOptionalString(document.RootElement, "error") ?? "failed"
                : "no-ok-field";
            DesktopDiagnostics.Log(
                result == "ok" ? "info" : "warn",
                "protocol",
                "command_completed",
                "Agent command completed",
                requestId: request.RequestId,
                durationMs: stopwatch.ElapsedMilliseconds,
                result: result,
                fields: new Dictionary<string, object?>
                {
                    ["command"] = command,
                    ["responseBytes"] = response.Length
                });
            return document;
        }
        catch (Exception error)
        {
            DesktopDiagnostics.Log(
                "error",
                "protocol",
                "command_failed",
                "Agent command failed",
                requestId: request.RequestId,
                durationMs: stopwatch.ElapsedMilliseconds,
                error: error,
                fields: new Dictionary<string, object?>
                {
                    ["command"] = command
                });
            throw;
        }
    }

    public async IAsyncEnumerable<FileEntry> ScanRootAsync(
        string root,
        bool includeCaches,
        bool fullHash,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var entry in ScanAsync(
                           "root_scan",
                           new { root, includeCaches, fullHash },
                           cancellationToken))
            yield return entry;
    }

    public async Task<SharedStorageCapability> GetSharedStorageAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync("shared_roots", cancellationToken: cancellationToken);
        var roots = response.RootElement.GetProperty("roots").EnumerateArray()
            .Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList();
        return new(
            response.RootElement.GetProperty("accessGranted").GetBoolean(),
            roots);
    }

    public async Task<PersonalDataCapability> GetPersonalDataAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync("personal_status", cancellationToken: cancellationToken);
        var permissions = response.RootElement.GetProperty("permissions");
        return new(
            permissions.GetProperty("contacts").GetBoolean(),
            permissions.GetProperty("messages").GetBoolean(),
            permissions.GetProperty("calls").GetBoolean());
    }

    public async Task<byte[]> ExportSystemStateAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync("system_state", cancellationToken: cancellationToken);
        return Encoding.UTF8.GetBytes(response.RootElement.GetProperty("state").GetRawText());
    }

    public async Task<string?> GetHomePackageAsync(
        CancellationToken cancellationToken = default)
    {
        var stateJson = await ExportSystemStateAsync(cancellationToken);
        using var state = JsonDocument.Parse(stateJson);
        if (!state.RootElement.TryGetProperty("roles", out var roles) ||
            !roles.TryGetProperty("android.app.role.HOME", out var holders) ||
            holders.ValueKind != JsonValueKind.Array ||
            holders.GetArrayLength() == 0)
            return null;
        var packageName = holders[0].GetString();
        return string.IsNullOrWhiteSpace(packageName) ? null : packageName;
    }

    public async Task<byte[]> ExportAccountInventoryAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync(
            "account_inventory",
            cancellationToken: cancellationToken);
        return Encoding.UTF8.GetBytes(
            response.RootElement.GetProperty("inventory").GetRawText());
    }

    public async Task<MediaCapability> GetMediaCapabilityAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync(
            "media_status",
            cancellationToken: cancellationToken);
        var permissions = response.RootElement.GetProperty("permissions");
        return new(
            permissions.GetProperty("images").GetBoolean(),
            permissions.GetProperty("videos").GetBoolean(),
            permissions.GetProperty("allFiles").GetBoolean());
    }

    public async Task<SystemStateRestoreResult> RestoreSystemStateAsync(
        byte[] stateJson,
        IReadOnlyCollection<string>? confirmedSettingIds = null,
        CancellationToken cancellationToken = default)
    {
        using var state = JsonDocument.Parse(stateJson);
        using var response = await SendCommandAsync(
            "restore_system_state",
            new
            {
                state = state.RootElement.Clone(),
                confirmedSettingIds = confirmedSettingIds ?? []
            },
            cancellationToken);
        if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException("Agent не смог применить безопасные настройки Android.");
        var failures = response.RootElement.GetProperty("failures").EnumerateArray()
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToList();
        var needsConfirmation = response.RootElement
            .GetProperty("needsConfirmation").EnumerateArray()
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var outcomes = response.RootElement.GetProperty("outcomes").EnumerateArray()
            .Select(x => new SettingApplyOutcome(
                x.GetProperty("id").GetString() ?? string.Empty,
                x.GetProperty("status").GetString() ?? "unknown",
                x.GetProperty("reason").GetString() ?? string.Empty,
                x.TryGetProperty("adapter", out var adapter) && adapter.ValueKind == JsonValueKind.String
                    ? adapter.GetString()
                    : null))
            .Where(x => x.Id.Length > 0)
            .ToList();
        return new(failures, needsConfirmation, outcomes);
    }

    public async IAsyncEnumerable<FileEntry> ScanSharedAsync(
        string root,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var entry in ScanAsync(
                           "shared_scan",
                           new { root },
                           cancellationToken))
            yield return entry;
    }

    public async IAsyncEnumerable<FileEntry> ScanMediaAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var entry in ScanAsync(
                           "media_scan",
                           new { },
                           cancellationToken))
            yield return entry;
    }

    private async IAsyncEnumerable<FileEntry> ScanAsync(
        string command,
        object payloadObject,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = CreateSignedRequest(command, payloadObject);
        await WriteFrameAsync(TransferFrameType.Command, request, cancellationToken);
        while (true)
        {
            var (type, payload) = await ReadFrameAsync(cancellationToken);
            if (type == TransferFrameType.End) yield break;
            if (type == TransferFrameType.Error)
                throw new InvalidOperationException(Encoding.UTF8.GetString(payload));
            if (type != TransferFrameType.FileMetadata)
                throw new InvalidDataException($"Unexpected root scan frame: {type}");
            yield return JsonSerializer.Deserialize<FileEntry>(payload, AgentJsonOptions)
                ?? throw new InvalidDataException("Agent returned invalid FileEntry.");
        }
    }

    public async Task<Stream> OpenRootFileAsync(
        string root,
        string relative,
        CancellationToken cancellationToken = default)
    {
        var request = CreateSignedRequest("root_read", new { root, relative });
        await WriteFrameAsync(TransferFrameType.Command, request, cancellationToken);
        return new RootReadStream(this);
    }

    public async Task<Stream> OpenSharedFileAsync(
        string root,
        string relative,
        CancellationToken cancellationToken = default)
    {
        var request = CreateSignedRequest("shared_read", new { root, relative });
        await WriteFrameAsync(TransferFrameType.Command, request, cancellationToken);
        return new RootReadStream(this);
    }

    public async Task<Stream> OpenPersonalDataAsync(
        string kind,
        CancellationToken cancellationToken = default)
    {
        if (kind is not ("contacts" or "messages" or "calls"))
            throw new ArgumentOutOfRangeException(nameof(kind));
        var request = CreateSignedRequest("personal_export", new { kind });
        await WriteFrameAsync(TransferFrameType.Command, request, cancellationToken);
        return new RootReadStream(this);
    }

    public async Task<Stream> OpenWallpaperAsync(
        string kind,
        CancellationToken cancellationToken = default)
    {
        if (kind is not ("system" or "lock"))
            throw new ArgumentOutOfRangeException(nameof(kind));
        var request = CreateSignedRequest("root_wallpaper_read", new { kind });
        await WriteFrameAsync(TransferFrameType.Command, request, cancellationToken);
        return new RootReadStream(this);
    }

    public async Task<PersonalRestoreResult> RestorePersonalDataAsync(
        string kind,
        Func<Stream, CancellationToken, Task> writeContent,
        CancellationToken cancellationToken = default)
    {
        if (kind is not ("contacts" or "messages" or "calls"))
            throw new ArgumentOutOfRangeException(nameof(kind));
        var request = CreateSignedRequest("personal_restore", new { kind });
        await WriteFrameAsync(TransferFrameType.Command, request, cancellationToken);
        var (readyType, readyPayload) = await ReadFrameAsync(cancellationToken);
        if (readyType == TransferFrameType.Error)
            throw new IOException(Encoding.UTF8.GetString(readyPayload));
        if (readyType != TransferFrameType.Response)
            throw new InvalidDataException($"Unexpected personal restore response: {readyType}");
        using (var ready = JsonDocument.Parse(readyPayload))
        {
            if (!ready.RootElement.TryGetProperty("ready", out var isReady) || !isReady.GetBoolean())
                throw new InvalidOperationException("Agent не готов принять personal restore stream.");
        }

        var output = new ProtocolDataWriteStream(this);
        Exception? producerError = null;
        byte[]? response = null;
        try
        {
            await writeContent(output, cancellationToken);
        }
        catch (Exception error)
        {
            producerError = error;
        }
        try
        {
            if (producerError is null)
                response = await output.CompleteResponseAsync(cancellationToken);
            else
            {
                using var abortTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await output.AbortAsync(abortTimeout.Token);
            }
        }
        catch when (producerError is not null) { }
        if (producerError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producerError).Throw();
        using var document = JsonDocument.Parse(response!);
        var root = document.RootElement;
        return new(
            kind,
            root.TryGetProperty("status", out var status) ? status.GetString() ?? "failed" : "failed",
            root.TryGetProperty("inserted", out var inserted) ? inserted.GetInt32() : 0,
            root.TryGetProperty("skipped", out var skipped) ? skipped.GetInt32() : 0,
            root.TryGetProperty("failed", out var failed) ? failed.GetInt32() : 0,
            root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : root.TryGetProperty("error", out var errorElement)
                    ? errorElement.GetString()
                    : null);
    }

    public Task RestoreWallpaperAsync(
        string kind,
        long expectedSize,
        string? expectedBlake3,
        Func<Stream, CancellationToken, Task> writeContent,
        CancellationToken cancellationToken = default)
    {
        if (kind is not ("system" or "lock"))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return RestoreEntryAsync(
            "wallpaper_restore",
            new { kind, expectedSize, expectedBlake3 },
            "file",
            writeContent,
            cancellationToken);
    }

    public async Task<Stream> OpenMediaFileAsync(
        string contentUri,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(contentUri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "content", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "media", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Некорректный MediaStore URI.", nameof(contentUri));
        var request = CreateSignedRequest("media_read", new { uri = contentUri });
        await WriteFrameAsync(TransferFrameType.Command, request, cancellationToken);
        return new RootReadStream(this);
    }

    public async Task<AgentMediaReadStream> OpenMediaFileV2Async(
        string contentUri,
        long offset,
        long expectedSize,
        long expectedModifiedUnixNanos,
        CancellationToken cancellationToken = default)
    {
        ValidateMediaUri(contentUri);
        if (offset < 0 || offset > expectedSize)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var request = CreateSignedRequest("media_read_v2", new
        {
            uri = contentUri,
            offset,
            expectedSize,
            expectedModifiedUnixNanos
        });
        await WriteFrameAsync(TransferFrameType.Command, request, cancellationToken);
        return new(this);
    }

    public async Task<AgentMediaReadStream> OpenMediaProbeAsync(
        long length,
        CancellationToken cancellationToken = default)
    {
        if (length is < 0 or > 64L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(length));
        var request = CreateSignedRequest("media_probe", new { length });
        await WriteFrameAsync(TransferFrameType.Command, request, cancellationToken);
        return new(this);
    }

    public async Task<FastMediaSession> OpenFastMediaSessionAsync(
        byte[] sessionKey,
        int workers,
        CancellationToken cancellationToken = default)
    {
        if (sessionKey.Length != FastMediaProtocol.SessionKeyBytes)
            throw new ArgumentException("Fast media session key must contain 32 bytes.", nameof(sessionKey));
        using var response = await SendCommandAsync(
            "media_session_open",
            new
            {
                sessionKey = Convert.ToBase64String(sessionKey),
                workers = Math.Clamp(workers, 1, 4)
            },
            cancellationToken);
        if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new IOException(
                response.RootElement.TryGetProperty("error", out var error)
                    ? error.GetString()
                    : "Agent could not open a Fast LAN session.");
        var session = response.RootElement.GetProperty("session");
        return new(
            session.GetProperty("sessionId").GetString()
                ?? throw new InvalidDataException("Fast LAN session ID is missing."),
            session.GetProperty("host").GetString()
                ?? throw new InvalidDataException("Fast LAN host is missing."),
            session.GetProperty("port").GetInt32(),
            DateTimeOffset.FromUnixTimeMilliseconds(
                session.GetProperty("expiresAtUtcMillis").GetInt64()),
            session.GetProperty("maxWorkers").GetInt32());
    }

    public async Task CloseFastMediaSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync(
            "media_session_close",
            new { sessionId },
            cancellationToken);
    }

    public async Task RestoreRootEntryAsync(
        string packageName,
        string root,
        FileEntry entry,
        int remappedUid,
        Func<Stream, CancellationToken, Task>? writeContent,
        CancellationToken cancellationToken = default)
    {
        if (entry.Kind is not ("file" or "directory"))
            throw new NotSupportedException($"Root restore не поддерживает тип {entry.Kind}.");
        if (!RestorePathPolicy.IsSafeRelativePath(entry.RelativePath))
            throw new InvalidDataException($"Небезопасный путь Restore: {entry.RelativePath}");
        await RestoreEntryAsync("root_restore", new
        {
            packageName,
            root,
            relative = entry.RelativePath,
            kind = entry.Kind,
            mode = entry.Mode,
            uid = remappedUid,
            gid = remappedUid,
            modifiedUnixNanos = entry.ModifiedUnixNanos,
            expectedSize = entry.Size,
            expectedBlake3 = entry.ContentHash,
            selinuxLabel = (string?)null
        }, entry.Kind, writeContent, cancellationToken);
    }

    public async Task RestoreSharedEntryAsync(
        string root,
        FileEntry entry,
        Func<Stream, CancellationToken, Task>? writeContent,
        CancellationToken cancellationToken = default)
    {
        if (entry.Kind is not ("file" or "directory"))
            throw new NotSupportedException($"Shared restore не поддерживает тип {entry.Kind}.");
        if (!RestorePathPolicy.IsSafeRelativePath(entry.RelativePath))
            throw new InvalidDataException($"Небезопасный путь Restore: {entry.RelativePath}");
        await RestoreEntryAsync("shared_restore", new
        {
            root,
            relative = entry.RelativePath,
            kind = entry.Kind,
            modifiedUnixNanos = entry.ModifiedUnixNanos,
            expectedSize = entry.Size,
            expectedBlake3 = entry.ContentHash
        }, entry.Kind, writeContent, cancellationToken);
    }

    private async Task RestoreEntryAsync(
        string command,
        object payload,
        string kind,
        Func<Stream, CancellationToken, Task>? writeContent,
        CancellationToken cancellationToken)
    {
        var request = CreateSignedRequest(command, payload);
        await WriteFrameAsync(TransferFrameType.Command, request, cancellationToken);
        var (readyType, readyPayload) = await ReadFrameAsync(cancellationToken);
        if (readyType == TransferFrameType.Error)
            throw new IOException(Encoding.UTF8.GetString(readyPayload));
        if (readyType != TransferFrameType.Response)
            throw new InvalidDataException($"Unexpected restore preflight response: {readyType}");
        using (var ready = JsonDocument.Parse(readyPayload))
        {
            if (!ready.RootElement.TryGetProperty("ready", out var isReady) || !isReady.GetBoolean())
                throw new InvalidOperationException("Agent не готов принять Restore stream.");
        }
        var output = new ProtocolDataWriteStream(this);
        Exception? producerError = null;
        try
        {
            if (kind == "file")
            {
                if (writeContent is null)
                    throw new ArgumentNullException(nameof(writeContent));
                await writeContent(output, cancellationToken);
            }
        }
        catch (Exception error)
        {
            producerError = error;
        }

        try
        {
            if (producerError is null)
                await output.CompleteAsync(cancellationToken);
            else
            {
                using var abortTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await output.AbortAsync(abortTimeout.Token);
            }
        }
        catch when (producerError is not null)
        {
            // Preserve the repository/decryption error that stopped the producer.
        }
        if (producerError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producerError).Throw();
    }

    private byte[] CreateSignedRequest(string command, object? payload) =>
        CreateSignedRequestEnvelope(command, payload).Payload;

    private SignedRequest CreateSignedRequestEnvelope(string command, object? payload)
    {
        var requestId = Guid.NewGuid().ToString("D");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var payloadBytes = CanonicalPayload(payload);
        var payloadHash = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
        var canonical = Encoding.UTF8.GetBytes(
            $"{ProtocolConstants.ProtocolVersion}\n{requestId}\n{command}\n{timestamp}\n{nonce}\n{payloadHash}");
        var signature = Convert.ToBase64String(_identity.Sign(canonical));
        return new(
            requestId,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                protocolVersion = ProtocolConstants.ProtocolVersion,
                requestId,
                command,
                desktopKey = _desktopKey,
                timestamp,
                nonce,
                payloadHash,
                signature,
                payload
            }));
    }

    internal static void ValidateResponseEnvelope(
        JsonElement root,
        string expectedRequestId,
        string command)
    {
        if (!root.TryGetProperty("protocolVersion", out var protocol) ||
            protocol.ValueKind != JsonValueKind.Number ||
            !protocol.TryGetInt32(out var protocolVersion))
            throw new InvalidDataException(
                $"Agent response for {command} has no protocolVersion.");
        if (protocolVersion != ProtocolConstants.ProtocolVersion)
            throw new InvalidDataException(
                $"Agent protocol mismatch for {command}: " +
                $"expected {ProtocolConstants.ProtocolVersion}, received {protocolVersion}.");
        var requestId = ReadRequiredString(root, "requestId", command);
        if (!string.Equals(requestId, expectedRequestId, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Agent requestId mismatch for {command}: " +
                $"expected {expectedRequestId}, received {requestId}.");
    }

    internal static void EnsureOk(JsonElement root, string command)
    {
        if (!root.TryGetProperty("ok", out var ok) ||
            ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Agent response for {command} has no boolean ok field.");
        if (ok.GetBoolean()) return;
        var code = ReadOptionalString(root, "error") ?? "unknown_error";
        var message = ReadOptionalString(root, "message");
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(message)
                ? $"Agent rejected {command}: {code}."
                : $"Agent rejected {command}: {code} — {message}");
    }

    internal static AgentBuildInfo ParseBuildInfo(JsonElement build, int protocolVersion)
    {
        return new(
            ReadRequiredString(build, "channel", "build"),
            ReadRequiredString(build, "packageName", "build"),
            ReadRequiredString(build, "versionName", "build"),
            ReadRequiredInt32(build, "versionCode", "build"),
            ReadRequiredString(build, "buildId", "build"),
            ReadRequiredInt32(build, "agentPort", "build"),
            ReadRequiredBoolean(build, "diagnosticsEnabled", "build"),
            protocolVersion);
    }

    internal static AgentRootDiagnostics ParseRootDiagnostics(JsonElement trace)
    {
        var timestampText = ReadRequiredString(trace, "timestampUtc", "root diagnostics");
        if (!DateTimeOffset.TryParse(
                timestampText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var timestamp))
            throw new InvalidDataException("Agent root diagnostics contains invalid timestampUtc.");
        var steps = trace.TryGetProperty("steps", out var stepArray) &&
                    stepArray.ValueKind == JsonValueKind.Array
            ? stepArray.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToList()
            : [];
        return new(
            ReadRequiredString(trace, "attemptId", "root diagnostics"),
            timestamp,
            ReadRequiredBoolean(trace, "requestGrant", "root diagnostics"),
            ReadRequiredInt32(trace, "appUid", "root diagnostics"),
            ReadRequiredString(trace, "appGrantState", "root diagnostics"),
            ReadRequiredBoolean(trace, "cachedShellPresent", "root diagnostics"),
            ReadRequiredBoolean(trace, "cachedShellAlive", "root diagnostics"),
            ReadRequiredBoolean(trace, "cachedShellRoot", "root diagnostics"),
            ReadRequiredBoolean(trace, "cachedShellClosed", "root diagnostics"),
            ReadOptionalInt32(trace, "shellExitCode"),
            ReadRequiredBoolean(trace, "shellSuccess", "root diagnostics"),
            ReadRequiredString(trace, "stdout", "root diagnostics", allowEmpty: true),
            ReadRequiredString(trace, "stderr", "root diagnostics", allowEmpty: true),
            ReadRequiredString(trace, "selinux", "root diagnostics", allowEmpty: true),
            ReadOptionalString(trace, "exceptionType"),
            ReadOptionalString(trace, "exceptionMessage"),
            ReadRequiredInt64(trace, "durationMs", "root diagnostics"),
            steps);
    }

    internal static AgentRootHelperDiagnostics ParseRootHelperDiagnostics(JsonElement helper)
    {
        return new(
            ReadRequiredBoolean(helper, "ok", "root helper diagnostics"),
            ReadOptionalInt32(helper, "exitCode"),
            ReadOptionalInt32(helper, "uid"),
            ReadRequiredString(helper, "stdout", "root helper diagnostics", allowEmpty: true),
            ReadRequiredString(helper, "stderr", "root helper diagnostics", allowEmpty: true),
            ReadOptionalString(helper, "exceptionType"));
    }

    internal static bool ReadRequiredBoolean(
        JsonElement root,
        string name,
        string context)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException(
                $"Agent response for {context} has no boolean {name} field.");
        return value.GetBoolean();
    }

    internal static string ReadRequiredString(
        JsonElement root,
        string name,
        string context,
        bool allowEmpty = false)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException(
                $"Agent response for {context} has no string {name} field.");
        var result = value.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(result))
            throw new InvalidDataException(
                $"Agent response for {context} contains an empty {name} field.");
        return result;
    }

    private static int ReadRequiredInt32(JsonElement root, string name, string context)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
            throw new InvalidDataException(
                $"Agent response for {context} has no integer {name} field.");
        return result;
    }

    private static long ReadRequiredInt64(JsonElement root, string name, string context)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var result))
            throw new InvalidDataException(
                $"Agent response for {context} has no integer {name} field.");
        return result;
    }

    private static int? ReadOptionalInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new InvalidDataException($"Agent response contains invalid {name} field.");
        return result;
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Agent response contains invalid {name} field.");
        return value.GetString();
    }

    internal static byte[] CanonicalPayload(object? payload)
    {
        var element = JsonSerializer.SerializeToElement(payload);
        var builder = new StringBuilder();
        WriteCanonical(builder, element);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void WriteCanonical(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in value.EnumerateObject()
                             .OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty) builder.Append(',');
                    firstProperty = false;
                    WriteCanonicalString(builder, property.Name);
                    builder.Append(':');
                    WriteCanonical(builder, property.Value);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem) builder.Append(',');
                    firstItem = false;
                    WriteCanonical(builder, item);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                WriteCanonicalString(builder, value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                builder.Append(value.GetRawText());
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    private static void WriteCanonicalString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < ' ')
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }
        builder.Append('"');
    }

    private async Task WriteFrameAsync(
        TransferFrameType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProtocolIdleTimeout);
        var header = new byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
        await _stream.WriteAsync(header, timeout.Token);
        await _stream.WriteAsync(payload, timeout.Token);
    }

    private async Task<(TransferFrameType Type, byte[] Payload)> ReadFrameAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProtocolIdleTimeout);
        var header = new byte[5];
        await _stream.ReadExactlyAsync(header, timeout.Token);
        var type = (TransferFrameType)header[0];
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        var max = type == TransferFrameType.Data
            ? ProtocolConstants.DataFrameBytes
            : ProtocolConstants.MaxJsonFrameBytes;
        if (length < 0 || length > max)
            throw new InvalidDataException($"Agent frame length is invalid: {length}");
        var payload = new byte[length];
        await _stream.ReadExactlyAsync(payload, timeout.Token);
        return (type, payload);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stream.DisposeAsync();
        }
        finally
        {
            _client.Dispose();
            _identity.Dispose();
            if (_ownsForward && _adb is not null && _serial is not null)
                await _adb.RemoveAgentForwardAsync(
                    _serial,
                    _forwardPort,
                    CancellationToken.None);
        }
    }

    private static void ValidateMediaUri(string contentUri)
    {
        if (!Uri.TryCreate(contentUri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "content", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "media", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Некорректный MediaStore URI.", nameof(contentUri));
    }

    private static readonly JsonSerializerOptions AgentJsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record SignedRequest(string RequestId, byte[] Payload);

    private sealed class RootReadStream(AgentClient owner) : Stream
    {
        private byte[]? _current;
        private int _offset;
        private bool _completed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_completed) return 0;
            while (_current is null || _offset >= _current.Length)
            {
                var (type, payload) = await owner.ReadFrameAsync(cancellationToken);
                if (type == TransferFrameType.End)
                {
                    _completed = true;
                    return 0;
                }
                if (type == TransferFrameType.Error)
                {
                    _completed = true;
                    throw new IOException(Encoding.UTF8.GetString(payload));
                }
                if (type != TransferFrameType.Data)
                {
                    _completed = true;
                    throw new InvalidDataException($"Unexpected root read frame: {type}");
                }
                _current = payload;
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_completed)
                DrainOrPoisonAsync().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_completed) await DrainOrPoisonAsync();
            GC.SuppressFinalize(this);
        }

        private async Task DrainOrPoisonAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                while (!_completed)
                {
                    var (type, _) = await owner.ReadFrameAsync(timeout.Token);
                    if (type is TransferFrameType.End or TransferFrameType.Error)
                        _completed = true;
                }
            }
            catch
            {
                _completed = true;
                owner._client.Dispose();
            }
        }
    }

    public sealed class AgentMediaReadStream(AgentClient owner) : Stream
    {
        private int _remainingDataBytes;
        private bool _completed;
        private readonly TaskCompletionSource<MediaReadCompletion> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MediaReadCompletion> Completion => _completion.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_completed) return 0;
            if (buffer.Length == 0) return 0;
            try
            {
                while (_remainingDataBytes == 0)
                {
                    var header = new byte[5];
                    await owner._stream.ReadExactlyAsync(header, cancellationToken);
                    var type = (TransferFrameType)header[0];
                    var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                    var maximum = type == TransferFrameType.Data
                        ? ProtocolConstants.DataFrameBytes
                        : ProtocolConstants.MaxJsonFrameBytes;
                    if (length < 0 || length > maximum)
                        throw new InvalidDataException(
                            $"Agent media frame length is invalid: {length}");
                    if (type == TransferFrameType.Data)
                    {
                        _remainingDataBytes = length;
                        if (length == 0) continue;
                        break;
                    }
                    var payload = new byte[length];
                    await owner._stream.ReadExactlyAsync(payload, cancellationToken);
                    if (type == TransferFrameType.End)
                    {
                        using var document = JsonDocument.Parse(payload);
                        var root = document.RootElement;
                        var completion = new MediaReadCompletion(
                            root.GetProperty("sourceSize").GetInt64(),
                            root.GetProperty("modifiedUnixNanos").GetInt64(),
                            root.GetProperty("acceptedOffset").GetInt64(),
                            root.GetProperty("transferredBytes").GetInt64(),
                            root.GetProperty("sha256").GetString() ?? string.Empty);
                        _completed = true;
                        _completion.TrySetResult(completion);
                        return 0;
                    }
                    if (type == TransferFrameType.Error)
                        throw new IOException(Encoding.UTF8.GetString(payload));
                    if (type != TransferFrameType.Data)
                        throw new InvalidDataException($"Unexpected media read frame: {type}");
                }
                var count = Math.Min(buffer.Length, _remainingDataBytes);
                var read = await owner._stream.ReadAsync(buffer[..count], cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException("Agent closed a media DATA frame early.");
                _remainingDataBytes -= read;
                return read;
            }
            catch (Exception error)
            {
                _completion.TrySetException(error);
                throw;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ProtocolDataWriteStream(AgentClient owner) : Stream
    {
        private bool _completed;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_completed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            while (!buffer.IsEmpty)
            {
                var count = Math.Min(buffer.Length, ProtocolConstants.DataFrameBytes);
                await owner.WriteFrameAsync(
                    TransferFrameType.Data, buffer[..count], cancellationToken);
                buffer = buffer[count..];
            }
        }

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            _ = await CompleteResponseAsync(cancellationToken);
        }

        public async Task<byte[]> CompleteResponseAsync(CancellationToken cancellationToken)
        {
            if (_completed) return [];
            _completed = true;
            await owner.WriteFrameAsync(
                TransferFrameType.End, ReadOnlyMemory<byte>.Empty, cancellationToken);
            var (type, payload) = await owner.ReadFrameAsync(cancellationToken);
            if (type == TransferFrameType.Error)
                throw new IOException(Encoding.UTF8.GetString(payload));
            if (type != TransferFrameType.End)
                throw new InvalidDataException($"Unexpected root restore response: {type}");
            return payload;
        }

        public async Task AbortAsync(CancellationToken cancellationToken)
        {
            if (_completed) return;
            _completed = true;
            await owner.WriteFrameAsync(
                TransferFrameType.Abort, ReadOnlyMemory<byte>.Empty, cancellationToken);
            var (type, payload) = await owner.ReadFrameAsync(cancellationToken);
            if (type is not (TransferFrameType.End or TransferFrameType.Error))
                throw new InvalidDataException($"Unexpected root abort response: {type}");
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class PackageRestoreCommitException(
    string message,
    bool rolledBack) : InvalidOperationException(message)
{
    public bool RolledBack { get; } = rolledBack;
}

public sealed record SharedStorageCapability(
    bool AccessGranted,
    IReadOnlyList<string> Roots);

public sealed record MediaReadCompletion(
    long SourceSize,
    long ModifiedUnixNanos,
    long AcceptedOffset,
    long TransferredBytes,
    string Sha256);

public sealed record PersonalDataCapability(
    bool Contacts,
    bool Messages,
    bool Calls);

public sealed record MediaCapability(
    bool Images,
    bool Videos,
    bool AllFiles);

internal sealed class DesktopIdentity : IDisposable
{
    private readonly Key _key;
    public string PublicKey { get; }

    private DesktopIdentity(Key key)
    {
        _key = key;
        PublicKey = Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    public static DesktopIdentity LoadOrCreate()
    {
        var directory = AppRuntimeProfile.RoamingRoot;
        var path = Path.Combine(directory, "desktop.key");
        Directory.CreateDirectory(directory);
        Key key;
        if (File.Exists(path))
        {
            var stored = File.ReadAllText(path).Trim();
            byte[] bytes;
            if (stored.StartsWith('{'))
            {
                using var document = JsonDocument.Parse(stored);
                var protectedBytes = Convert.FromBase64String(
                    document.RootElement.GetProperty("protectedKey").GetString()
                    ?? throw new InvalidDataException("Desktop identity is invalid."));
                bytes = ProtectedData.Unprotect(
                    protectedBytes,
                    IdentityEntropy,
                    DataProtectionScope.CurrentUser);
            }
            else
            {
                // One-time migration from the early plaintext development format.
                bytes = Convert.FromBase64String(stored);
                SaveProtected(path, bytes);
            }
            key = Key.Import(SignatureAlgorithm.Ed25519, bytes, KeyBlobFormat.RawPrivateKey);
            CryptographicOperations.ZeroMemory(bytes);
        }
        else
        {
            key = Key.Create(
                SignatureAlgorithm.Ed25519,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
            var bytes = key.Export(KeyBlobFormat.RawPrivateKey);
            SaveProtected(path, bytes);
            CryptographicOperations.ZeroMemory(bytes);
        }
        return new(key);
    }

    private static void SaveProtected(string path, byte[] privateKey)
    {
        var protectedBytes = ProtectedData.Protect(
            privateKey,
            IdentityEntropy,
            DataProtectionScope.CurrentUser);
        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            protection = "dpapi-current-user",
            protectedKey = Convert.ToBase64String(protectedBytes)
        });
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    public byte[] Sign(ReadOnlySpan<byte> message) =>
        SignatureAlgorithm.Ed25519.Sign(_key, message);

    public void Dispose() => _key.Dispose();

    private static readonly byte[] IdentityEntropy =
        Encoding.UTF8.GetBytes("PhoneBackup/DesktopIdentity/v1");
}
