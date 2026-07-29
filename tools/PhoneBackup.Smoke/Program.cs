using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using PhoneBackup.Core;
using PhoneBackup.Desktop;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: PhoneBackup.Smoke <exact-adb-serial>");
    return 2;
}

var adb = new AdbService();
await using var agent = await AgentClient.ConnectAsync(adb, args[0]);
if (!await agent.PairWithApprovalAsync(
        TimeSpan.FromSeconds(30),
        new Progress<string>(Console.WriteLine)))
{
    Console.Error.WriteLine("Desktop pairing was not approved.");
    return 3;
}

using var hello = await agent.HelloAsync();
var capabilities = hello.RootElement.GetProperty("capabilities")
    .EnumerateArray()
    .Select(x => x.GetString())
    .Where(x => x is not null)
    .ToHashSet(StringComparer.Ordinal);
if (!capabilities.Contains("media-export") ||
    !capabilities.Contains("account-inventory"))
{
    Console.Error.WriteLine("Agent does not expose the new capabilities.");
    return 4;
}

var mediaCapability = await agent.GetMediaCapabilityAsync();
var accountBytes = await agent.ExportAccountInventoryAsync();
using var accountDocument = JsonDocument.Parse(accountBytes);
var accountCount = accountDocument.RootElement
    .GetProperty("accounts")
    .GetArrayLength();
if (accountDocument.RootElement.GetProperty("credentialsIncluded").GetBoolean())
{
    Console.Error.WriteLine("Account inventory unexpectedly contains credentials.");
    return 5;
}

var count = 0;
var totalBytes = 0L;
FileEntry? sample = null;
await foreach (var entry in agent.ScanMediaAsync())
{
    count++;
    totalBytes += entry.Size;
    if (sample is null && entry.Size is > 0 and <= 5 * 1024 * 1024)
        sample = entry;
}

var received = 0L;
var adbProbeBytesPerSecond = 0d;
var fastProbeBytesPerSecond = 0d;
var fastSessionClosed = true;
if (capabilities.Contains("media-export-v2"))
{
    const long probeBytes = 16L * 1024 * 1024;
    await using var probeWorker = await agent.ConnectSiblingAsync();
    await using var probe = await probeWorker.OpenMediaProbeAsync(probeBytes);
    var probeBuffer = new byte[1024 * 1024];
    var stopwatch = Stopwatch.StartNew();
    while (await probe.ReadAsync(probeBuffer) is var read && read > 0)
        received += read;
    _ = await probe.Completion;
    stopwatch.Stop();
    adbProbeBytesPerSecond = received / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
    received = 0;

    if (capabilities.Contains("fast-lan-aead-v1"))
    {
        var fast = await FastMediaClient.ConnectAsync(agent, 1);
        var fastSession = fast.Session;
        try
        {
            fastProbeBytesPerSecond = await fast.Workers[0].ProbeAsync(
                probeBytes,
                CancellationToken.None);
            if (sample is not null)
            {
                _ = await fast.Workers[0].CopyFileAsync(
                    sample.LinkTarget!,
                    0,
                    sample.Size,
                    sample.ModifiedUnixNanos,
                    Stream.Null,
                    count => received += count,
                    CancellationToken.None);
            }
        }
        finally
        {
            await fast.DisposeAsync();
        }

        using var closedSessionProbe = new TcpClient();
        using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await closedSessionProbe.ConnectAsync(
                fastSession.Host,
                fastSession.Port,
                closeTimeout.Token);
            fastSessionClosed = false;
        }
        catch (Exception error) when (
            error is SocketException or OperationCanceledException)
        {
            fastSessionClosed = true;
        }
    }
}

if (sample is not null)
{
    if (received == 0)
    {
        await using var input = await agent.OpenMediaFileAsync(sample.LinkTarget!);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) break;
            received += read;
        }
    }
    if (received != sample.Size)
    {
        Console.Error.WriteLine(
            $"Media stream size mismatch: expected {sample.Size}, received {received}.");
        return 6;
    }
}
if (!fastSessionClosed)
{
    Console.Error.WriteLine("Fast LAN listener remained open after session close.");
    return 7;
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    paired = true,
    mediaCapability.Images,
    mediaCapability.Videos,
    mediaCount = count,
    totalBytes,
    accountCount,
    sampleBytes = received,
    adbProbeMiBps = adbProbeBytesPerSecond / 1024 / 1024,
    fastProbeMiBps = fastProbeBytesPerSecond / 1024 / 1024,
    fastSessionClosed
}));
return 0;
