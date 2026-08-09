namespace PhoneBackup.Core;

public static class RestorePathPolicy
{
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
            return false;
        if (Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\'))
            return false;

        var normalized = path.Replace('\\', '/');
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(segment => segment is not "." and not "..");
    }

    public static string ResolveUnderRoot(string root, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
            throw new InvalidDataException($"Unsafe restore path: {relativePath}");

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Restore path escapes root: {relativePath}");
        return candidate;
    }
}

public sealed class MediaExclusionPolicy
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "DCIM", "Pictures", "Movies", ".thumbnails", ".Trash", "Trash"
    };

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".heic", ".heif",
        ".raw", ".dng", ".mp4", ".mkv", ".webm", ".avi", ".mov", ".3gp", ".m4v"
    };

    public bool ShouldExcludeSharedPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(ExcludedDirectories.Contains))
            return true;
        return ExcludedExtensions.Contains(Path.GetExtension(normalized));
    }
}

public static class CompatibilityEngine
{
    public static CompatibilityReport Compare(DeviceInventory source, DeviceInventory target)
    {
        var items = new List<CompatibilityItem>();
        var exact = source.Rom is not null && source.Rom.IsExactRomMatch(target.Rom);
        if (source.Rom is null || target.Rom is null)
        {
            exact = source.Device == target.Device &&
                    source.Sdk == target.Sdk &&
                    source.Fingerprint == target.Fingerprint;
        }
        var sameFamily = source.Device == target.Device && source.Sdk == target.Sdk;

        items.Add(new(
            "portable",
            source.Sdk <= target.Sdk + 1 ? CompatibilityLevel.Safe : CompatibilityLevel.Conditional,
            "Portable components are restored through Android APIs and per-package ownership remapping."));
        items.Add(new(
            "full.wifi",
            exact ? CompatibilityLevel.Safe :
            sameFamily ? CompatibilityLevel.Conditional : CompatibilityLevel.Blocked,
            exact ? "Build fingerprint matches." :
            sameFamily ? "Same device and SDK; explicit confirmation required." :
            "Wi-Fi internals are ROM-specific."));
        items.Add(new(
            "full.root_modules",
            exact ? CompatibilityLevel.Conditional : CompatibilityLevel.Blocked,
            "Root modules are never restored automatically."));

        var overall = items.Any(x => x.Level == CompatibilityLevel.Blocked)
            ? CompatibilityLevel.Conditional
            : items.MaxBy(x => x.Level)!.Level;
        return new(overall, items);
    }

    public static bool Allows(
        RestoreClass restoreClass,
        DeviceInventory source,
        DeviceInventory target,
        bool explicitlyConfirmed = false) => restoreClass switch
        {
            RestoreClass.Portable => source.Sdk <= target.Sdk + 1,
            RestoreClass.ExactRom => explicitlyConfirmed &&
                                     source.Rom?.IsExactRomMatch(target.Rom) == true,
            RestoreClass.ExactDevice => explicitlyConfirmed &&
                                        source.Rom?.IsExactRomMatch(target.Rom) == true &&
                                        string.Equals(source.Device, target.Device,
                                            StringComparison.Ordinal),
            _ => false
        };
}
