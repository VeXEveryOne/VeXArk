using System.Diagnostics;
using System.Text.RegularExpressions;
using PhoneBackup.Core;

namespace PhoneBackup.Desktop;

public sealed partial class AdbService
{
    private const string AgentActivityClass =
        "com.vex.phonebackup.agent.MainActivity";

    public string AdbPath { get; }

    public AdbService() : this(ResolveAdbPath())
    {
    }

    internal AdbService(string adbPath)
    {
        if (!File.Exists(adbPath))
            throw new FileNotFoundException(
                "adb.exe не найден. Установите Android Platform Tools.",
                adbPath);
        AdbPath = adbPath;
    }

    private static string ResolveAdbPath()
    {
        var localSdk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "platform-tools", "adb.exe");
        var embedded = RuntimeBootstrap.AdbPath;
        return File.Exists(embedded) ? embedded :
            File.Exists(localSdk) ? localSdk :
            throw new FileNotFoundException("adb.exe не найден. Установите Android Platform Tools.");
    }

    public string AgentApkPath => RuntimeBootstrap.AgentApkPath;

    public async Task<IReadOnlyList<DeviceInventory>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(["devices", "-l"], cancellationToken);
        var transports = new List<(string Serial, string Kind)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var match = DeviceLineRegex().Match(line.Trim());
            if (!match.Success || match.Groups["state"].Value != "device") continue;
            var serial = match.Groups["serial"].Value;
            transports.Add((serial, serial.Contains("._adb-tls-connect._tcp", StringComparison.Ordinal)
                ? "Wi-Fi" : "USB"));
        }

        var probes = await Task.WhenAll(transports.Select(async transport =>
        {
            var props = await ProbeAsync(transport.Serial, cancellationToken);
            return (transport, props);
        }));

        var result = new List<DeviceInventory>();
        foreach (var group in probes.GroupBy(x => x.props.StableId))
        {
            var preferred = group.OrderBy(x => x.transport.Kind == "USB" ? 0 : 1).First();
            var deviceTransports = group.Select(x => new DeviceTransport(
                x.transport.Serial,
                x.transport.Kind,
                x.transport.Serial == preferred.transport.Serial)).ToList();
            var p = preferred.props;
            result.Add(new(
                p.StableId, p.Model, p.Device, p.AndroidVersion, p.Sdk, p.Fingerprint,
                p.Abi, p.Selinux, p.Root, deviceTransports, p.Total, p.Available));
        }
        return result;
    }

    public async Task<bool> IsAgentInstalledAsync(string serial, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(["-s", serial, "shell", "pm", "path", AgentPackage], cancellationToken, false);
        return output.Contains("package:", StringComparison.Ordinal);
    }

    public async Task<InstalledAgentInfo?> GetInstalledAgentInfoAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        if (!await IsAgentInstalledAsync(serial, cancellationToken)) return null;
        var output = await RunAsync(
            ["-s", serial, "shell", "dumpsys", "package", AgentPackage],
            cancellationToken,
            false);
        var versionName = Regex.Match(output, @"(?m)^\s*versionName=(?<value>\S+)\s*$")
            .Groups["value"].Value;
        var versionCodeText = Regex.Match(output, @"(?m)^\s*versionCode=(?<value>\d+)")
            .Groups["value"].Value;
        var userIdText = Regex.Match(output, @"(?m)^\s*userId=(?<value>\d+)\s*$")
            .Groups["value"].Value;
        int.TryParse(versionCodeText, out var versionCode);
        int.TryParse(userIdText, out var userId);

        var hashOutput = await RunAsync(
            [
                "-s", serial, "shell", "sh", "-c",
                $"apk=$(pm path {AgentPackage} | head -n 1 | cut -d: -f2); " +
                "if [ -n \"$apk\" ]; then sha256sum \"$apk\"; fi"
            ],
            cancellationToken,
            false);
        var sha256 = Regex.Match(hashOutput, @"(?i)\b[0-9a-f]{64}\b").Value.ToLowerInvariant();
        return new(AgentPackage, versionName, versionCode, userId, sha256);
    }

    public async Task InstallAgentAsync(string serial, string apkPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(apkPath))
            throw new FileNotFoundException("Agent APK ещё не собран.", apkPath);
        var result = await RunAsync(["-s", serial, "install", "-r", apkPath], cancellationToken);
        if (!result.Contains("Success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(result);
    }

    public async Task PairWirelessAsync(
        string endpoint,
        string pairingCode,
        CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint);
        if (pairingCode.Length is < 6 or > 12 || pairingCode.Any(x => !char.IsDigit(x)))
            throw new ArgumentException("Некорректный код Wireless ADB.", nameof(pairingCode));
        var result = await RunAsync(["pair", endpoint, pairingCode], cancellationToken);
        if (!result.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(result.Trim());
    }

    public async Task ConnectWirelessAsync(
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint);
        var result = await RunAsync(["connect", endpoint], cancellationToken);
        if (!result.Contains("connected", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(result.Trim());
    }

    public async Task LaunchAgentAsync(string serial, CancellationToken cancellationToken = default) =>
        _ = await RunAsync(
            [
                "-s", serial, "shell", "am", "start", "-n",
                $"{AgentPackage}/{AgentActivityClass}"
            ],
            cancellationToken);

    public async Task<int> ForwardAgentPortAsync(string serial, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(
            ["-s", serial, "forward", "tcp:0", $"tcp:{AppRuntimeProfile.AgentPort}"],
            cancellationToken);
        if (!int.TryParse(output.Trim(), out var port))
            throw new InvalidOperationException($"ADB не вернул локальный порт: {output}");
        return port;
    }

    public async Task RemoveAgentForwardAsync(
        string serial,
        int localPort,
        CancellationToken cancellationToken = default)
    {
        if (localPort is <= 0 or > 65535)
            return;
        _ = await RunAsync(
            ["-s", serial, "forward", "--remove", $"tcp:{localPort}"],
            cancellationToken,
            throwOnError: false);
    }

    public static string AgentPackage => AppRuntimeProfile.AgentPackage;

    public async Task InstallMultipleAsync(
        string serial,
        IReadOnlyList<string> apkPaths,
        bool allowDowngrade,
        CancellationToken cancellationToken = default)
    {
        if (apkPaths.Count == 0) throw new ArgumentException("APK list is empty.", nameof(apkPaths));
        if (apkPaths.Any(x => !File.Exists(x)))
            throw new FileNotFoundException("One or more staged APK files are missing.");
        var arguments = new List<string> { "-s", serial, "install-multiple", "-r" };
        if (allowDowngrade) arguments.Add("-d");
        arguments.AddRange(apkPaths);
        var result = await RunAsync(arguments, cancellationToken);
        if (!result.Contains("Success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(result);
    }

    public Task<Stream> OpenRemoteFileAsync(
        string serial,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        if (!remotePath.StartsWith('/') ||
            remotePath.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("Некорректный путь Android.", nameof(remotePath));

        var start = CreateStartInfo(["-s", serial, "exec-out", "cat", remotePath]);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить ADB.");
        return Task.FromResult<Stream>(new AdbProcessReadStream(process, cancellationToken));
    }

    private async Task<ProbeResult> ProbeAsync(string serial, CancellationToken cancellationToken)
    {
        async Task<string> Shell(params string[] args) =>
            (await RunAsync(["-s", serial, "shell", .. args], cancellationToken, false)).Trim();

        var modelTask = Shell("getprop", "ro.product.model");
        var deviceTask = Shell("getprop", "ro.product.device");
        var androidTask = Shell("getprop", "ro.build.version.release");
        var sdkTask = Shell("getprop", "ro.build.version.sdk");
        var fingerprintTask = Shell("getprop", "ro.build.fingerprint");
        var abiTask = Shell("getprop", "ro.product.cpu.abi");
        var stableTask = Shell("getprop", "ro.serialno");
        var selinuxTask = Shell("getenforce");
        var rootTask = Shell("sh", "-c", "command -v su");
        await Task.WhenAll(modelTask, deviceTask, androidTask, sdkTask, fingerprintTask,
            abiTask, stableTask, selinuxTask, rootTask);

        var disk = await Shell("df", "-k", "/data");
        var numbers = DiskRegex().Match(disk);
        long total = 0, available = 0;
        if (numbers.Success)
        {
            long.TryParse(numbers.Groups["total"].Value, out total);
            long.TryParse(numbers.Groups["available"].Value, out available);
            total *= 1024;
            available *= 1024;
        }

        return new(
            string.IsNullOrWhiteSpace(stableTask.Result) ? serial : stableTask.Result,
            modelTask.Result,
            deviceTask.Result,
            androidTask.Result,
            int.TryParse(sdkTask.Result, out var sdk) ? sdk : 0,
            fingerprintTask.Result,
            abiTask.Result,
            selinuxTask.Result,
            string.IsNullOrWhiteSpace(rootTask.Result) ? RootState.Unavailable : RootState.Available,
            total,
            available);
    }

    internal async Task<string> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnError = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var safeArguments = DesktopDiagnostics.SanitizeAdbArguments(arguments);
        DesktopDiagnostics.Log(
            "info",
            "adb",
            "command_started",
            "ADB command started",
            fields: new Dictionary<string, object?>
            {
                ["arguments"] = string.Join(" ", safeArguments)
            });
        var start = CreateStartInfo(arguments);
        try
        {
            using var process = Process.Start(start) ??
                                throw new InvalidOperationException("Не удалось запустить ADB.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var standardOutput = await stdout;
            var standardError = await stderr;
            var result = standardOutput + standardError;
            var safeStandardOutput = DesktopDiagnostics.SanitizeAdbOutput(
                arguments,
                standardOutput);
            var safeStandardError = DesktopDiagnostics.SanitizeAdbOutput(
                arguments,
                standardError);
            DesktopDiagnostics.Log(
                process.ExitCode == 0 ? "info" : "warn",
                "adb",
                "command_completed",
                "ADB command completed",
                durationMs: stopwatch.ElapsedMilliseconds,
                result: process.ExitCode.ToString(),
                fields: new Dictionary<string, object?>
                {
                    ["arguments"] = string.Join(" ", safeArguments),
                    ["exitCode"] = process.ExitCode,
                    ["stdout"] = safeStandardOutput,
                    ["stderr"] = safeStandardError
                });
            if (throwOnError && process.ExitCode != 0)
                throw new InvalidOperationException(result.Trim());
            return result;
        }
        catch (Exception error)
        {
            DesktopDiagnostics.Log(
                "error",
                "adb",
                "command_failed",
                "ADB command failed",
                durationMs: stopwatch.ElapsedMilliseconds,
                error: error,
                fields: new Dictionary<string, object?>
                {
                    ["arguments"] = string.Join(" ", safeArguments)
                });
            throw;
        }
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static void ValidateEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            endpoint.Length > 255 ||
            endpoint.Any(char.IsWhiteSpace) ||
            !endpoint.Contains(':'))
            throw new ArgumentException("Endpoint должен иметь вид IP:порт.", nameof(endpoint));
    }

    private sealed record ProbeResult(
        string StableId, string Model, string Device, string AndroidVersion,
        int Sdk, string Fingerprint, string Abi, string Selinux, RootState Root,
        long Total, long Available);

    [GeneratedRegex(@"^(?<serial>\S+)\s+(?<state>\S+).*$")]
    private static partial Regex DeviceLineRegex();

    [GeneratedRegex(@"(?m)^\S+\s+(?<total>\d+)\s+\d+\s+(?<available>\d+)\s+\d+%\s+\S+\s*$")]
    private static partial Regex DiskRegex();

    private sealed class AdbProcessReadStream(
        Process process,
        CancellationToken cancellationToken) : Stream
    {
        private readonly Stream _inner = process.StandardOutput.BaseStream;
        private readonly Task<string> _stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        private bool _disposed;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken token = default) => _inner.ReadAsync(buffer, token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _inner.DisposeAsync();
            await process.WaitForExitAsync(cancellationToken);
            var error = await _stderr;
            var exitCode = process.ExitCode;
            process.Dispose();
            if (exitCode != 0)
                throw new IOException($"ADB read failed ({exitCode}): {error.Trim()}");
            GC.SuppressFinalize(this);
        }
    }
}

public sealed record InstalledAgentInfo(
    string PackageName,
    string VersionName,
    int VersionCode,
    int UserId,
    string Sha256);
