using AppTrace.Core.Attribution;
using AppTrace.Core.Model;

namespace AppTrace.Core.Tests;

/// <summary>
/// Small builders so attribution tests state only what matters to them.
/// </summary>
internal static class Fixtures
{
    public static AppIdentity App(
        string displayName,
        string? publisher = null,
        string? installLocation = null,
        string? version = null,
        string? id = null)
        => new()
        {
            Id = id ?? "app-" + TextNormalizer.Fold(displayName),
            DisplayName = displayName,
            NormalizedName = TextNormalizer.NormalizeDisplayName(displayName),
            Publisher = publisher,
            NormalizedPublisher = TextNormalizer.NormalizePublisher(publisher),
            Version = version,
            InstallLocation = installLocation,
            NormalizedInstallLocation = installLocation is null ? null : TextNormalizer.NormalizePath(installLocation),
            ProductCode = "{0000-0000}",
            RegistrySource = @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Test",
            RegistryRoot = RegistryRootKind.LocalMachine64,
        };

    /// <summary>
    /// Evaluates a path using the real engine with executable probing disabled, so
    /// results do not depend on what happens to be on the developer's machine.
    /// </summary>
    /// <remarks>
    /// Install-location evidence is derived by the engine from each application's
    /// own <see cref="AppIdentity.NormalizedInstallLocation"/>, exactly as it is
    /// during a real scan; a test cannot pass that evidence in directly, which is
    /// what keeps these tests honest about how attribution actually happens.
    /// </remarks>
    public static LocationAttribution Evaluate(
        string path,
        IReadOnlyList<AppIdentity> apps,
        LocationCategory category = LocationCategory.ProgramFiles,
        IReadOnlyList<string>? acceptedAncestors = null)
    {
        var engine = new AttributionEngine(apps, new AttributionOptions { MaxExecutableProbes = 0 });
        var normalized = TextNormalizer.NormalizePath(path);

        return engine.Evaluate(new AttributionInput
        {
            Path = path,
            NormalizedPath = normalized,
            DirectoryName = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar)),
            Category = category,
            AcceptedAncestorPaths = acceptedAncestors ?? [],
            NormalizedScanRoot = ScanRootFor(normalized, category),
            Depth = DepthBelowRoot(normalized, category),
        });
    }

    /// <summary>
    /// The deepest well-known root this test path sits under, mirroring how the
    /// scanner anchors a walk.
    /// </summary>
    /// <remarks>
    /// Path semantics needs the same anchor the scanner uses, otherwise a synthetic
    /// path would appear far deeper than it is and product-level directories would
    /// be treated as components. Keeping the fixture honest here is what lets the
    /// semantic rules be tested without weakening them.
    /// </remarks>
    private static string ScanRootFor(string normalizedPath, LocationCategory category)
    {
        var candidate = RootsFor(category)
            .Where(root => root.Length > 0
                && (normalizedPath.Equals(root, StringComparison.Ordinal)
                    || normalizedPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();

        return candidate ?? string.Empty;
    }

    private static int DepthBelowRoot(string normalizedPath, LocationCategory category)
    {
        var root = ScanRootFor(normalizedPath, category);
        return Math.Max(Segments(normalizedPath) - Segments(root), 0);
    }

    private static int Segments(string path)
        => path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;

    private static IEnumerable<string> RootsFor(LocationCategory category) => category switch
    {
        LocationCategory.ProgramFiles => [@"c:\program files", @"c:\program files (x86)"],
        LocationCategory.ProgramData => [@"c:\programdata"],
        LocationCategory.RoamingAppData => [@"c:\users\user\appdata\roaming"],
        LocationCategory.LocalAppData => [@"c:\users\user\appdata\local"],
        LocationCategory.LocalLowAppData => [@"c:\users\user\appdata\locallow"],
        _ => [],
    };

    public static CandidateOwner? Candidate(LocationAttribution attribution, string displayName)
        => attribution.Candidates.FirstOrDefault(c => c.AppId == "app-" + TextNormalizer.Fold(displayName));
}

