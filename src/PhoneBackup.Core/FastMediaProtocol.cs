using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PhoneBackup.Core;

public enum FastMediaRecordType : byte
{
    Open = 1,
    Metadata = 2,
    Data = 3,
    End = 4,
    Error = 5,
    Probe = 6
}

public sealed class FastMediaRecord : IDisposable
{
    private byte[]? _buffer;

    internal FastMediaRecord(
        FastMediaRecordType type,
        byte[] buffer,
        int length)
    {
        Type = type;
        _buffer = buffer;
        Length = length;
    }

    public FastMediaRecordType Type { get; }
    public int Length { get; }
    public ReadOnlyMemory<byte> Payload =>
        (_buffer ?? throw new ObjectDisposedException(nameof(FastMediaRecord)))
        .AsMemory(0, Length);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is null) return;
        CryptographicOperations.ZeroMemory(buffer);
        ArrayPool<byte>.Shared.Return(buffer);
    }
}

public static class FastMediaProtocol
{
    public const byte Version = 1;
    public const int SessionIdBytes = 16;
    public const int SessionKeyBytes = 32;
    public const int HandshakeBytes = 4 + 1 + 1 + SessionIdBytes + 32;
    public const int RecordHeaderBytes = 1 + 4 + 8;
    public const int TagBytes = 16;
    public const int MaxPlaintextBytes = 1024 * 1024;
    public static ReadOnlySpan<byte> Magic => "VXF1"u8;

    public static byte[] ParseSessionId(string value)
    {
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length != SessionIdBytes)
            throw new InvalidDataException("Fast media session ID has an invalid length.");
        return bytes;
    }

    public static string FormatSessionId(ReadOnlySpan<byte> value)
    {
        if (value.Length != SessionIdBytes)
            throw new ArgumentException("Fast media session ID has an invalid length.", nameof(value));
        return Convert.ToBase64String(value);
    }

    public static byte[] DeriveKey(
        ReadOnlySpan<byte> sessionKey,
        ReadOnlySpan<byte> sessionId,
        byte workerId,
        bool clientToServer)
    {
        if (sessionKey.Length != SessionKeyBytes)
            throw new ArgumentException("Session key must contain 32 bytes.", nameof(sessionKey));
        if (sessionId.Length != SessionIdBytes)
            throw new ArgumentException("Session ID must contain 16 bytes.", nameof(sessionId));
        var info = Encoding.UTF8.GetBytes(
            $"vexark-fast-media-v1/{workerId}/{(clientToServer ? "c2s" : "s2c")}");
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sessionKey.ToArray(),
            SessionKeyBytes,
            sessionId.ToArray(),
            info);
    }

    public static byte[] BuildHandshake(
        ReadOnlySpan<byte> sessionId,
        byte workerId,
        ReadOnlySpan<byte> sessionKey)
    {
        if (sessionId.Length != SessionIdBytes)
            throw new ArgumentException("Session ID must contain 16 bytes.", nameof(sessionId));
        var result = new byte[HandshakeBytes];
        Magic.CopyTo(result);
        result[4] = Version;
        result[5] = workerId;
        sessionId.CopyTo(result.AsSpan(6, SessionIdBytes));
        using var hmac = new HMACSHA256(sessionKey.ToArray());
        var proof = hmac.ComputeHash(result, 0, 6 + SessionIdBytes);
        proof.CopyTo(result, 6 + SessionIdBytes);
        return result;
    }

    public static bool VerifyHandshake(
        ReadOnlySpan<byte> handshake,
        ReadOnlySpan<byte> expectedSessionId,
        ReadOnlySpan<byte> sessionKey,
        out byte workerId)
    {
        workerId = 0;
        if (handshake.Length != HandshakeBytes ||
            !handshake[..4].SequenceEqual(Magic) ||
            handshake[4] != Version ||
            !handshake.Slice(6, SessionIdBytes).SequenceEqual(expectedSessionId))
            return false;
        workerId = handshake[5];
        var expected = BuildHandshake(expectedSessionId, workerId, sessionKey);
        return CryptographicOperations.FixedTimeEquals(
            expected.AsSpan(6 + SessionIdBytes, 32),
            handshake.Slice(6 + SessionIdBytes, 32));
    }
}

public sealed class FastMediaRecordWriter(
    Stream stream,
    ReadOnlySpan<byte> key,
    ReadOnlySpan<byte> sessionId,
    byte workerId,
    bool clientToServer) : IDisposable
{
    private readonly AesGcm _aes = new(key.ToArray(), FastMediaProtocol.TagBytes);
    private readonly byte[] _sessionId = sessionId.ToArray();
    private ulong _counter;

    public async ValueTask WriteAsync(
        FastMediaRecordType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length > FastMediaProtocol.MaxPlaintextBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), "Fast media record is too large.");

        var header = new byte[FastMediaProtocol.RecordHeaderBytes];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(
            header.AsSpan(1, 4),
            checked(payload.Length + FastMediaProtocol.TagBytes));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(5, 8), _counter);

        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), _counter);
        var aad = BuildAad(
            _sessionId,
            workerId,
            clientToServer,
            type,
            _counter,
            payload.Length);
        var cipher = ArrayPool<byte>.Shared.Rent(Math.Max(1, payload.Length));
        var tag = ArrayPool<byte>.Shared.Rent(FastMediaProtocol.TagBytes);
        try
        {
            _aes.Encrypt(
                nonce,
                payload.Span,
                cipher.AsSpan(0, payload.Length),
                tag.AsSpan(0, FastMediaProtocol.TagBytes),
                aad);
            await stream.WriteAsync(header, cancellationToken);
            if (payload.Length > 0)
                await stream.WriteAsync(cipher.AsMemory(0, payload.Length), cancellationToken);
            await stream.WriteAsync(
                tag.AsMemory(0, FastMediaProtocol.TagBytes),
                cancellationToken);
            _counter = checked(_counter + 1);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipher);
            CryptographicOperations.ZeroMemory(tag);
            ArrayPool<byte>.Shared.Return(cipher);
            ArrayPool<byte>.Shared.Return(tag);
        }
    }

    public void Dispose() => _aes.Dispose();

    internal static byte[] BuildAad(
        ReadOnlySpan<byte> sessionId,
        byte workerId,
        bool clientToServer,
        FastMediaRecordType type,
        ulong counter,
        int plaintextLength)
    {
        var aad = new byte[1 + FastMediaProtocol.SessionIdBytes + 1 + 1 + 1 + 8 + 4];
        aad[0] = FastMediaProtocol.Version;
        sessionId.CopyTo(aad.AsSpan(1, FastMediaProtocol.SessionIdBytes));
        aad[17] = workerId;
        aad[18] = clientToServer ? (byte)1 : (byte)2;
        aad[19] = (byte)type;
        BinaryPrimitives.WriteUInt64BigEndian(aad.AsSpan(20, 8), counter);
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(28, 4), plaintextLength);
        return aad;
    }
}

public sealed class FastMediaRecordReader(
    Stream stream,
    ReadOnlySpan<byte> key,
    ReadOnlySpan<byte> sessionId,
    byte workerId,
    bool clientToServer) : IDisposable
{
    private readonly AesGcm _aes = new(key.ToArray(), FastMediaProtocol.TagBytes);
    private readonly byte[] _sessionId = sessionId.ToArray();
    private ulong _expectedCounter;

    public async ValueTask<FastMediaRecord> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var header = new byte[FastMediaProtocol.RecordHeaderBytes];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var type = (FastMediaRecordType)header[0];
        if (!Enum.IsDefined(type))
            throw new InvalidDataException($"Unknown fast media record type {(byte)type}.");
        var encryptedLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
        if (encryptedLength < FastMediaProtocol.TagBytes ||
            encryptedLength > FastMediaProtocol.MaxPlaintextBytes + FastMediaProtocol.TagBytes)
            throw new InvalidDataException("Fast media record length is invalid.");
        var counter = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(5, 8));
        if (counter != _expectedCounter)
            throw new InvalidDataException("Fast media record counter is invalid.");

        var plaintextLength = encryptedLength - FastMediaProtocol.TagBytes;
        var encrypted = ArrayPool<byte>.Shared.Rent(encryptedLength);
        var plaintext = ArrayPool<byte>.Shared.Rent(Math.Max(1, plaintextLength));
        try
        {
            await stream.ReadExactlyAsync(
                encrypted.AsMemory(0, encryptedLength),
                cancellationToken);
            var nonce = new byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
            var aad = FastMediaRecordWriter.BuildAad(
                _sessionId,
                workerId,
                clientToServer,
                type,
                counter,
                plaintextLength);
            _aes.Decrypt(
                nonce,
                encrypted.AsSpan(0, plaintextLength),
                encrypted.AsSpan(plaintextLength, FastMediaProtocol.TagBytes),
                plaintext.AsSpan(0, plaintextLength),
                aad);
            _expectedCounter = checked(_expectedCounter + 1);
            return new(type, plaintext, plaintextLength);
        }
        catch (CryptographicException error)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            ArrayPool<byte>.Shared.Return(plaintext);
            throw new InvalidDataException("Fast media record authentication failed.", error);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            ArrayPool<byte>.Shared.Return(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            ArrayPool<byte>.Shared.Return(encrypted);
        }
    }

    public void Dispose() => _aes.Dispose();
}
