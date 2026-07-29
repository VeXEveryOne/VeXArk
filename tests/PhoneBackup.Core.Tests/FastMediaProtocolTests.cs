using System.Security.Cryptography;

namespace PhoneBackup.Core.Tests;

public sealed class FastMediaProtocolTests
{
    [Fact]
    public void HandshakeAuthenticatesSessionAndWorker()
    {
        var sessionId = RandomNumberGenerator.GetBytes(FastMediaProtocol.SessionIdBytes);
        var key = RandomNumberGenerator.GetBytes(FastMediaProtocol.SessionKeyBytes);
        var handshake = FastMediaProtocol.BuildHandshake(sessionId, 3, key);

        Assert.True(FastMediaProtocol.VerifyHandshake(
            handshake,
            sessionId,
            key,
            out var worker));
        Assert.Equal(3, worker);

        handshake[^1] ^= 0x40;
        Assert.False(FastMediaProtocol.VerifyHandshake(
            handshake,
            sessionId,
            key,
            out _));
    }

    [Fact]
    public async Task RecordRoundTripAuthenticatesPayload()
    {
        var sessionId = RandomNumberGenerator.GetBytes(FastMediaProtocol.SessionIdBytes);
        var sessionKey = RandomNumberGenerator.GetBytes(FastMediaProtocol.SessionKeyBytes);
        var key = FastMediaProtocol.DeriveKey(sessionKey, sessionId, 1, true);
        var payload = RandomNumberGenerator.GetBytes(512 * 1024);
        await using var stream = new MemoryStream();
        var writer = new FastMediaRecordWriter(stream, key, sessionId, 1, true);
        await writer.WriteAsync(FastMediaRecordType.Data, payload);

        stream.Position = 0;
        var reader = new FastMediaRecordReader(stream, key, sessionId, 1, true);
        using var record = await reader.ReadAsync();

        Assert.Equal(FastMediaRecordType.Data, record.Type);
        Assert.Equal(payload, record.Payload.ToArray());
    }

    [Fact]
    public async Task RecordRejectsTamperingAndReplayCounter()
    {
        var sessionId = RandomNumberGenerator.GetBytes(FastMediaProtocol.SessionIdBytes);
        var sessionKey = RandomNumberGenerator.GetBytes(FastMediaProtocol.SessionKeyBytes);
        var key = FastMediaProtocol.DeriveKey(sessionKey, sessionId, 0, false);
        await using var stream = new MemoryStream();
        var writer = new FastMediaRecordWriter(stream, key, sessionId, 0, false);
        await writer.WriteAsync(FastMediaRecordType.Data, new byte[] { 1, 2, 3 });
        await writer.WriteAsync(FastMediaRecordType.End, new byte[] { 4, 5 });

        var bytes = stream.ToArray();
        var secondRecord = FastMediaProtocol.RecordHeaderBytes + 3 + FastMediaProtocol.TagBytes;
        Array.Clear(bytes, secondRecord + 5, 8);
        await using var replay = new MemoryStream(bytes);
        var reader = new FastMediaRecordReader(replay, key, sessionId, 0, false);
        using var first = await reader.ReadAsync();
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => _ = await reader.ReadAsync());

        bytes = stream.ToArray();
        bytes[FastMediaProtocol.RecordHeaderBytes] ^= 0x01;
        await using var tampered = new MemoryStream(bytes);
        reader = new FastMediaRecordReader(tampered, key, sessionId, 0, false);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => _ = await reader.ReadAsync());
    }

    [Fact]
    public void KeysAreSeparatedByWorkerAndDirection()
    {
        var sessionId = RandomNumberGenerator.GetBytes(FastMediaProtocol.SessionIdBytes);
        var sessionKey = RandomNumberGenerator.GetBytes(FastMediaProtocol.SessionKeyBytes);
        var worker0Request = FastMediaProtocol.DeriveKey(sessionKey, sessionId, 0, true);
        var worker0Response = FastMediaProtocol.DeriveKey(sessionKey, sessionId, 0, false);
        var worker1Request = FastMediaProtocol.DeriveKey(sessionKey, sessionId, 1, true);

        Assert.NotEqual(worker0Request, worker0Response);
        Assert.NotEqual(worker0Request, worker1Request);
        Assert.NotEqual(worker0Response, worker1Request);
    }
}
