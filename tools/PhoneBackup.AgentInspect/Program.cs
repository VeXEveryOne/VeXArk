using PhoneBackup.Desktop;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: PhoneBackup.AgentInspect <adb-serial> <package> [package ...]");
    return 2;
}

RuntimeBootstrap.EnsureExtracted();
var adb = new AdbService();
await using var agent = await AgentClient.ConnectAsync(adb, args[0]);
var paired = await agent.PairWithApprovalAsync(
    TimeSpan.FromMinutes(2),
    new Progress<string>(Console.WriteLine));
if (!paired) throw new InvalidOperationException("Agent pairing was not approved.");
var root = await agent.RequestRootDetailsAsync();
if (!root.RootDataReady) throw new InvalidOperationException($"Root helper unavailable: {root.Detail}");

var packages = await agent.GetPackagesAsync(
    includeSystemApps: true,
    packageNames: args.Skip(1).ToArray(),
    hashArtifacts: false);
foreach (var package in packages)
{
    Console.WriteLine($"PACKAGE {package.PackageName} uid={package.Uid}");
    foreach (var dataRoot in package.DataRoots)
    {
        Console.WriteLine($"ROOT {dataRoot.Kind} {dataRoot.Path}");
        await foreach (var entry in agent.ScanRootAsync(
                           dataRoot.Path,
                           includeCaches: true,
                           fullHash: false))
        {
            if (entry.RelativePath is "files" or "databases" or "shared_prefs" or "no_backup" ||
                entry.RelativePath.StartsWith("files/datastore", StringComparison.Ordinal) ||
                entry.RelativePath.StartsWith("no_backup/", StringComparison.Ordinal) ||
                entry.RelativePath.Contains("workdb", StringComparison.Ordinal))
                Console.WriteLine(
                    $"  {entry.Kind,-9} {entry.RelativePath} mode={Convert.ToString(entry.Mode & 0xFFF, 8)} " +
                    $"uid={entry.Uid} gid={entry.Gid} label={entry.SelinuxLabel}");
        }
    }
}

return 0;
