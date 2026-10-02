using System.Runtime.Versioning;
using AppTrace.Core.Model;

namespace AppTrace.Core.Discovery;

/// <summary>A filesystem location AppTrace inspects, with the category it represents.</summary>
public sealed class ScanRoot
{
    public required string Path { get; init; }

    public required LocationCategory Category { get; init; }

    /// <summary>Label used in CLI output, e.g. <c>%ProgramFiles%</c>.</summary>
    public required string Label { get; init; }

    public override string ToString() => $"{Label} -> {Path}";
}

/// <summary>
/// Resolves the Windows well-known folders AppTrace inspects.
/// </summary>
[SupportedOSPlatform("windows")]
public static class KnownFolders
{
    /// <summary>
    /// The scan scope, in the order the spec lists it.
    /// </summary>
    /// <remarks>
    /// <c>%LOCALAPPDATA%</c> is deliberately first-class: Squirrel/Electron
    /// applications routinely keep their actual binaries and the bulk of their
    /// data there rather than in Program Files.
    /// </remarks>
    public static IReadOnlyList<ScanRoot> DefaultScanRoots()
    {
        var roots = new List<ScanRoot>();

        Add(roots, Environment.GetEnvironmentVariable("ProgramFiles"), LocationCategory.ProgramFiles, "%ProgramFiles%");
        Add(roots, Environment.GetEnvironmentVariable("ProgramFiles(x86)"), LocationCategory.ProgramFiles, "%ProgramFiles(x86)%");
        Add(roots, Environment.GetEnvironmentVariable("ProgramData"), LocationCategory.ProgramData, "%ProgramData%");
        Add(roots, LocalAppData(), LocationCategory.LocalAppData, "%LOCALAPPDATA%");
        Add(roots, RoamingAppData(), LocationCategory.RoamingAppData, "%APPDATA%");
        Add(roots, LocalLowAppData(), LocationCategory.LocalLowAppData, "%USERPROFILE%\\AppData\\LocalLow");

        return roots;
    }

    public static string? LocalAppData() => NonEmpty(Environment.GetEnvironmentVariable("LOCALAPPDATA"))
        ?? NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string? RoamingAppData() => NonEmpty(Environment.GetEnvironmentVariable("APPDATA"))
        ?? NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    public static string? LocalLowAppData()
    {
        var profile = NonEmpty(Environment.GetEnvironmentVariable("USERPROFILE"))
            ?? NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return profile is null ? null : Path.Combine(profile, "AppData", "LocalLow");
    }

    /// <summary>
    /// Classifies an arbitrary path against the known location set. Used for
    /// paths that fall outside the scan roots, most importantly install
    /// locations declared in the registry.
    /// </summary>
    public static LocationCategory Categorize(string path)
    {
        var normalized = TextNormalizer.NormalizePath(path);
        if (normalized.Length == 0)
        {
            return LocationCategory.Unknown;
        }

        foreach (var root in DefaultScanRoots())
        {
            var rootPath = TextNormalizer.NormalizePath(root.Path);
            if (rootPath.Length == 0)
            {
                continue;
            }

            if (normalized.Equals(rootPath, StringComparison.Ordinal))
            {
                return root.Category;
            }

            if (normalized.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return root.Category;
            }
        }

        return LocationCategory.Unknown;
    }

    private static void Add(List<ScanRoot> roots, string? path, LocationCategory category, string label)
    {
        var value = NonEmpty(path);
        if (value is null)
        {
            return;
        }

        var full = Path.GetFullPath(value);
        if (Directory.Exists(full))
        {
            roots.Add(new ScanRoot { Path = full, Category = category, Label = label });
        }
    }

    private static string? NonEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
