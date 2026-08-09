using System.Text.Json.Serialization;

namespace PhoneBackup.Core;

public static class ProtocolConstants
{
    public const int ProtocolVersion = 2;
    public const int MinimumProtocolVersion = 2;
    public const int AgentPort = 49321;
    public const int MaxJsonFrameBytes = 1024 * 1024;
    public const int DataFrameBytes = 1024 * 1024;
}

public enum RootState { Unavailable, Available, Granted, Denied }
public enum BackupMode { Portable, Full }
public enum CompatibilityLevel { Safe, Conditional, Blocked }
public enum ComponentStatus { Unknown, Complete, Partial, Skipped, Failed }
public enum RestoreClass { Portable, ExactRom, ExactDevice, ArchiveOnly, Never }
public enum DataRootKind { Unknown, CredentialEncrypted, DeviceEncrypted, ExternalData, Obb }
public enum RestoreItemState
{
    Pending, Running, NeedsUser, Succeeded, Skipped, Failed, RolledBack
}
public enum SettingValueType
{
    Boolean, Integer, Float, Enum, String, Package, ComponentList, TileList
}
public enum SettingsCoverageStatus { Supported, Archived, Ignored }
public enum MediaTransportMode { Auto, Adb, FastLan }
public enum TransferFrameType : byte
{
    Command = 1, Response = 2, FileMetadata = 3, Data = 4,
    Progress = 5, End = 6, Error = 7, Abort = 8
}

public sealed record AgentHello(
    int ProtocolVersion,
    string AgentVersion,
    string HelperVersion,
    IReadOnlyList<string> Capabilities);

public sealed record DeviceTransport(
    string AdbSerial,
    string Kind,
    bool IsPreferred);

public sealed record DeviceInventory(
    string StableId,
    string Model,
    string Device,
    string AndroidVersion,
    int Sdk,
    string Fingerprint,
    string Abi,
    string Selinux,
    RootState Root,
    IReadOnlyList<DeviceTransport> Transports,
    long DataTotalBytes = 0,
    long DataAvailableBytes = 0,
    RomIdentity? Rom = null);

public sealed record RomIdentity(
    int Sdk,
    string Device,
    string BuildIncremental,
    string? CrDroidVersion,
    string? CrDroidBuildVersion,
    string? LineageDevice,
    IReadOnlyDictionary<string, int> ProviderSchemaVersions,
    IReadOnlyDictionary<string, string> PackageSignatures,
    IReadOnlyList<string> Capabilities,
    IReadOnlyDictionary<string, long>? PackageVersions = null)
{
    public bool IsExactRomMatch(RomIdentity? other)
    {
        if (other is null || Sdk != other.Sdk ||
            !string.Equals(Device, other.Device, StringComparison.Ordinal) ||
            !string.Equals(BuildIncremental, other.BuildIncremental, StringComparison.Ordinal) ||
            !string.Equals(CrDroidVersion ?? string.Empty, other.CrDroidVersion ?? string.Empty,
                StringComparison.Ordinal) ||
            !string.Equals(CrDroidBuildVersion ?? string.Empty,
                other.CrDroidBuildVersion ?? string.Empty, StringComparison.Ordinal) ||
            !string.Equals(LineageDevice ?? string.Empty, other.LineageDevice ?? string.Empty,
                StringComparison.Ordinal))
            return false;

        if (!DictionaryEquals(ProviderSchemaVersions, other.ProviderSchemaVersions))
            return false;
        if (PackageSignatures.Count != other.PackageSignatures.Count ||
            !PackageSignatures.All(pair =>
            other.PackageSignatures.TryGetValue(pair.Key, out var signature) &&
            string.Equals(pair.Value, signature, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (PackageVersions is null) return other.PackageVersions is null;
        return other.PackageVersions is not null &&
               PackageVersions.Count == other.PackageVersions.Count && PackageVersions.All(pair =>
            other.PackageVersions?.TryGetValue(pair.Key, out var version) == true &&
            pair.Value == version);
    }

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var value) && value == pair.Value);
}

public sealed record AppDataRoot(
    DataRootKind Kind,
    string Path,
    int UserId);

public sealed record PackageSnapshot(
    string PackageName,
    string Label,
    long VersionCode,
    string VersionName,
    string SigningCertificateSha256,
    string? Installer,
    int UserId,
    int Uid,
    bool IsSystem,
    bool IsEnabled,
    IReadOnlyList<ApkArtifact> ApkArtifacts,
    IReadOnlyList<string> DataPaths,
    IReadOnlyList<RuntimePermissionState>? RuntimePermissions = null,
    bool BatteryOptimizationExempt = false,
    IReadOnlyList<AppDataRoot>? DataRoots = null,
    IReadOnlyList<string>? SigningLineageSha256 = null,
    int TargetSdk = 0,
    string? AppLocales = null);

public sealed record RuntimePermissionState(
    string Name,
    bool Granted,
    int Flags);

public sealed record ApkArtifact(
    string Path,
    string FileName,
    long Size,
    long ModifiedUnixNanos,
    string Sha256);

public sealed record FileEntry(
    string RelativePath,
    string Kind,
    long Size,
    long ModifiedUnixNanos,
    int Mode,
    int Uid,
    int Gid,
    string? SelinuxLabel,
    string? LinkTarget,
    string? ContentHash,
    IReadOnlyList<ChunkReference>? Chunks = null);

public sealed record ChunkReference(string ObjectId, int PlainLength, int StoredLength);

public sealed record BackupPlan(
    BackupMode Mode,
    IReadOnlyList<string> Packages,
    bool IncludeCaches,
    bool IncludeSystemApps,
    bool IncludeSharedStorage,
    bool IncludeContacts,
    bool IncludeMessages,
    bool FullHashVerification);

public sealed record RestorePlan(
    string SnapshotId,
    IReadOnlyList<string> Packages,
    bool RestoreAppData,
    bool RestoreSharedStorage,
    bool RestoreSafeSettings,
    IReadOnlyList<string> ExplicitFullComponents);

public sealed record CompatibilityItem(
    string Component,
    CompatibilityLevel Level,
    string Reason);

public sealed record CompatibilityReport(
    CompatibilityLevel Overall,
    IReadOnlyList<CompatibilityItem> Items);

public sealed record SettingDescriptor(
    string Id,
    string DisplayName,
    string Adapter,
    string Source,
    SettingValueType ValueType,
    RestoreClass RestoreClass,
    bool RequiresConfirmation,
    string? Capability = null,
    double? Minimum = null,
    double? Maximum = null,
    IReadOnlyList<string>? AllowedValues = null);

public sealed record SettingsCoverageEntry(
    string Id,
    string Adapter,
    SettingsCoverageStatus Status,
    RestoreClass RestoreClass,
    string Reason);

public sealed record SettingApplyOutcome(
    string Id,
    string Status,
    string Reason,
    string? Adapter = null);

public sealed record SystemStateRestoreResult(
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> NeedsConfirmation,
    IReadOnlyList<SettingApplyOutcome> Outcomes);

public sealed record SettingsCoverageSummary(
    int Supported,
    int Archived,
    int Ignored,
    int Unknown,
    IReadOnlyList<string> UnknownSamples);

public sealed record PersonalRestoreResult(
    string Kind,
    string Status,
    int Inserted,
    int Skipped,
    int Failed,
    string? Reason = null);

public sealed record ComponentRestoreResult(
    string ComponentId,
    RestoreItemState State,
    string? Reason = null);

public sealed record ComponentFailure(
    string Stage,
    string? RelativePath,
    string ErrorCode,
    string Message);

public sealed record ComponentVerification(
    long ExpectedFiles,
    long CapturedFiles,
    long ExpectedBytes,
    long CapturedBytes,
    bool HashesVerified);

public sealed record BackupComponentManifest(
    string Id,
    string Kind,
    IReadOnlyList<FileEntry> Files,
    long PlainBytes,
    long StoredBytes,
    PackageBackupMetadata? Package = null,
    ComponentStatus Status = ComponentStatus.Unknown,
    RestoreClass RestoreClass = RestoreClass.ArchiveOnly,
    string? SchemaId = null,
    int SchemaVersion = 1,
    int UserId = 0,
    DataRootKind DataRootKind = DataRootKind.Unknown,
    IReadOnlyList<ComponentFailure>? Failures = null,
    ComponentVerification? Verification = null);

public sealed record PackageBackupMetadata(
    string PackageName,
    long VersionCode,
    string VersionName,
    string SigningCertificateSha256,
    bool WasEnabled,
    IReadOnlyList<RuntimePermissionState>? RuntimePermissions = null,
    bool BatteryOptimizationExempt = false,
    IReadOnlyList<string>? SigningLineageSha256 = null,
    int TargetSdk = 0,
    string? AppLocales = null);

public sealed record ExcludedMediaReport(long FileCount, long TotalBytes, IReadOnlyList<string> Samples);

public sealed record SnapshotManifest(
    int FormatVersion,
    string SnapshotId,
    DateTimeOffset CreatedAtUtc,
    BackupMode Mode,
    DeviceInventory Device,
    IReadOnlyList<BackupComponentManifest> Components,
    ExcludedMediaReport ExcludedMedia,
    string Purpose = "manual",
    RomIdentity? Rom = null,
    SnapshotSummary? Summary = null);

public sealed record SnapshotSummary(
    ComponentStatus Status,
    int CompleteComponents,
    int PartialComponents,
    int FailedComponents,
    int SkippedComponents,
    IReadOnlyList<string> Warnings);

public sealed record RepositoryHeader(
    int FormatVersion,
    string RepositoryId,
    string PasswordSalt,
    int KdfIterations,
    int KdfMemoryKiB,
    int KdfParallelism,
    string KdfAlgorithm,
    string PasswordWrappedMasterKey,
    string RecoveryWrappedMasterKey,
    string RecoveryKeyId,
    string HashAlgorithm,
    string CompressionAlgorithm);

public sealed record TransferProgress(string Stage, string Item, long Completed, long Total);

public sealed record RestoreProgress(
    string Stage,
    string Item,
    RestoreItemState State,
    long Completed,
    long Total,
    string? ErrorCode = null,
    string? Message = null);

public sealed record MediaTransferOptions(
    MediaTransportMode Transport,
    int AdbWorkers = 2,
    int FastLanWorkers = 4,
    long BufferBudgetBytes = 64L * 1024 * 1024);

public sealed record MediaTransferMetrics(
    MediaTransportMode Transport,
    double BytesPerSecond,
    double DiskBytesPerSecond,
    long CompletedBytes,
    long TotalBytes,
    long ResumedBytes,
    int ActiveFiles,
    TimeSpan? EstimatedRemaining);

public sealed record FastMediaSession(
    string SessionId,
    string Host,
    int Port,
    DateTimeOffset ExpiresAtUtc,
    int MaxWorkers);

public sealed record ProtocolEnvelope(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    object? Payload);

[JsonSerializable(typeof(AgentHello))]
[JsonSerializable(typeof(DeviceInventory))]
[JsonSerializable(typeof(PackageSnapshot))]
[JsonSerializable(typeof(BackupPlan))]
[JsonSerializable(typeof(RestorePlan))]
[JsonSerializable(typeof(CompatibilityReport))]
[JsonSerializable(typeof(SnapshotManifest))]
[JsonSerializable(typeof(RomIdentity))]
[JsonSerializable(typeof(SettingDescriptor))]
[JsonSerializable(typeof(SettingsCoverageEntry))]
[JsonSerializable(typeof(ProtocolEnvelope))]
public partial class PhoneBackupJsonContext : JsonSerializerContext;
