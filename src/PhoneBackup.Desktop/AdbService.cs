using System.Diagnostics;
using System.Text.RegularExpressions;
using PhoneBackup.Core;

namespace PhoneBackup.Desktop;

public sealed partial class AdbService
{
    public string AdbPath { get; }

    public AdbService()
    {
        var localSdk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "platform-tools", "adb.exe");
        var embedded = RuntimeBootstrap.AdbPath;
        AdbPath = File.Exists(embedded) ? embedded :
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
            ["-s", serial, "shell", "am", "start", "-n", $"{AgentPackage}/.MainActivity"],
            cancellationToken);

    public async Task<int> ForwardAgentPortAsync(string serial, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(
            ["-s", serial, "forward", "tcp:0", $"tcp:{ProtocolConstants.AgentPort}"],
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

    public const string AgentPackage = "com.vex.phonebackup.agent";

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

    public async Task<int> CreateInstallSessionAsync(
        string serial,
        long totalBytes,
        bool allowDowngrade,
        CancellationToken cancellationToken = default)
    {
        if (totalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(totalBytes));
        var arguments = new List<string>
        {
            "-s", serial, "shell", "pm", "install-create", "-r", "-S", totalBytes.ToString()
        };
        if (allowDowngrade) arguments.Add("-d");
        var result = await RunAsync(arguments, cancellationToken);
        var match = InstallSessionRegex().Match(result);
        if (!match.Success || !int.TryParse(match.Groups["id"].Value, out var sessionId))
            throw new InvalidOperationException($"PackageInstaller не создал session: {result.Trim()}");
        return sessionId;
    }

    public async Task WriteInstallSessionAsync(
        string serial,
        int sessionId,
        string splitName,
        long size,
        Func<Stream, CancellationToken, Task> producer,
        CancellationToken cancellationToken = default)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (!SafeSplitNameRegex().IsMatch(splitName))
            throw new ArgumentException("Некорректное имя APK split.", nameof(splitName));
        var start = CreateStartInfo([
            "-s", serial, "shell", "pm", "install-write",
            "-S", size.ToString(), sessionId.ToString(), splitName, "-"
        ]);
        start.RedirectStandardInput = true;
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Не удалось запустить ADB PackageInstaller.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        Exception? producerError = null;
        try
        {
            await producer(process.StandardInput.BaseStream, cancellationToken);
        }
        catch (Exception error)
        {
            producerError = error;
        }
        await process.StandardInput.BaseStream.DisposeAsync();
        await process.WaitForExitAsync(cancellationToken);
        var result = (await stdout) + (await stderr);
        if (producerError is not null &&
            !result.Contains("Success", StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                $"PackageInstaller closed the input stream: {result.Trim()}",
                producerError);
        if (process.ExitCode != 0 ||
            !result.Contains("Success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"PackageInstaller write failed: {result.Trim()}");
    }

    public async Task CommitInstallSessionAsync(
        string serial,
        int sessionId,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            ["-s", serial, "shell", "pm", "install-commit", sessionId.ToString()],
            cancellationToken);
        if (!result.Contains("Success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"PackageInstaller commit failed: {result.Trim()}");
    }

    public async Task AbandonInstallSessionAsync(
        string serial,
        int sessionId,
        CancellationToken cancellationToken = default) =>
        _ = await RunAsync(
            ["-s", serial, "shell", "pm", "install-abandon", sessionId.ToString()],
            cancellationToken,
            throwOnError: false);

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
        var start = CreateStartInfo(arguments);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить ADB.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var result = (await stdout) + (await stderr);
        if (throwOnError && process.ExitCode != 0)
            throw new InvalidOperationException(result.Trim());
        return result;
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

    [GeneratedRegex(@"\[(?<id>\d+)\]")]
    private static partial Regex InstallSessionRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeSplitNameRegex();

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
