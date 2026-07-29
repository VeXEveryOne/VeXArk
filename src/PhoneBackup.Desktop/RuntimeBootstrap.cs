using System.Reflection;

namespace PhoneBackup.Desktop;

public static class RuntimeBootstrap
{
    private const string RuntimeVersion = "0.7.0";
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhoneBackup",
        "runtime",
        RuntimeVersion);

    public static string AdbPath => Path.Combine(Root, "adb", "adb.exe");
    public static string AgentApkPath => Path.Combine(Root, "agent", "phonebackup-agent.apk");

    public static void EnsureExtracted()
    {
        Extract("PhoneBackup.Runtime.adb.exe", AdbPath);
        Extract("PhoneBackup.Runtime.AdbWinApi.dll", Path.Combine(Root, "adb", "AdbWinApi.dll"));
        Extract("PhoneBackup.Runtime.AdbWinUsbApi.dll", Path.Combine(Root, "adb", "AdbWinUsbApi.dll"));
        Extract("PhoneBackup.Runtime.phonebackup-agent.apk", AgentApkPath);
    }

    private static void Extract(string resourceName, string destination)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var resource = assembly.GetManifestResourceStream(resourceName);
        if (resource is null) return;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var expectedLength = resource.Length;
        if (File.Exists(destination) && new FileInfo(destination).Length == expectedLength)
            return;

        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(
                       temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
