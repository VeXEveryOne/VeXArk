using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Runtime.CompilerServices;
using NSec.Cryptography;
using PhoneBackup.Core;

namespace PhoneBackup.Desktop;

public sealed class AgentClient : IAsyncDisposable
{
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

    public Task<JsonDocument> PackagesAsync(
        bool includeSystemApps = false,
        IReadOnlyCollection<string>? packageNames = null,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync("packages", new { includeSystemApps, packageNames }, cancellationToken);

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
        CancellationToken cancellationToken = default)
    {
        using var document = await PackagesAsync(includeSystemApps, packageNames, cancellationToken);
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
                0,
                item.GetProperty("uid").GetInt32(),
                item.GetProperty("isSystem").GetBoolean(),
                item.GetProperty("isEnabled").GetBoolean(),
                artifacts,
                dataPaths,
                runtimePermissions,
                item.TryGetProperty("batteryOptimizationExempt", out var battery) &&
                battery.GetBoolean()));
        }
        return result;
    }

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

    public async Task<bool> RequestRootAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync("root_request", cancellationToken: cancellationToken);
        return response.RootElement.TryGetProperty("granted", out var granted) && granted.GetBoolean();
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
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync(
            "restore_prepare", new { packageName }, cancellationToken);
        if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException($"Agent не смог подготовить Restore {packageName}.");
    }

    public async Task FinishPackageRestoreAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCommandAsync(
            "restore_finish", new { packageName }, cancellationToken);
        if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException($"Agent не смог завершить Restore {packageName}.");
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
                runtimePermissions = metadata.RuntimePermissions ?? []
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
        var request = CreateSignedRequest(command, payload);
        await WriteFrameAsync(TransferFrameType.Command, request, cancellationToken);
        var (type, response) = await ReadFrameAsync(cancellationToken);
        if (type is TransferFrameType.Error)
            throw new InvalidOperationException(Encoding.UTF8.GetString(response));
        if (type is not TransferFrameType.Response)
            throw new InvalidDataException($"Unexpected Agent frame: {type}");
        return JsonDocument.Parse(response);
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

    public async Task<IReadOnlyList<string>> RestoreSystemStateAsync(
        byte[] stateJson,
        CancellationToken cancellationToken = default)
    {
        using var state = JsonDocument.Parse(stateJson);
        using var response = await SendCommandAsync(
            "restore_system_state",
            new { state = state.RootElement.Clone() },
            cancellationToken);
        if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException("Agent не смог применить безопасные настройки Android.");
        return response.RootElement.GetProperty("failures").EnumerateArray()
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToList();
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
            root,
            relative = entry.RelativePath,
            kind = entry.Kind,
            mode = entry.Mode,
            uid = remappedUid,
            gid = remappedUid,
            modifiedUnixNanos = entry.ModifiedUnixNanos,
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
            modifiedUnixNanos = entry.ModifiedUnixNanos
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
            await output.CompleteAsync(cancellationToken);
        }
        catch when (producerError is not null)
        {
            // Preserve the repository/decryption error that stopped the producer.
        }
        if (producerError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producerError).Throw();
    }

    private byte[] CreateSignedRequest(string command, object? payload)
    {
        var requestId = Guid.NewGuid().ToString("D");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var payloadBytes = CanonicalPayload(payload);
        var payloadHash = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
        var canonical = Encoding.UTF8.GetBytes(
            $"{ProtocolConstants.ProtocolVersion}\n{requestId}\n{command}\n{timestamp}\n{nonce}\n{payloadHash}");
        var signature = Convert.ToBase64String(_identity.Sign(canonical));
        return JsonSerializer.SerializeToUtf8Bytes(new
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
        });
    }

    private static byte[] CanonicalPayload(object? payload)
    {
        var element = JsonSerializer.SerializeToElement(payload);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
            WriteCanonical(writer, element);
        // Android's org.json canonical string escaping includes forward slashes.
        var json = Encoding.UTF8.GetString(buffer.ToArray()).Replace("/", "\\/", StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(json);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private async Task WriteFrameAsync(
        TransferFrameType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var header = new byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
        await _stream.WriteAsync(header, cancellationToken);
        await _stream.WriteAsync(payload, cancellationToken);
    }

    private async Task<(TransferFrameType Type, byte[] Payload)> ReadFrameAsync(
        CancellationToken cancellationToken)
    {
        var header = new byte[5];
        await _stream.ReadExactlyAsync(header, cancellationToken);
        var type = (TransferFrameType)header[0];
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        var max = type == TransferFrameType.Data
            ? ProtocolConstants.DataFrameBytes
            : ProtocolConstants.MaxJsonFrameBytes;
        if (length < 0 || length > max)
            throw new InvalidDataException($"Agent frame length is invalid: {length}");
        var payload = new byte[length];
        await _stream.ReadExactlyAsync(payload, cancellationToken);
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
                    throw new IOException(Encoding.UTF8.GetString(payload));
                if (type != TransferFrameType.Data)
                    throw new InvalidDataException($"Unexpected root read frame: {type}");
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
            if (_completed) return;
            _completed = true;
            await owner.WriteFrameAsync(
                TransferFrameType.End, ReadOnlyMemory<byte>.Empty, cancellationToken);
            var (type, payload) = await owner.ReadFrameAsync(cancellationToken);
            if (type == TransferFrameType.Error)
                throw new IOException(Encoding.UTF8.GetString(payload));
            if (type != TransferFrameType.End)
                throw new InvalidDataException($"Unexpected root restore response: {type}");
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
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
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PhoneBackup");
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
