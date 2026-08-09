using PhoneBackup.Core;
using PhoneBackup.Desktop;

namespace PhoneBackup.Desktop.Tests;

public sealed class SignatureLineageTests
{
    [Fact]
    public void RestoreAcceptsAValidSigningCertificateRotation()
    {
        var backup = Metadata("old-cert", ["old-cert"]);
        var installed = Package("new-cert", ["old-cert", "new-cert"]);

        Assert.True(RestoreCoordinator.SignatureMatches(backup, installed));
    }

    [Fact]
    public void RestoreRejectsUnrelatedSigningLineages()
    {
        var backup = Metadata("backup-cert", ["backup-cert"]);
        var installed = Package("other-cert", ["other-cert"]);

        Assert.False(RestoreCoordinator.SignatureMatches(backup, installed));
    }

    private static PackageBackupMetadata Metadata(
        string certificate,
        IReadOnlyList<string> lineage) =>
        new(
            "com.example.app", 1, "1.0", certificate, true, [], false,
            lineage, 36, "");

    private static PackageSnapshot Package(
        string certificate,
        IReadOnlyList<string> lineage) =>
        new(
            "com.example.app", "Example", 1, "1.0", certificate, null,
            0, 10_123, false, true, [], [], [], false, [], lineage, 36, "");
}
