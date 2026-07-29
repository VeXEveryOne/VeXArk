using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhoneBackup.Core;

namespace PhoneBackup.Desktop;

public sealed class FastMediaClient : IAsyncDisposable
{
    private readonly AgentClient _control;
    private readonly FastMediaSession _session;
    private readonly byte[] _sessionKey;
    private readonly byte[] _sessionId;
    private readonly List<FastMediaWorker> _workers = [];
    private bool _disposed;

    private FastMediaClient(
        AgentClient control,
        FastMediaSession session,
        byte[] sessionKey)
    {
        _control = control;
        _session = session;
        _sessionKey = sessionKey;
        _sessionId = FastMediaProtocol.ParseSessionId(session.SessionId);
    }

    public IReadOnlyList<FastMediaWorker> Workers => _workers;
    public FastMediaSession Session => _session;

    public static async Task<FastMediaClient> ConnectAsync(
        AgentClient control,
        int workerCount,
        CancellationToken cancellationToken = default)
    {
        var key = RandomNumberGenerator.GetBytes(FastMediaProtocol.SessionKeyBytes);
        FastMediaSession? session = null;
        var client = default(FastMediaClient);
        try
        {
            session = await control.OpenFastMediaSessionAsync(
                key,
                Math.Clamp(workerCount, 1, 4),
                cancellationToken);
            client = new(control, session, key);
            var count = Math.Min(workerCount, session.MaxWorkers);
            for (var index = 0; index < count; index++)
            {
                client._workers.Add(await FastMediaWorker.ConnectAsync(
                    session,
                    key,
                    checked((byte)index),
                    cancellationToken));
            }
            return client;
        }
        catch
        {
            if (client is not null)
                await client.DisposeAsync();
            else
                CryptographicOperations.ZeroMemory(key);
            if (session is not null)
                await control.CloseFastMediaSessionAsync(
                    session.SessionId,
                    CancellationToken.None);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var worker in _workers)
            await worker.DisposeAsync();
        _workers.Clear();
        try
        {
            await _control.CloseFastMediaSessionAsync(
                _session.SessionId,
                CancellationToken.None);
        }
        catch
        {
            // The Agent also expires idle Fast LAN sessions after 30 seconds.
        }
        CryptographicOperations.ZeroMemory(_sessionKey);
    }
}

public sealed class FastMediaWorker : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly FastMediaRecordWriter _writer;
    private readonly FastMediaRecordReader _reader;

    private FastMediaWorker(
        TcpClient client,
        FastMediaRecordWriter writer,
        FastMediaRecordReader reader)
    {
        _client = client;
        _stream = client.GetStream();
        _writer = writer;
        _reader = reader;
    }

    public static async Task<FastMediaWorker> ConnectAsync(
        FastMediaSession session,
        byte[] sessionKey,
        byte workerId,
        CancellationToken cancellationToken)
    {
        var sessionId = FastMediaProtocol.ParseSessionId(session.SessionId);
        var client = new TcpClient
        {
            NoDelay = true,
            ReceiveBufferSize = 4 * 1024 * 1024,
            SendBufferSize = 256 * 1024
        };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await client.ConnectAsync(session.Host, session.Port, timeout.Token);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("Fast Wi-Fi connection timed out.", error);
            }
            var stream = client.GetStream();
            var handshake = FastMediaProtocol.BuildHandshake(
                sessionId,
                workerId,
                sessionKey);
            await stream.WriteAsync(handshake, cancellationToken);
            var requestKey = FastMediaProtocol.DeriveKey(
                sessionKey,
                sessionId,
                workerId,
                clientToServer: true);
            var responseKey = FastMediaProtocol.DeriveKey(
                sessionKey,
                sessionId,
                workerId,
                clientToServer: false);
            try
            {
                return new(
                    client,
                    new(stream, requestKey, sessionId, workerId, clientToServer: true),
                    new(stream, responseKey, sessionId, workerId, clientToServer: false));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(requestKey);
                CryptographicOperations.ZeroMemory(responseKey);
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<double> ProbeAsync(
        long length,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { length });
        await _writer.WriteAsync(FastMediaRecordType.Probe, payload, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var received = 0L;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (true)
        {
            using var record = await _reader.ReadAsync(cancellationToken);
            switch (record.Type)
            {
                case FastMediaRecordType.Data:
                    digest.AppendData(record.Payload.Span);
                    received += record.Length;
                    break;
                case FastMediaRecordType.End:
                    {
                        var completion = ParseCompletion(record.Payload);
                        ValidateCompletion(completion, received, digest.GetHashAndReset());
                        stopwatch.Stop();
                        return received / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                    }
                case FastMediaRecordType.Error:
                    throw new IOException(ParseError(record.Payload));
                default:
                    throw new InvalidDataException(
                        $"Unexpected Fast LAN probe record: {record.Type}");
            }
        }
    }

    public async Task<MediaReadCompletion> CopyFileAsync(
        string uri,
        long offset,
        long expectedSize,
        long expectedModifiedUnixNanos,
        Stream destination,
        Action<int>? onBytes,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            uri,
            offset,
            expectedSize,
            expectedModifiedUnixNanos
        });
        await _writer.WriteAsync(FastMediaRecordType.Open, payload, cancellationToken);
        var received = 0L;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (true)
        {
            using var record = await _reader.ReadAsync(cancellationToken);
            switch (record.Type)
            {
                case FastMediaRecordType.Data:
                    await destination.WriteAsync(record.Payload, cancellationToken);
                    digest.AppendData(record.Payload.Span);
                    received += record.Length;
                    onBytes?.Invoke(record.Length);
                    break;
                case FastMediaRecordType.End:
                    {
                        var completion = ParseCompletion(record.Payload);
                        ValidateCompletion(completion, received, digest.GetHashAndReset());
                        return completion;
                    }
                case FastMediaRecordType.Error:
                    throw new IOException(ParseError(record.Payload));
                default:
                    throw new InvalidDataException(
                        $"Unexpected Fast LAN media record: {record.Type}");
            }
        }
    }

    private static MediaReadCompletion ParseCompletion(ReadOnlyMemory<byte> payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        return new(
            root.GetProperty("sourceSize").GetInt64(),
            root.GetProperty("modifiedUnixNanos").GetInt64(),
            root.GetProperty("acceptedOffset").GetInt64(),
            root.GetProperty("transferredBytes").GetInt64(),
            root.GetProperty("sha256").GetString() ?? string.Empty);
    }

    private static string ParseError(ReadOnlyMemory<byte> payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("message", out var message)
            ? message.GetString() ?? "Fast LAN transfer failed."
            : "Fast LAN transfer failed.";
    }

    internal static void ValidateCompletion(
        MediaReadCompletion completion,
        long received,
        byte[] digest)
    {
        if (completion.TransferredBytes != received)
            throw new InvalidDataException("Fast media transferred byte count does not match.");
        var localHash = Convert.ToHexString(digest).ToLowerInvariant();
        if (!string.Equals(localHash, completion.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Fast media SHA-256 verification failed.");
    }

    public async ValueTask DisposeAsync()
    {
        _writer.Dispose();
        _reader.Dispose();
        await _stream.DisposeAsync();
        _client.Dispose();
    }
}
