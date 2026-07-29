using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using PhoneBackup.Core;

namespace PhoneBackup.Desktop;

public sealed record MediaExportReport(
    int CopiedFiles,
    int SkippedFiles,
    int FailedFiles,
    long CopiedBytes,
    long TotalBytes,
    IReadOnlyList<string> Errors,
    MediaTransportMode Transport = MediaTransportMode.Adb,
    double AverageBytesPerSecond = 0,
    double DiskBytesPerSecond = 0,
    double AdbProbeBytesPerSecond = 0,
    double FastLanProbeBytesPerSecond = 0,
    int ResumedFiles = 0,
    long ResumedBytes = 0,
    int WorkerCount = 1);

public sealed class MediaExportCoordinator
{
    private const long ProbeBytes = 16L * 1024 * 1024;
    private const long DiskProbeBytes = 64L * 1024 * 1024;
    private const int IoBufferBytes = 1024 * 1024;

    public async Task<MediaExportReport> ExportAsync(
        AgentClient agent,
        string destination,
        MediaTransferOptions? options = null,
        IProgress<TransferProgress>? progress = null,
        IProgress<MediaTransferMetrics>? metrics = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new(MediaTransportMode.Auto);
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("Выберите папку для фото и видео.", nameof(destination));

        var destinationRoot = Path.GetFullPath(destination);
        Directory.CreateDirectory(destinationRoot);
        var capability = await agent.GetMediaCapabilityAsync(cancellationToken);
        if (!capability.Images && !capability.Videos)
            throw new UnauthorizedAccessException(
                "На телефоне не разрешён доступ к фото и видео. " +
                "Откройте VeXArk Agent и нажмите «Разрешить фото и видео».");

        progress?.Report(new("media-scan", "Чтение каталога MediaStore", 0, 0));
        var entries = new List<FileEntry>();
        await foreach (var entry in agent.ScanMediaAsync(cancellationToken))
        {
            if (entry.Kind == "file" &&
                !string.IsNullOrWhiteSpace(entry.LinkTarget) &&
                RestorePathPolicy.IsSafeRelativePath(entry.RelativePath))
                entries.Add(entry);
        }

        var capabilities = await agent.GetCapabilitiesAsync(cancellationToken);
        if (!capabilities.Contains("media-export-v2"))
            return await ExportLegacyAsync(
                agent,
                destinationRoot,
                entries,
                progress,
                cancellationToken);

        var prepared = PrepareWork(destinationRoot, entries);
        var work = prepared.Work
            .OrderByDescending(x => x.Entry.Size - x.Offset)
            .ToList();
        if (work.Count == 0)
        {
            return new(
                0,
                prepared.Skipped,
                0,
                0,
                entries.Sum(x => x.Size),
                [],
                options.Transport == MediaTransportMode.FastLan
                    ? MediaTransportMode.FastLan
                    : MediaTransportMode.Adb,
                ResumedFiles: prepared.ResumedFiles,
                ResumedBytes: prepared.ResumedBytes);
        }

        progress?.Report(new("media-probe", "Проверка скорости диска", 0, 3));
        var diskSpeed = await ProbeDiskAsync(destinationRoot, cancellationToken);

        progress?.Report(new("media-probe", "Проверка скорости ADB", 1, 3));
        var adbProbe = await ProbeAdbAsync(agent, cancellationToken);
        FastMediaClient? fastClient = null;
        var fastProbe = 0d;
        if (options.Transport != MediaTransportMode.Adb &&
            capabilities.Contains("fast-lan-aead-v1"))
        {
            progress?.Report(new("media-probe", "Проверка Fast Wi-Fi", 2, 3));
            try
            {
                fastClient = await FastMediaClient.ConnectAsync(
                    agent,
                    Math.Min(options.FastLanWorkers, 4),
                    cancellationToken);
                fastProbe = await fastClient.Workers[0].ProbeAsync(
                    ProbeBytes,
                    cancellationToken);
            }
            catch (Exception error) when (
                options.Transport == MediaTransportMode.Auto &&
                error is IOException or SocketException or UnauthorizedAccessException)
            {
                if (fastClient is not null)
                    await fastClient.DisposeAsync();
                fastClient = null;
                progress?.Report(new(
                    "media-fallback",
                    $"Fast Wi-Fi недоступен: {error.Message}. Используется ADB.",
                    0,
                    0));
            }
        }

        var useFastLan = MediaTransferPolicy.PreferFastLan(
            options.Transport,
            fastClient is not null,
            adbProbe,
            fastProbe);
        if (!useFastLan && fastClient is not null)
        {
            await fastClient.DisposeAsync();
            fastClient = null;
        }
        var transport = useFastLan ? MediaTransportMode.FastLan : MediaTransportMode.Adb;
        var workerCount = MediaTransferPolicy.SelectWorkerCount(
            transport,
            diskSpeed,
            options,
            fastClient?.Workers.Count ?? 0);

        try
        {
            return await CopyParallelAsync(
                agent,
                fastClient,
                transport,
                workerCount,
                work,
                entries.Sum(x => x.Size),
                prepared,
                diskSpeed,
                adbProbe,
                fastProbe,
                progress,
                metrics,
                cancellationToken);
        }
        finally
        {
            if (fastClient is not null)
                await fastClient.DisposeAsync();
            CleanupStaleResumeFiles(destinationRoot);
        }
    }

    private static async Task<MediaExportReport> CopyParallelAsync(
        AgentClient control,
        FastMediaClient? fastClient,
        MediaTransportMode transport,
        int workerCount,
        IReadOnlyList<MediaWorkItem> work,
        long totalSourceBytes,
        PreparedWork prepared,
        double diskSpeed,
        double adbProbe,
        double fastProbe,
        IProgress<TransferProgress>? progress,
        IProgress<MediaTransferMetrics>? metrics,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<MediaWorkItem>(new BoundedChannelOptions(workerCount * 2)
        {
            SingleWriter = true,
            SingleReader = workerCount == 1,
            FullMode = BoundedChannelFullMode.Wait
        });
        var errors = new ConcurrentQueue<string>();
        var copied = 0;
        var failed = 0;
        var completedFiles = 0L;
        var transferredBytes = 0L;
        var activeFiles = 0;
        var copyBytesTotal = work.Sum(x => x.Entry.Size);
        var stopwatch = Stopwatch.StartNew();
        var metricsGate = new object();
        var lastMetricsAt = TimeSpan.Zero;

        void OnBytes(int count)
        {
            var completed = Interlocked.Add(ref transferredBytes, count);
            lock (metricsGate)
            {
                if (stopwatch.Elapsed - lastMetricsAt < TimeSpan.FromMilliseconds(250))
                    return;
                lastMetricsAt = stopwatch.Elapsed;
                var speed = completed / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                var logicalCompleted = Math.Min(copyBytesTotal, prepared.ResumedBytes + completed);
                var remaining = Math.Max(0, copyBytesTotal - logicalCompleted);
                metrics?.Report(new(
                    transport,
                    speed,
                    diskSpeed,
                    logicalCompleted,
                    copyBytesTotal,
                    prepared.ResumedBytes,
                    Volatile.Read(ref activeFiles),
                    speed > 0 ? TimeSpan.FromSeconds(remaining / speed) : null));
            }
        }

        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var item in work)
                    await channel.Writer.WriteAsync(item, cancellationToken);
                channel.Writer.TryComplete();
            }
            catch (Exception error)
            {
                channel.Writer.TryComplete(error);
                throw;
            }
        }, cancellationToken);

        var workers = Enumerable.Range(0, workerCount).Select(async workerIndex =>
        {
            await using var adbWorker = await control.ConnectSiblingAsync(cancellationToken);
            var fastWorker = fastClient?.Workers[workerIndex];
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Increment(ref activeFiles);
                var current = Interlocked.Increment(ref completedFiles);
                progress?.Report(new(
                    "media-copy",
                    item.Relative,
                    current - 1,
                    work.Count));
                try
                {
                    try
                    {
                        await CopyOneAsync(
                            adbWorker,
                            fastWorker,
                            item,
                            OnBytes,
                            cancellationToken);
                    }
                    catch (Exception error) when (
                        fastWorker is not null &&
                        error is IOException or SocketException or InvalidDataException)
                    {
                        fastWorker = null;
                        progress?.Report(new(
                            "media-fallback",
                            $"{item.Relative}: Fast Wi-Fi прерван, продолжение через ADB",
                            current - 1,
                            work.Count));
                        var resumed = item with { Offset = new FileInfo(item.PartPath).Length };
                        await CopyOneAsync(
                            adbWorker,
                            fastWorker: null,
                            resumed,
                            OnBytes,
                            cancellationToken);
                    }
                    Interlocked.Increment(ref copied);
                }
                catch (OperationCanceledException)
                {
                    PersistCurrentOffset(item);
                    throw;
                }
                catch (Exception error) when (
                    error is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    PersistCurrentOffset(item);
                    Interlocked.Increment(ref failed);
                    if (errors.Count < 50)
                        errors.Enqueue($"{item.Relative}: {error.Message}");
                }
                finally
                {
                    Interlocked.Decrement(ref activeFiles);
                }
            }
        }).ToArray();

        await producer;
        await Task.WhenAll(workers);
        stopwatch.Stop();
        var copiedBytes = work
            .Where(x => File.Exists(x.TargetPath) && IsUnchanged(x.TargetPath, x.Entry))
            .Sum(x => x.Entry.Size);
        var speed = transferredBytes / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        metrics?.Report(new(
            transport,
            speed,
            diskSpeed,
            copyBytesTotal,
            copyBytesTotal,
            prepared.ResumedBytes,
            0,
            TimeSpan.Zero));
        progress?.Report(new("media-complete", "Копирование завершено", work.Count, work.Count));
        return new(
            copied,
            prepared.Skipped,
            failed,
            copiedBytes,
            totalSourceBytes,
            errors.ToList(),
            transport,
            speed,
            diskSpeed,
            adbProbe,
            fastProbe,
            prepared.ResumedFiles,
            prepared.ResumedBytes,
            workerCount);
    }

    private static async Task CopyOneAsync(
        AgentClient adbWorker,
        FastMediaWorker? fastWorker,
        MediaWorkItem item,
        Action<int> onBytes,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(item.TargetPath)
            ?? throw new InvalidDataException("Некорректный путь назначения.");
        Directory.CreateDirectory(parent);
        WriteResumeState(item, item.Offset);

        var mode = File.Exists(item.PartPath) ? FileMode.Open : FileMode.CreateNew;
        var streamOptions = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            BufferSize = IoBufferBytes,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        if (mode == FileMode.CreateNew)
            streamOptions.PreallocationSize = item.Entry.Size;
        await using (var output = new FileStream(item.PartPath, streamOptions))
        {
            if (output.Length != item.Offset)
                output.SetLength(item.Offset);
            output.Position = item.Offset;
            MediaReadCompletion completion;
            if (fastWorker is not null)
            {
                completion = await fastWorker.CopyFileAsync(
                    item.Entry.LinkTarget!,
                    item.Offset,
                    item.Entry.Size,
                    item.Entry.ModifiedUnixNanos,
                    output,
                    onBytes,
                    cancellationToken);
            }
            else
            {
                completion = await CopyWithAdbAsync(
                    adbWorker,
                    item,
                    output,
                    onBytes,
                    cancellationToken);
            }
            if (completion.AcceptedOffset != item.Offset ||
                completion.SourceSize != item.Entry.Size ||
                completion.ModifiedUnixNanos != item.Entry.ModifiedUnixNanos)
                throw new InvalidDataException("MediaStore metadata changed during copying.");
            await output.FlushAsync(cancellationToken);
        }

        if (new FileInfo(item.PartPath).Length != item.Entry.Size)
            throw new InvalidDataException("Размер полученного файла не совпал.");
        File.Move(item.PartPath, item.TargetPath, overwrite: true);
        ApplyTimestamp(item.TargetPath, item.Entry.ModifiedUnixNanos);
        if (File.Exists(item.SidecarPath))
            File.Delete(item.SidecarPath);
    }

    private static async Task<MediaReadCompletion> CopyWithAdbAsync(
        AgentClient adbWorker,
        MediaWorkItem item,
        Stream output,
        Action<int> onBytes,
        CancellationToken cancellationToken)
    {
        await using var source = await adbWorker.OpenMediaFileV2Async(
            item.Entry.LinkTarget!,
            item.Offset,
            item.Entry.Size,
            item.Entry.ModifiedUnixNanos,
            cancellationToken);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(IoBufferBytes);
        var received = 0L;
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(
                    buffer.AsMemory(0, IoBufferBytes),
                    cancellationToken);
                if (count == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                digest.AppendData(buffer, 0, count);
                received += count;
                onBytes(count);
            }
            var completion = await source.Completion.WaitAsync(cancellationToken);
            FastMediaWorker.ValidateCompletion(
                completion,
                received,
                digest.GetHashAndReset());
            return completion;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<double> ProbeAdbAsync(
        AgentClient control,
        CancellationToken cancellationToken)
    {
        await using var worker = await control.ConnectSiblingAsync(cancellationToken);
        await using var source = await worker.OpenMediaProbeAsync(ProbeBytes, cancellationToken);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(IoBufferBytes);
        var received = 0L;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(
                    buffer.AsMemory(0, IoBufferBytes),
                    cancellationToken);
                if (count == 0) break;
                digest.AppendData(buffer, 0, count);
                received += count;
            }
            var completion = await source.Completion.WaitAsync(cancellationToken);
            FastMediaWorker.ValidateCompletion(
                completion,
                received,
                digest.GetHashAndReset());
            stopwatch.Stop();
            return received / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<double> ProbeDiskAsync(
        string destination,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(destination, $".vexark-disk-probe-{Guid.NewGuid():N}.tmp");
        var buffer = ArrayPool<byte>.Shared.Rent(IoBufferBytes);
        RandomNumberGenerator.Fill(buffer.AsSpan(0, IoBufferBytes));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var output = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = IoBufferBytes,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    PreallocationSize = DiskProbeBytes
                });
            var written = 0L;
            while (written < DiskProbeBytes)
            {
                await output.WriteAsync(
                    buffer.AsMemory(0, IoBufferBytes),
                    cancellationToken);
                written += IoBufferBytes;
            }
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
            stopwatch.Stop();
            return written / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static PreparedWork PrepareWork(
        string destinationRoot,
        IReadOnlyList<FileEntry> entries)
    {
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var work = new List<MediaWorkItem>();
        var skipped = 0;
        var resumedFiles = 0;
        var resumedBytes = 0L;
        foreach (var entry in entries)
        {
            var relative = MakeUniqueRelativePath(
                MakeWindowsSafeRelativePath(entry.RelativePath),
                entry.LinkTarget!,
                usedPaths);
            var target = RestorePathPolicy.ResolveUnderRoot(destinationRoot, relative);
            if (IsUnchanged(target, entry))
            {
                skipped++;
                continue;
            }
            var parent = Path.GetDirectoryName(target)
                ?? throw new InvalidDataException("Некорректный путь назначения.");
            var part = Path.Combine(parent, $".{Path.GetFileName(target)}.vexark.part");
            var sidecar = part + ".json";
            var offset = LoadResumeOffset(entry, part, sidecar);
            if (offset > 0)
            {
                resumedFiles++;
                resumedBytes += offset;
            }
            work.Add(new(entry, relative, target, part, sidecar, offset));
        }
        return new(work, skipped, resumedFiles, resumedBytes);
    }

    private static long LoadResumeOffset(
        FileEntry entry,
        string partPath,
        string sidecarPath)
    {
        if (!File.Exists(partPath) || !File.Exists(sidecarPath))
        {
            DeleteResumeFiles(partPath, sidecarPath);
            return 0;
        }
        try
        {
            var state = JsonSerializer.Deserialize<MediaResumeMetadata>(
                File.ReadAllBytes(sidecarPath))
                ?? throw new InvalidDataException("Resume metadata is missing.");
            var length = new FileInfo(partPath).Length;
            if (!state.Matches(entry, length))
                throw new InvalidDataException("Stale resume metadata.");
            return length;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            DeleteResumeFiles(partPath, sidecarPath);
            return 0;
        }
    }

    private static void WriteResumeState(MediaWorkItem item, long offset)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(item.SidecarPath)!);
        var temporary = item.SidecarPath + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(
            temporary,
            JsonSerializer.SerializeToUtf8Bytes(new MediaResumeMetadata(
                1,
                item.Entry.LinkTarget!,
                item.Entry.Size,
                item.Entry.ModifiedUnixNanos,
                offset,
                DateTimeOffset.UtcNow)));
        File.Move(temporary, item.SidecarPath, overwrite: true);
    }

    private static void PersistCurrentOffset(MediaWorkItem item)
    {
        if (File.Exists(item.PartPath))
            WriteResumeState(item, new FileInfo(item.PartPath).Length);
    }

    private static void DeleteResumeFiles(string partPath, string sidecarPath)
    {
        if (File.Exists(partPath))
            File.Delete(partPath);
        if (File.Exists(sidecarPath))
            File.Delete(sidecarPath);
    }

    private static void CleanupStaleResumeFiles(string destinationRoot)
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        try
        {
            foreach (var sidecar in Directory.EnumerateFiles(
                         destinationRoot,
                         ".*.vexark.part.json",
                         SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTimeUtc(sidecar) >= cutoff)
                    continue;
                var part = sidecar[..^".json".Length];
                DeleteResumeFiles(part, sidecar);
            }
        }
        catch (IOException)
        {
            // Cleanup must never invalidate an otherwise complete export.
        }
        catch (UnauthorizedAccessException)
        {
            // Some destination subdirectories may not be accessible.
        }
    }

    private static async Task<MediaExportReport> ExportLegacyAsync(
        AgentClient agent,
        string destinationRoot,
        IReadOnlyList<FileEntry> entries,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var totalBytes = entries.Sum(x => x.Size);
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copied = 0;
        var skipped = 0;
        var copiedBytes = 0L;
        var errors = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            var relative = MakeUniqueRelativePath(
                MakeWindowsSafeRelativePath(entry.RelativePath),
                entry.LinkTarget!,
                usedPaths);
            var target = RestorePathPolicy.ResolveUnderRoot(destinationRoot, relative);
            progress?.Report(new("media-copy", relative, index, entries.Count));
            try
            {
                if (IsUnchanged(target, entry))
                {
                    skipped++;
                    continue;
                }
                var parent = Path.GetDirectoryName(target)
                    ?? throw new InvalidDataException("Некорректный путь назначения.");
                Directory.CreateDirectory(parent);
                var temporary = Path.Combine(
                    parent,
                    $".{Path.GetFileName(target)}.vexark-legacy.part");
                try
                {
                    await using var source = await agent.OpenMediaFileAsync(
                        entry.LinkTarget!,
                        cancellationToken);
                    await using var output = new FileStream(
                        temporary,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        IoBufferBytes,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await source.CopyToAsync(output, IoBufferBytes, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    if (output.Length != entry.Size)
                        throw new InvalidDataException("Размер полученного файла не совпал.");
                    File.Move(temporary, target, overwrite: true);
                    ApplyTimestamp(target, entry.ModifiedUnixNanos);
                    copied++;
                    copiedBytes += entry.Size;
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                if (errors.Count < 50)
                    errors.Add($"{relative}: {error.Message}");
            }
        }
        stopwatch.Stop();
        return new(
            copied,
            skipped,
            entries.Count - copied - skipped,
            copiedBytes,
            totalBytes,
            errors,
            MediaTransportMode.Adb,
            copiedBytes / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001));
    }

    private static bool IsUnchanged(string target, FileEntry entry)
    {
        if (!File.Exists(target) || entry.ModifiedUnixNanos <= 0)
            return false;
        var info = new FileInfo(target);
        if (info.Length != entry.Size)
            return false;
        var sourceSeconds = entry.ModifiedUnixNanos / 1_000_000_000L;
        var targetSeconds = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
        return Math.Abs(sourceSeconds - targetSeconds) <= 2;
    }

    private static void ApplyTimestamp(string target, long unixNanos)
    {
        if (unixNanos <= 0)
            return;
        try
        {
            File.SetLastWriteTimeUtc(
                target,
                DateTimeOffset.FromUnixTimeSeconds(unixNanos / 1_000_000_000L).UtcDateTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A broken MediaStore timestamp must not invalidate an otherwise complete copy.
        }
    }

    private static string MakeWindowsSafeRelativePath(string source)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var segments = source.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment =>
            {
                var clean = new string(segment
                        .Select(character => invalid.Contains(character) ? '_' : character)
                        .ToArray())
                    .Trim()
                    .TrimEnd('.');
                if (clean.Length == 0)
                    clean = "_";
                var stem = Path.GetFileNameWithoutExtension(clean);
                if (ReservedWindowsNames.Contains(stem))
                    clean = "_" + clean;
                return clean;
            });
        var result = Path.Combine(segments.ToArray());
        if (!RestorePathPolicy.IsSafeRelativePath(result))
            throw new InvalidDataException($"Небезопасный путь MediaStore: {source}");
        return result;
    }

    private static string MakeUniqueRelativePath(
        string relative,
        string contentUri,
        ISet<string> used)
    {
        if (used.Add(relative))
            return relative;
        var directory = Path.GetDirectoryName(relative);
        var extension = Path.GetExtension(relative);
        var name = Path.GetFileNameWithoutExtension(relative);
        var mediaId = contentUri.TrimEnd('/').Split('/').LastOrDefault() ?? "copy";
        var candidateName = $"{name} ({mediaId}){extension}";
        var candidate = string.IsNullOrEmpty(directory)
            ? candidateName
            : Path.Combine(directory, candidateName);
        var suffix = 2;
        while (!used.Add(candidate))
        {
            candidateName = $"{name} ({mediaId}-{suffix++}){extension}";
            candidate = string.IsNullOrEmpty(directory)
                ? candidateName
                : Path.Combine(directory, candidateName);
        }
        return candidate;
    }

    private sealed record MediaWorkItem(
        FileEntry Entry,
        string Relative,
        string TargetPath,
        string PartPath,
        string SidecarPath,
        long Offset);

    private sealed record PreparedWork(
        IReadOnlyList<MediaWorkItem> Work,
        int Skipped,
        int ResumedFiles,
        long ResumedBytes);

    private static readonly HashSet<string> ReservedWindowsNames =
        new(
            [
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            ],
            StringComparer.OrdinalIgnoreCase);
}
