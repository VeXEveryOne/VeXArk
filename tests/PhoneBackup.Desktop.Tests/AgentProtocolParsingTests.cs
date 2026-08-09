using System.Text.Json;
using System.Text;
using PhoneBackup.Desktop;

namespace PhoneBackup.Desktop.Tests;

public sealed class AgentProtocolParsingTests
{
    [Fact]
    public void ResponseEnvelopeAcceptsMatchingProtocolAndRequest()
    {
        using var document = JsonDocument.Parse(
            """{"protocolVersion":2,"requestId":"request-1","ok":true}""");

        AgentClient.ValidateResponseEnvelope(
            document.RootElement,
            "request-1",
            "root_request");
        AgentClient.EnsureOk(document.RootElement, "root_request");
    }

    [Fact]
    public void ResponseEnvelopeRejectsMismatchedRequest()
    {
        using var document = JsonDocument.Parse(
            """{"protocolVersion":2,"requestId":"wrong","ok":true}""");

        var error = Assert.Throws<InvalidDataException>(() =>
            AgentClient.ValidateResponseEnvelope(
                document.RootElement,
                "expected",
                "root_request"));

        Assert.Contains("requestId mismatch", error.Message);
    }

    [Theory]
    [InlineData("""{"protocolVersion":1,"requestId":"r","ok":false,"error":"not_paired"}""")]
    [InlineData("""{"protocolVersion":1,"requestId":"r","ok":null}""")]
    [InlineData("""{"protocolVersion":1,"requestId":"r"}""")]
    public void EnsureOkRejectsErrorsAndMissingBoolean(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.ThrowsAny<Exception>(() =>
            AgentClient.EnsureOk(document.RootElement, "root_request"));
    }

    [Theory]
    [InlineData("""{"available":null}""")]
    [InlineData("""{}""")]
    [InlineData("""{"available":"true"}""")]
    public void RequiredBooleanRejectsNullMissingAndWrongType(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() =>
            AgentClient.ReadRequiredBoolean(
                document.RootElement,
                "available",
                "root_request"));
    }

    [Fact]
    public void RootTraceParsesAllDiagnosticFields()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "attemptId":"attempt-1",
              "timestampUtc":"2026-07-30T12:00:00Z",
              "requestGrant":true,
              "appUid":10341,
              "appGrantState":"granted",
              "cachedShellPresent":true,
              "cachedShellAlive":true,
              "cachedShellRoot":false,
              "cachedShellClosed":true,
              "shellExitCode":0,
              "shellSuccess":true,
              "stdout":"uid=0(root)",
              "stderr":"",
              "selinux":"Enforcing",
              "exceptionType":null,
              "exceptionMessage":null,
              "durationMs":42,
              "steps":["cached_shell","root_shell"]
            }
            """);

        var result = AgentClient.ParseRootDiagnostics(document.RootElement);

        Assert.Equal("attempt-1", result.AttemptId);
        Assert.Equal(10341, result.AppUid);
        Assert.True(result.ShellSuccess);
        Assert.Equal("uid=0(root)", result.Stdout);
        Assert.Equal(["cached_shell", "root_shell"], result.Steps);
    }

    [Fact]
    public void DevProfileIsSideBySide()
    {
        Assert.Equal("com.vex.phonebackup.agent.dev", AgentRuntimeProfile.Dev.AgentPackage);
        Assert.Equal(49322, AgentRuntimeProfile.Dev.AgentPort);
        Assert.True(AgentRuntimeProfile.Dev.DiagnosticsEnabled);
        Assert.NotEqual(AgentRuntimeProfile.Stable.AgentPackage, AgentRuntimeProfile.Dev.AgentPackage);
        Assert.True(AppRuntimeProfile.IsDev);
    }

    [Fact]
    public void CanonicalPayloadPreservesUnicodeAndUsesPlatformIndependentEscaping()
    {
        var payload = new
        {
            root = "/data/user/0/пример",
            relative = "a/b\\c\"d\n\u0001😀"
        };

        var canonical = Encoding.UTF8.GetString(AgentClient.CanonicalPayload(payload));

        Assert.Equal(
            "{\"relative\":\"a/b\\\\c\\\"d\\n\\u0001😀\",\"root\":\"/data/user/0/пример\"}",
            canonical);
    }

    [Fact]
    public void RootHelperFailureIsPreserved()
    {
        using var document = JsonDocument.Parse(
            """{"ok":false,"exitCode":1,"stdout":"","stderr":"permission denied","exceptionType":null}""");

        var result = AgentClient.ParseRootHelperDiagnostics(document.RootElement);

        Assert.False(result.Ok);
        Assert.Equal(1, result.ExitCode);
        Assert.Null(result.Uid);
        Assert.Equal("permission denied", result.Stderr);
    }

    [Fact]
    public void RootDataRequiresUidZeroFromHelper()
    {
        var trace = new AgentRootDiagnostics(
            "attempt",
            DateTimeOffset.UtcNow,
            true,
            10341,
            "granted",
            false,
            false,
            false,
            false,
            0,
            true,
            "uid=0(root) gid=0(root)",
            "",
            "Enforcing",
            null,
            null,
            10,
            []);
        var notRoot = new RootRequestResult(
            true,
            true,
            "KernelSU",
            "uid=0(root)",
            trace,
            new AgentRootHelperDiagnostics(true, 0, 10341, "{}", "", null));
        var root = notRoot with
        {
            Helper = new AgentRootHelperDiagnostics(true, 0, 0, "{}", "", null)
        };

        Assert.False(notRoot.RootDataReady);
        Assert.True(root.RootDataReady);
    }

    [Fact]
    public void DiagnosticRedactionProtectsSecretsAndPairingCode()
    {
        Assert.Equal("[redacted]", DesktopDiagnostics.Redact("repositoryPassword", "secret"));
        Assert.Equal("[redacted]", DesktopDiagnostics.Redact("nonce", "secret"));
        Assert.Equal(
            ["-s", "[device]", "pair", "10.0.0.1:1234", "[redacted]"],
            DesktopDiagnostics.SanitizeAdbArguments(
                ["-s", "device-serial", "pair", "10.0.0.1:1234", "123456"]));
        Assert.Equal(
            "pairing code: [redacted]",
            DesktopDiagnostics.RedactText("pairing code: 123456"));
        Assert.Equal(
            """{"nonce":"[redacted]"}""",
            DesktopDiagnostics.RedactText("""{"nonce":"sensitive-value"}"""));
        Assert.Equal(
            "[device]\r\n",
            DesktopDiagnostics.SanitizeAdbOutput(
                ["-s", "device-serial", "shell", "getprop", "ro.serialno"],
                "device-serial\r\n"));
        Assert.Equal(
            "List of devices attached\r\n[device] device product:test\r\n",
            DesktopDiagnostics.SanitizeAdbOutput(
                ["devices", "-l"],
                "List of devices attached\r\ndevice-serial device product:test\r\n"));
    }

    [Fact]
    public void RollingLogKeepsConfiguredNumberOfFiles()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "vexark-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var current = Path.Combine(directory, "desktop.jsonl");
            File.WriteAllText(current, "current");
            File.WriteAllText(current + ".1", "one");
            File.WriteAllText(current + ".2", "two");

            DesktopDiagnostics.RotateFiles(current, 3);

            Assert.False(File.Exists(current));
            Assert.Equal("current", File.ReadAllText(current + ".1"));
            Assert.Equal("one", File.ReadAllText(current + ".2"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
