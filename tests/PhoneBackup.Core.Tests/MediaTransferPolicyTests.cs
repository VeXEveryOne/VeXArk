namespace PhoneBackup.Core.Tests;

public sealed class MediaTransferPolicyTests
{
    [Theory]
    [InlineData(40, 1)]
    [InlineData(80, 2)]
    [InlineData(200, 4)]
    public void FastLanWorkersRespectDiskSpeed(double diskMib, int expected)
    {
        var options = new MediaTransferOptions(MediaTransportMode.Auto);
        var workers = MediaTransferPolicy.SelectWorkerCount(
            MediaTransportMode.FastLan,
            diskMib * 1024 * 1024,
            options,
            4);
        Assert.Equal(expected, workers);
    }

    [Fact]
    public void AdbAndMemoryBudgetCapWorkers()
    {
        var options = new MediaTransferOptions(
            MediaTransportMode.Adb,
            AdbWorkers: 8,
            FastLanWorkers: 8,
            BufferBudgetBytes: 8L * 1024 * 1024);
        Assert.Equal(1, MediaTransferPolicy.SelectWorkerCount(
            MediaTransportMode.Adb,
            500L * 1024 * 1024,
            options));
    }

    [Fact]
    public void AutoNeedsFifteenPercentFastLanAdvantage()
    {
        Assert.False(MediaTransferPolicy.PreferFastLan(
            MediaTransportMode.Auto,
            true,
            100,
            114));
        Assert.True(MediaTransferPolicy.PreferFastLan(
            MediaTransportMode.Auto,
            true,
            100,
            115));
        Assert.True(MediaTransferPolicy.PreferFastLan(
            MediaTransportMode.FastLan,
            true,
            100,
            10));
    }

    [Fact]
    public void ResumeMetadataRejectsChangedSourceAndOversizedPart()
    {
        var entry = new FileEntry(
            "DCIM/photo.jpg",
            "file",
            100,
            123,
            0,
            0,
            0,
            null,
            "content://media/external/file/1",
            "image/jpeg");
        var state = new MediaResumeMetadata(
            1,
            entry.LinkTarget!,
            entry.Size,
            entry.ModifiedUnixNanos,
            50,
            DateTimeOffset.UtcNow);

        Assert.True(state.Matches(entry, 50));
        Assert.False(state.Matches(entry with { Size = 101 }, 50));
        Assert.False(state.Matches(entry, 101));
    }
}
