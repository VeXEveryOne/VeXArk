namespace PhoneBackup.Core;

public sealed record MediaResumeMetadata(
    int Version,
    string Uri,
    long Size,
    long ModifiedUnixNanos,
    long Offset,
    DateTimeOffset UpdatedAtUtc)
{
    public bool Matches(FileEntry entry, long partLength) =>
        Version == 1 &&
        string.Equals(Uri, entry.LinkTarget, StringComparison.Ordinal) &&
        Size == entry.Size &&
        ModifiedUnixNanos == entry.ModifiedUnixNanos &&
        partLength >= 0 &&
        partLength <= Size;
}

public static class MediaTransferPolicy
{
    public static int SelectWorkerCount(
        MediaTransportMode transport,
        double diskBytesPerSecond,
        MediaTransferOptions options,
        int availableFastLanWorkers = 4)
    {
        var diskWorkers = diskBytesPerSecond < 50 * 1024 * 1024
            ? 1
            : diskBytesPerSecond < 120 * 1024 * 1024 ? 2 : 4;
        var transportWorkers = transport == MediaTransportMode.FastLan
            ? Math.Min(options.FastLanWorkers, availableFastLanWorkers)
            : Math.Min(options.AdbWorkers, 2);
        var budgetWorkers = checked((int)Math.Max(
            1,
            options.BufferBudgetBytes / (8L * 1024 * 1024)));
        return Math.Clamp(
            Math.Min(Math.Min(transportWorkers, diskWorkers), budgetWorkers),
            1,
            4);
    }

    public static bool PreferFastLan(
        MediaTransportMode requested,
        bool available,
        double adbBytesPerSecond,
        double fastLanBytesPerSecond) =>
        available &&
        (requested == MediaTransportMode.FastLan ||
         requested == MediaTransportMode.Auto &&
         fastLanBytesPerSecond >= adbBytesPerSecond * 1.15);
}
