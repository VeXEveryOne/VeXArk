using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Blake3;
using Konscious.Security.Cryptography;
using ZstdSharp;

namespace PhoneBackup.Core;

public sealed record RepositoryUnlock(byte[] MasterKey, string RepositoryId);
public sealed record RepositoryCreation(string RecoveryCode, RepositoryUnlock Unlock);
public sealed record RepositoryIntegrityReport(
    int SnapshotCount,
    int ReferencedObjectCount,
    int VerifiedObjectCount,
    IReadOnlyList<string> Errors);
public sealed record GarbageCollectionReport(int DeletedObjects, long FreedBytes);
public sealed record SnapshotBundleReport(int ObjectCount, long StoredBytes);

public sealed class EncryptedRepository
{
    private const int HeaderVersion = 2;
    private const int MasterKeyBytes = 64;
    private const int ArgonIterations = 3;
    private const int ArgonMemoryKiB = 64 * 1024;
    private static readonly int ArgonParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    private static readonly JsonSerializerOptions RepositoryJson =
        new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly byte[] _encryptionKey;
    private readonly byte[] _dedupKey;
    private readonly FastCdcChunker _chunker = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly ConcurrentQueue<string> _snapshotErrors = new();

    public IReadOnlyList<string> SnapshotErrors => _snapshotErrors.ToArray();

    private EncryptedRepository(string root, RepositoryUnlock unlock)
    {
        _root = root;
        _encryptionKey = unlock.MasterKey[..32];
        _dedupKey = unlock.MasterKey[32..];
    }

    public static async Task<RepositoryCreation> CreateAsync(
        string root,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (password.Length < 10)
            throw new ArgumentException("Repository password must contain at least 10 characters.", nameof(password));
        if (File.Exists(Path.Combine(root, "repository.json")))
            throw new InvalidOperationException("Repository already exists and will not be overwritten.");
        if (Directory.Exists(root))
        {
            var entries = Directory.EnumerateFileSystemEntries(root).ToList();
            var allowedEmptyDirectories = new HashSet<string>(
                ["objects", "snapshots", "tmp"],
                StringComparer.OrdinalIgnoreCase);
            if (entries.Any(x =>
                    File.Exists(x) ||
                    !allowedEmptyDirectories.Contains(Path.GetFileName(x)) ||
                    Directory.EnumerateFileSystemEntries(x).Any()))
                throw new InvalidOperationException("Repository directory must be empty.");
        }

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "objects"));
        Directory.CreateDirectory(Path.Combine(root, "snapshots"));
        Directory.CreateDirectory(Path.Combine(root, "tmp"));

        var masterKey = RandomNumberGenerator.GetBytes(MasterKeyBytes);
        var recoveryKey = RandomNumberGenerator.GetBytes(32);
        var passwordSalt = RandomNumberGenerator.GetBytes(16);
        var passwordKey = await DerivePasswordKeyAsync(
            password, passwordSalt, ArgonIterations, ArgonMemoryKiB, ArgonParallelism);
        var repositoryId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        var header = new RepositoryHeader(
            HeaderVersion,
            repositoryId,
            Convert.ToBase64String(passwordSalt),
            ArgonIterations,
            ArgonMemoryKiB,
            ArgonParallelism,
            "argon2id",
            Convert.ToBase64String(Encrypt(masterKey, passwordKey, Encoding.UTF8.GetBytes(repositoryId))),
            Convert.ToBase64String(Encrypt(masterKey, recoveryKey, Encoding.UTF8.GetBytes(repositoryId))),
            Convert.ToHexString(Hasher.Hash(recoveryKey).AsSpan()[..8]).ToLowerInvariant(),
            "blake3",
            "zstd-3");

        await WriteAtomicAsync(
            Path.Combine(root, "repository.json"),
            JsonSerializer.SerializeToUtf8Bytes(header, RepositoryJson),
            cancellationToken);

        var unlock = new RepositoryUnlock(masterKey, repositoryId);
        return new(RecoveryPhrase.Encode(recoveryKey), unlock);
    }

    public static async Task<EncryptedRepository> OpenWithPasswordAsync(
        string root,
        string password,
        CancellationToken cancellationToken = default)
    {
        var header = await ReadHeaderAsync(root, cancellationToken);
        var salt = Convert.FromBase64String(header.PasswordSalt);
        if (!string.Equals(header.KdfAlgorithm, "argon2id", StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported repository KDF: {header.KdfAlgorithm}");
        var key = await DerivePasswordKeyAsync(
            password, salt, header.KdfIterations, header.KdfMemoryKiB, header.KdfParallelism);
        var master = Decrypt(
            Convert.FromBase64String(header.PasswordWrappedMasterKey),
            key,
            Encoding.UTF8.GetBytes(header.RepositoryId));
        return new(root, new(master, header.RepositoryId));
    }

    public static async Task<EncryptedRepository> OpenWithRecoveryCodeAsync(
        string root,
        string recoveryCode,
        CancellationToken cancellationToken = default)
    {
        var header = await ReadHeaderAsync(root, cancellationToken);
        var key = RecoveryPhrase.Decode(recoveryCode);
        var master = Decrypt(
            Convert.FromBase64String(header.RecoveryWrappedMasterKey),
            key,
            Encoding.UTF8.GetBytes(header.RepositoryId));
        return new(root, new(master, header.RepositoryId));
    }

    public async Task<IReadOnlyList<ChunkReference>> PutStreamAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        var references = new List<ChunkReference>();
        await foreach (var chunk in _chunker.ChunkAsync(input, cancellationToken))
            references.Add(await PutChunkAsync(chunk, cancellationToken));
        return references;
    }

    public async ValueTask<IAsyncDisposable> AcquireOperationLeaseAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            var lockPath = Path.Combine(_root, ".vexark.lock");
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(
                        lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                        1, FileOptions.DeleteOnClose);
                    return new RepositoryLease(this, stream);
                }
                catch (IOException) when (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    public async Task RestoreStreamAsync(
        IEnumerable<ChunkReference> references,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        foreach (var reference in references)
        {
            var plain = await ReadChunkAsync(reference, cancellationToken).ConfigureAwait(false);
            if (plain.Length != reference.PlainLength)
                throw new InvalidDataException($"Object {reference.ObjectId} has an invalid plain length.");
            await output.WriteAsync(plain, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task VerifyReferencesAsync(
        IEnumerable<ChunkReference> references,
        CancellationToken cancellationToken = default)
    {
        foreach (var reference in references
                     .GroupBy(x => x.ObjectId, StringComparer.Ordinal)
                     .Select(x => x.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plain = await ReadChunkAsync(reference, cancellationToken).ConfigureAwait(false);
            if (plain.Length != reference.PlainLength)
                throw new InvalidDataException(
                    $"Object {reference.ObjectId} has an invalid plain length.");
            var digest = Hasher.Hash(plain).AsSpan().ToArray();
            var expected = Convert.ToHexString(KeyedHash(_dedupKey, digest)).ToLowerInvariant();
            if (!string.Equals(expected, reference.ObjectId, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Object {reference.ObjectId} has an invalid content hash.");
        }
    }

    public async Task VerifyFileAsync(
        IEnumerable<ChunkReference> references,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (expectedSize < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedSize));
        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(expectedSha256);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("Expected file SHA-256 is invalid.", error);
        }
        if (expectedHash.Length != 32)
            throw new InvalidDataException("Expected file SHA-256 must be 32 bytes.");

        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long actualSize = 0;
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plain = await ReadChunkAsync(reference, cancellationToken).ConfigureAwait(false);
            if (plain.Length != reference.PlainLength)
                throw new InvalidDataException(
                    $"Object {reference.ObjectId} has an invalid plain length.");

            var digest = Hasher.Hash(plain).AsSpan().ToArray();
            var expectedObjectId = Convert.ToHexString(KeyedHash(_dedupKey, digest)).ToLowerInvariant();
            if (!string.Equals(expectedObjectId, reference.ObjectId, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Object {reference.ObjectId} has an invalid content hash.");

            fileHash.AppendData(plain);
            actualSize = checked(actualSize + plain.Length);
        }

        var actualHash = fileHash.GetHashAndReset();
        if (actualSize != expectedSize)
            throw new InvalidDataException(
                $"File size mismatch. Expected {expectedSize}, got {actualSize}.");
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw new InvalidDataException("File SHA-256 does not match the manifest.");
    }

    public async Task CommitSnapshotAsync(
        SnapshotManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ValidateManifest(manifest);
        var plain = JsonSerializer.SerializeToUtf8Bytes(manifest, RepositoryJson);
        var idBytes = KeyedHash(_dedupKey, Encoding.UTF8.GetBytes(manifest.SnapshotId));
        var opaqueId = Convert.ToHexString(idBytes).ToLowerInvariant();
        var encrypted = Encrypt(plain, _encryptionKey, Encoding.UTF8.GetBytes(opaqueId));
        await WriteAtomicAsync(
            Path.Combine(_root, "snapshots", $"{opaqueId}.pbs"),
            encrypted,
            cancellationToken);
    }

    public async Task<IReadOnlyList<SnapshotManifest>> ListSnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        var manifests = new List<SnapshotManifest>();
        while (_snapshotErrors.TryDequeue(out _)) { }
        var directory = Path.Combine(_root, "snapshots");
        if (!Directory.Exists(directory)) return manifests;
        foreach (var path in Directory.EnumerateFiles(directory, "*.pbs"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var opaqueId = Path.GetFileNameWithoutExtension(path);
            try
            {
                var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
                var plain = Decrypt(encrypted, _encryptionKey, Encoding.UTF8.GetBytes(opaqueId));
                var manifest = JsonSerializer.Deserialize<SnapshotManifest>(plain, RepositoryJson)
                    ?? throw new InvalidDataException($"Snapshot {opaqueId} is invalid.");
                ValidateManifest(manifest);
                manifests.Add(NormalizeLegacyManifest(manifest));
            }
            catch (Exception error) when (
                error is IOException or CryptographicException or JsonException or InvalidDataException)
            {
                _snapshotErrors.Enqueue($"{opaqueId}: {error.Message}");
            }
        }
        return manifests.OrderByDescending(x => x.CreatedAtUtc).ToList();
    }

    public async Task<bool> DeleteSnapshotAsync(
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireOperationLeaseAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(snapshotId, "N", out _))
            throw new ArgumentException("Snapshot ID is invalid.", nameof(snapshotId));
        var idBytes = KeyedHash(_dedupKey, Encoding.UTF8.GetBytes(snapshotId));
        var opaqueId = Convert.ToHexString(idBytes).ToLowerInvariant();
        var path = Path.Combine(_root, "snapshots", $"{opaqueId}.pbs");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public async Task<RepositoryIntegrityReport> VerifyAsync(
        double sampleFraction = 1,
        CancellationToken cancellationToken = default)
    {
        if (sampleFraction is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(sampleFraction));
        var snapshots = await ListSnapshotsAsync(cancellationToken);
        var references = snapshots.SelectMany(x => x.Components)
            .SelectMany(x => x.Files)
            .SelectMany(x => x.Chunks ?? [])
            .GroupBy(x => x.ObjectId, StringComparer.Ordinal)
            .Select(x => x.First())
            .OrderBy(x => x.ObjectId, StringComparer.Ordinal)
            .ToList();
        var errors = new List<string>();
        var verified = 0;
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sampleFraction < 1 &&
                RandomNumberGenerator.GetInt32(1_000_000) >= sampleFraction * 1_000_000)
                continue;
            try
            {
                var plain = await ReadChunkAsync(reference, cancellationToken);
                var digest = Hasher.Hash(plain).AsSpan().ToArray();
                var expected = Convert.ToHexString(KeyedHash(_dedupKey, digest)).ToLowerInvariant();
                if (!string.Equals(expected, reference.ObjectId, StringComparison.Ordinal))
                    errors.Add($"{reference.ObjectId}: content hash mismatch");
                else
                    verified++;
            }
            catch (Exception error) when (error is IOException or CryptographicException or InvalidDataException)
            {
                errors.Add($"{reference.ObjectId}: {error.Message}");
            }
        }
        return new(snapshots.Count, references.Count, verified, errors);
    }

    public async Task<GarbageCollectionReport> GarbageCollectAsync(
        CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireOperationLeaseAsync(cancellationToken);
        var snapshots = await ListSnapshotsAsync(cancellationToken);
        var reachable = snapshots.SelectMany(x => x.Components)
            .SelectMany(x => x.Files)
            .SelectMany(x => x.Chunks ?? [])
            .Select(x => x.ObjectId)
            .ToHashSet(StringComparer.Ordinal);
        var deleted = 0;
        long freed = 0;
        var objects = Path.Combine(_root, "objects");
        if (!Directory.Exists(objects)) return new(0, 0);
        foreach (var path in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var objectId = Path.GetFileName(Path.GetDirectoryName(path)) + Path.GetFileName(path);
            if (reachable.Contains(objectId)) continue;
            var length = new FileInfo(path).Length;
            File.Delete(path);
            deleted++;
            freed += length;
        }
        return new(deleted, freed);
    }

    public async Task ChangePasswordAsync(
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireOperationLeaseAsync(cancellationToken);
        if (newPassword.Length < 10)
            throw new ArgumentException("Repository password must contain at least 10 characters.", nameof(newPassword));
        var old = await ReadHeaderAsync(_root, cancellationToken);
        var salt = RandomNumberGenerator.GetBytes(16);
        var wrappingKey = await DerivePasswordKeyAsync(
            newPassword, salt, ArgonIterations, ArgonMemoryKiB, ArgonParallelism);
        var master = _encryptionKey.Concat(_dedupKey).ToArray();
        var updated = old with
        {
            PasswordSalt = Convert.ToBase64String(salt),
            KdfIterations = ArgonIterations,
            KdfMemoryKiB = ArgonMemoryKiB,
            KdfParallelism = ArgonParallelism,
            KdfAlgorithm = "argon2id",
            PasswordWrappedMasterKey = Convert.ToBase64String(
                Encrypt(master, wrappingKey, Encoding.UTF8.GetBytes(old.RepositoryId)))
        };
        await WriteAtomicReplaceAsync(
            Path.Combine(_root, "repository.json"),
            JsonSerializer.SerializeToUtf8Bytes(updated, RepositoryJson),
            cancellationToken);
    }

    public async Task<SnapshotBundleReport> ExportSnapshotBundleAsync(
        SnapshotManifest manifest,
        string destination,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(manifest.SnapshotId, "N", out _))
            throw new ArgumentException("Snapshot ID is invalid.", nameof(manifest));
        var destinationPath = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporary = destinationPath + $".{Guid.NewGuid():N}.tmp";
        var objectIds = manifest.Components
            .SelectMany(x => x.Files)
            .SelectMany(x => x.Chunks ?? [])
            .Select(x => x.ObjectId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        var idBytes = KeyedHash(_dedupKey, Encoding.UTF8.GetBytes(manifest.SnapshotId));
        var opaqueId = Convert.ToHexString(idBytes).ToLowerInvariant();
        var snapshotPath = Path.Combine(_root, "snapshots", $"{opaqueId}.pbs");
        if (!File.Exists(snapshotPath))
            throw new FileNotFoundException("Encrypted snapshot manifest is missing.", snapshotPath);

        long storedBytes = 0;
        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             256 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await AddBundleEntryAsync(
                        archive,
                        "bundle.json",
                        new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            formatVersion = 1,
                            kind = "vexark-encrypted-snapshot"
                        }), writable: false),
                        cancellationToken);
                    storedBytes += await AddFileEntryAsync(
                        archive,
                        "repository.json",
                        Path.Combine(_root, "repository.json"),
                        cancellationToken);
                    storedBytes += await AddFileEntryAsync(
                        archive,
                        $"snapshots/{opaqueId}.pbs",
                        snapshotPath,
                        cancellationToken);
                    foreach (var objectId in objectIds)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var objectPath = ObjectPath(objectId);
                        storedBytes += await AddFileEntryAsync(
                            archive,
                            $"objects/{objectId[..2]}/{objectId[2..]}",
                            objectPath,
                            cancellationToken);
                    }
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destinationPath, overwrite: true);
            return new(objectIds.Count, storedBytes);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static async Task<SnapshotBundleReport> ImportSnapshotBundleAsync(
        string bundlePath,
        string destination,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.GetFullPath(bundlePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Файл локальной копии не найден.", sourcePath);
        var destinationPath = Path.GetFullPath(destination);
        if (Directory.Exists(destinationPath) &&
            Directory.EnumerateFileSystemEntries(destinationPath).Any())
            throw new InvalidOperationException(
                "Для импорта выберите новую или пустую папку.");

        var parent = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("Некорректная папка импорта.");
        Directory.CreateDirectory(parent);
        var temporaryRoot = Path.Combine(
            parent,
            $".phonebackup-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        var objectCount = 0;
        long storedBytes = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await using var input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                256 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                var limit = ValidateBundleEntry(name, entry.Length);
                if (!seen.Add(name))
                    throw new InvalidDataException($"Дублирующаяся запись архива: {name}");
                if (name == "bundle.json")
                {
                    await using var metadata = entry.Open();
                    using var document = await JsonDocument.ParseAsync(
                        metadata,
                        cancellationToken: cancellationToken);
                    var kind = document.RootElement.GetProperty("kind").GetString();
                    if (document.RootElement.GetProperty("formatVersion").GetInt32() != 1 ||
                        kind is not ("vexark-encrypted-snapshot" or
                            "phonebackup-encrypted-snapshot"))
                        throw new InvalidDataException("Неизвестный формат локальной копии.");
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(
                    temporaryRoot,
                    name.Replace('/', Path.DirectorySeparatorChar)));
                var rootPrefix = Path.GetFullPath(temporaryRoot)
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Путь архива выходит за папку импорта.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = entry.Open();
                await using var output = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    256 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(output, 256 * 1024, cancellationToken);
                await output.FlushAsync(cancellationToken);
                if (output.Length != entry.Length || output.Length > limit)
                    throw new InvalidDataException($"Некорректный размер записи: {name}");
                storedBytes += output.Length;
                if (name.StartsWith("objects/", StringComparison.Ordinal))
                    objectCount++;
            }

            if (!seen.Contains("bundle.json") ||
                !seen.Contains("repository.json") ||
                seen.Count(x => x.StartsWith("snapshots/", StringComparison.Ordinal)) != 1)
                throw new InvalidDataException("Локальная копия неполная.");
            if (Directory.Exists(destinationPath))
                Directory.Delete(destinationPath, recursive: false);
            Directory.Move(temporaryRoot, destinationPath);
            return new(objectCount, storedBytes);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static long ValidateBundleEntry(string name, long length)
    {
        if (length < 0 || name.StartsWith('/') || name.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException($"Небезопасная запись архива: {name}");
        if (name == "bundle.json")
            return RequireSize(name, length, 64 * 1024);
        if (name == "repository.json")
            return RequireSize(name, length, 1024 * 1024);
        if (System.Text.RegularExpressions.Regex.IsMatch(
                name,
                "^snapshots/[0-9a-f]{64}\\.pbs$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return RequireSize(name, length, 128L * 1024 * 1024);
        if (System.Text.RegularExpressions.Regex.IsMatch(
                name,
                "^objects/[0-9a-f]{2}/[0-9a-f]{62}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return RequireSize(name, length, 8L * 1024 * 1024);
        throw new InvalidDataException($"Неизвестная запись локальной копии: {name}");
    }

    private static long RequireSize(string name, long length, long maximum)
    {
        if (length > maximum)
            throw new InvalidDataException($"Запись архива слишком большая: {name}");
        return maximum;
    }

    private static async Task<long> AddFileEntryAsync(
        ZipArchive archive,
        string name,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Repository object is missing.", sourcePath);
        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await AddBundleEntryAsync(archive, name, input, cancellationToken);
        return input.Length;
    }

    private static async Task AddBundleEntryAsync(
        ZipArchive archive,
        string name,
        Stream input,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var output = entry.Open();
        await input.CopyToAsync(output, 256 * 1024, cancellationToken);
    }

    private async Task<ChunkReference> PutChunkAsync(byte[] plain, CancellationToken cancellationToken)
    {
        var digest = Hasher.Hash(plain).AsSpan().ToArray();
        var objectId = Convert.ToHexString(KeyedHash(_dedupKey, digest)).ToLowerInvariant();
        var path = ObjectPath(objectId);
        if (File.Exists(path))
            return new(objectId, plain.Length, checked((int)new FileInfo(path).Length));

        cancellationToken.ThrowIfCancellationRequested();
        var compressed = new Compressor(3).Wrap(plain).ToArray();
        var useCompressed = compressed.Length + 1 < plain.Length;
        var stored = new byte[1 + (useCompressed ? compressed.Length : plain.Length)];
        stored[0] = useCompressed ? (byte)1 : (byte)0;
        (useCompressed ? compressed : plain).CopyTo(stored, 1);

        var encrypted = Encrypt(stored, _encryptionKey, Encoding.UTF8.GetBytes(objectId));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteAtomicAsync(path, encrypted, cancellationToken);
        return new(objectId, plain.Length, encrypted.Length);
    }

    private string ObjectPath(string objectId) =>
        Path.Combine(_root, "objects", objectId[..2], objectId[2..]);

    private async Task<byte[]> ReadChunkAsync(
        ChunkReference reference,
        CancellationToken cancellationToken)
    {
        var path = ObjectPath(reference.ObjectId);
        var payload = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var stored = Decrypt(payload, _encryptionKey, Encoding.UTF8.GetBytes(reference.ObjectId));
        if (stored.Length == 0)
            throw new InvalidDataException($"Object {reference.ObjectId} has no compression marker.");
        return stored[0] switch
        {
            0 => stored[1..],
            1 => new Decompressor().Unwrap(stored.AsSpan(1)).ToArray(),
            _ => throw new InvalidDataException($"Object {reference.ObjectId} uses unknown compression.")
        };
    }

    private static async Task<byte[]> DerivePasswordKeyAsync(
        string password,
        byte[] salt,
        int iterations,
        int memoryKiB,
        int parallelism)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            Iterations = iterations,
            MemorySize = memoryKiB,
            DegreeOfParallelism = parallelism
        };
        return await argon.GetBytesAsync(32);
    }

    private static byte[] KeyedHash(byte[] key, ReadOnlySpan<byte> input)
    {
        using var hasher = Hasher.NewKeyed(key);
        hasher.Update(input);
        return hasher.Finalize().AsSpan().ToArray();
    }

    private static byte[] Encrypt(byte[] plain, byte[] key, byte[] associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, associatedData);
        var output = new byte[1 + nonce.Length + tag.Length + cipher.Length];
        output[0] = 1;
        nonce.CopyTo(output, 1);
        tag.CopyTo(output, 13);
        cipher.CopyTo(output, 29);
        return output;
    }

    private static byte[] Decrypt(byte[] payload, byte[] key, byte[] associatedData)
    {
        if (payload.Length < 29 || payload[0] != 1)
            throw new CryptographicException("Unsupported or damaged encrypted payload.");
        var plain = new byte[payload.Length - 29];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(payload.AsSpan(1, 12), payload.AsSpan(29), payload.AsSpan(13, 16), plain, associatedData);
        return plain;
    }

    private static async Task<RepositoryHeader> ReadHeaderAsync(string root, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path.Combine(root, "repository.json"));
        return await JsonSerializer.DeserializeAsync<RepositoryHeader>(
                   stream, RepositoryJson, cancellationToken)
               ?? throw new InvalidDataException("Repository header is missing or invalid.");
    }

    private static async Task WriteAtomicAsync(string destination, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            try
            {
                File.Move(temporary, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // A concurrent writer published the same content-addressed object first.
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task WriteAtomicReplaceAsync(
        string destination,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
        File.Move(temporary, destination, overwrite: true);
    }

    private static SnapshotManifest NormalizeLegacyManifest(SnapshotManifest manifest)
    {
        if (manifest.FormatVersion >= 2) return manifest;
        var components = manifest.Components.Select(component =>
        {
            var expected = component.Files.LongCount(x => x.Kind == "file");
            var captured = component.Files.LongCount(x => x.Kind == "file" && x.Chunks is not null);
            var complete = expected == captured && component.Files.All(x =>
                x.Kind is not ("special" or "symlink") &&
                (x.Kind != "file" || x.Chunks is not null));
            return component with
            {
                Status = complete ? ComponentStatus.Complete : ComponentStatus.Partial,
                RestoreClass = component.Kind switch
                {
                    "apk" or "app-data" or "shared-storage" or "personal-data" or "system-state" =>
                        RestoreClass.Portable,
                    "launcher-data" => RestoreClass.ExactRom,
                    _ => RestoreClass.ArchiveOnly
                },
                SchemaId = $"vexark.legacy.{component.Kind}",
                SchemaVersion = 1,
                Verification = new(expected, captured,
                    component.Files.Where(x => x.Kind == "file").Sum(x => x.Size),
                    component.Files.Where(x => x.Kind == "file" && x.Chunks is not null)
                        .Sum(x => x.Size),
                    complete)
            };
        }).ToList();
        var partial = components.Count(x => x.Status != ComponentStatus.Complete);
        return manifest with
        {
            Components = components,
            Summary = new(
                partial == 0 ? ComponentStatus.Complete : ComponentStatus.Partial,
                components.Count - partial,
                partial,
                0,
                0,
                ["Legacy v1 completeness was inferred; app-data restore uses overlay staging."])
        };
    }

    internal static void ValidateManifest(SnapshotManifest manifest)
    {
        if (manifest.FormatVersion < 1 || manifest.FormatVersion > 2)
            throw new InvalidDataException($"Unsupported snapshot format {manifest.FormatVersion}.");
        if (!Guid.TryParseExact(manifest.SnapshotId, "N", out _))
            throw new InvalidDataException("Snapshot ID must be a 32-character GUID.");
        if (manifest.Purpose is not ("manual" or "safety"))
            throw new InvalidDataException("Snapshot purpose is invalid.");
        if (manifest.Components.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() !=
            manifest.Components.Count)
            throw new InvalidDataException("Snapshot contains duplicate component IDs.");
        if (manifest.FormatVersion < 2) return;
        if (manifest.Summary is null)
            throw new InvalidDataException("Manifest v2 requires a summary.");

        foreach (var component in manifest.Components)
        {
            if (string.IsNullOrWhiteSpace(component.Id) || string.IsNullOrWhiteSpace(component.Kind))
                throw new InvalidDataException("Component ID and kind are required.");
            if (component.Status == ComponentStatus.Unknown)
                throw new InvalidDataException($"Component {component.Id} has unknown status.");
            if (string.IsNullOrWhiteSpace(component.SchemaId) || component.SchemaVersion < 1)
                throw new InvalidDataException($"Component {component.Id} has no schema identity.");
            if (component.PlainBytes < 0 || component.StoredBytes < 0)
                throw new InvalidDataException($"Component {component.Id} contains negative sizes.");
            if (component.Files.Select(x => x.RelativePath)
                    .Distinct(StringComparer.Ordinal).Count() != component.Files.Count)
                throw new InvalidDataException($"Component {component.Id} contains duplicate paths.");

            foreach (var file in component.Files)
            {
                if (!RestorePathPolicy.IsSafeRelativePath(file.RelativePath))
                    throw new InvalidDataException(
                        $"Component {component.Id} contains unsafe path {file.RelativePath}.");
                if (file.Size < 0 || file.Kind is not ("file" or "directory" or "symlink" or "special"))
                    throw new InvalidDataException(
                        $"Component {component.Id} contains invalid file metadata.");
                if (file.Kind != "file") continue;
                if (file.Chunks is null) {
                    if (component.Status == ComponentStatus.Complete)
                        throw new InvalidDataException(
                            $"Complete component {component.Id} has a file without chunks.");
                    continue;
                }
                if (file.Chunks.Sum(x => (long)x.PlainLength) != file.Size)
                    throw new InvalidDataException(
                        $"Chunk lengths do not match {component.Id}/{file.RelativePath}.");
                if (component.Kind is "app-data" or "launcher-data" or "shared-storage" or
                        "full" or "wallpaper" &&
                    (file.ContentHash is not { Length: 64 } hash ||
                     hash.Any(x => !Uri.IsHexDigit(x))))
                    throw new InvalidDataException(
                        $"Component {component.Id} has no whole-file BLAKE3 for {file.RelativePath}.");
                foreach (var chunk in file.Chunks)
                {
                    if (chunk.ObjectId.Length != 64 ||
                        chunk.ObjectId.Any(x => !Uri.IsHexDigit(x)) ||
                        chunk.PlainLength <= 0 || chunk.StoredLength <= 0)
                        throw new InvalidDataException(
                            $"Component {component.Id} contains an invalid chunk reference.");
                }
            }

            if (component.Status == ComponentStatus.Complete && component.Files.Any(x =>
                    x.Kind is "special" or "symlink"))
                throw new InvalidDataException(
                    $"Complete component {component.Id} contains a non-restorable entry.");
            if (component.Status == ComponentStatus.Complete &&
                component.Verification is not { HashesVerified: true })
                throw new InvalidDataException(
                    $"Complete component {component.Id} has no successful verification summary.");
            var capturedFiles = component.Files.LongCount(x =>
                x.Kind == "file" && x.Chunks is not null);
            var capturedBytes = component.Files.Where(x =>
                    x.Kind == "file" && x.Chunks is not null)
                .Sum(x => x.Size);
            var storedBytes = component.Files.SelectMany(x => x.Chunks ?? [])
                .Sum(x => (long)x.StoredLength);
            if (component.PlainBytes != capturedBytes || component.StoredBytes != storedBytes)
                throw new InvalidDataException(
                    $"Component {component.Id} byte totals do not match its files.");
            if (component.Verification is { } verification &&
                (verification.CapturedFiles != capturedFiles ||
                 verification.CapturedBytes != capturedBytes ||
                 verification.ExpectedFiles < verification.CapturedFiles ||
                 verification.ExpectedBytes < verification.CapturedBytes))
                throw new InvalidDataException(
                    $"Component {component.Id} verification totals are inconsistent.");
        }

        var complete = manifest.Components.Count(x => x.Status == ComponentStatus.Complete);
        var partial = manifest.Components.Count(x =>
            x.Status is ComponentStatus.Partial or ComponentStatus.Unknown);
        var failed = manifest.Components.Count(x => x.Status == ComponentStatus.Failed);
        var skipped = manifest.Components.Count(x => x.Status == ComponentStatus.Skipped);
        if (manifest.Summary.CompleteComponents != complete ||
            manifest.Summary.PartialComponents != partial ||
            manifest.Summary.FailedComponents != failed ||
            manifest.Summary.SkippedComponents != skipped)
            throw new InvalidDataException("Snapshot summary counts do not match components.");
        var meaningfulSkipped = manifest.Components.Count(x =>
            x.Status == ComponentStatus.Skipped &&
            x.Failures?.All(failure =>
                failure.ErrorCode == "not_independently_available") != true);
        var expectedStatus = failed > 0 && complete == 0
            ? ComponentStatus.Failed
            : partial > 0 || failed > 0 || meaningfulSkipped > 0
                ? ComponentStatus.Partial
                : ComponentStatus.Complete;
        if (manifest.Summary.Status != expectedStatus)
            throw new InvalidDataException("Snapshot summary status does not match components.");
    }

    private sealed class RepositoryLease(
        EncryptedRepository owner,
        FileStream stream) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            stream.Dispose();
            owner._operationGate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
