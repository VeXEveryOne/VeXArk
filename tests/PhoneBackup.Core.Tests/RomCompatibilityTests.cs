using PhoneBackup.Core;

namespace PhoneBackup.Core.Tests;

public sealed class RomCompatibilityTests
{
    [Fact]
    public void ExactRomIgnoresSpoofedFingerprintAndUsesIdentityTuple()
    {
        var source = Device("spoof-a", Identity("12.11", "settings-signature"));
        var target = Device("spoof-b", Identity("12.11", "settings-signature"));

        Assert.True(source.Rom!.IsExactRomMatch(target.Rom));
        Assert.True(CompatibilityEngine.Allows(
            RestoreClass.ExactRom, source, target, explicitlyConfirmed: true));
    }

    [Fact]
    public void ExactRomRejectsProviderOrPackageSchemaDrift()
    {
        var source = Device("same", Identity("12.11", "settings-signature"));
        var target = Device("same", Identity("12.12", "settings-signature"));

        Assert.False(source.Rom!.IsExactRomMatch(target.Rom));
        Assert.False(CompatibilityEngine.Allows(
            RestoreClass.ExactRom, source, target, explicitlyConfirmed: true));
    }

    [Fact]
    public void ExactRomRejectsDifferentIncrementalEvenWithSpoofedFingerprint()
    {
        var source = Device("spoof", Identity("12.11", "settings-signature"));
        var targetIdentity = Identity("12.11", "settings-signature") with
        {
            BuildIncremental = "different-incremental"
        };

        Assert.False(source.Rom!.IsExactRomMatch(targetIdentity));
    }

    [Fact]
    public void ArchiveOnlyAndNeverAreNeverAutomaticallyAllowed()
    {
        var device = Device("same", Identity("12.11", "settings-signature"));

        Assert.False(CompatibilityEngine.Allows(
            RestoreClass.ArchiveOnly, device, device, explicitlyConfirmed: true));
        Assert.False(CompatibilityEngine.Allows(
            RestoreClass.Never, device, device, explicitlyConfirmed: true));
    }

    private static DeviceInventory Device(string fingerprint, RomIdentity identity) => new(
        "id", "Xiaomi 15", "dada", "16", 36, fingerprint, "arm64-v8a", "Enforcing",
        RootState.Granted, [], Rom: identity);

    private static RomIdentity Identity(string build, string signature) => new(
        36,
        "dada",
        "incremental",
        "16.0",
        build,
        "dada",
        new Dictionary<string, int> { ["lineage.system"] = 6 },
        new Dictionary<string, string> { ["com.android.settings"] = signature },
        ["aosp-settings", "crdroid-settings"]);
}
