using System.Text.Json.Serialization;

namespace PhoneBackup.Core;

public static class ProtocolConstants
{
    public const int ProtocolVersion = 1;
    public const int AgentPort = 49321;
    public const int MaxJsonFrameBytes = 1024 * 1024;
    public const int DataFrameBytes = 1024 * 1024;
}

public enum RootState { Unavailable, Available, Granted, Denied }
public enum BackupMode { Portable, Full }
public enum CompatibilityLevel { Safe, Conditional, Blocked }
public enum MediaTransportMode { Auto, Adb, FastLan }
public enum TransferFrameType : byte
{
    Command = 1, Response = 2, FileMetadata = 3, Data = 4,
    Progress = 5, End = 6, Error = 7
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
    long DataAvailableBytes = 0);

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
    bool BatteryOptimizationExempt = false);

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

public sealed record BackupComponentManifest(
    string Id,
    string Kind,
    IReadOnlyList<FileEntry> Files,
    long PlainBytes,
    long StoredBytes,
    PackageBackupMetadata? Package = null);

public sealed record PackageBackupMetadata(
    string PackageName,
    long VersionCode,
    string VersionName,
    string SigningCertificateSha256,
    bool WasEnabled,
    IReadOnlyList<RuntimePermissionState>? RuntimePermissions = null,
    bool BatteryOptimizationExempt = false);

public sealed record ExcludedMediaReport(long FileCount, long TotalBytes, IReadOnlyList<string> Samples);

public sealed record SnapshotManifest(
    int FormatVersion,
    string SnapshotId,
    DateTimeOffset CreatedAtUtc,
    BackupMode Mode,
    DeviceInventory Device,
    IReadOnlyList<BackupComponentManifest> Components,
    ExcludedMediaReport ExcludedMedia,
    string Purpose = "manual");

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
[JsonSerializable(typeof(ProtocolEnvelope))]
public partial class PhoneBackupJsonContext : JsonSerializerContext;
