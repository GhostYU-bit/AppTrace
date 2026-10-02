using AppTrace.Core.Attribution;
using AppTrace.Core.Model;

namespace AppTrace.Core.Tests;

/// <summary>
/// Small builders so attribution tests state only what matters to them.
/// </summary>
internal static class Fixtures
{
    /// <summary>
    /// The synthetic per-user package root these fixtures evaluate against, so
    /// package attribution never depends on what is installed on the machine
    /// running the tests.
    /// </summary>
    public const string PackageDataRoot = @"C:\Users\user\AppData\Local\Packages";

    public static AppIdentity App(
        string displayName,
        string? publisher = null,
        string? installLocation = null,
        string? version = null,
        string? id = null,
        string? productCode = "{0000-0000}",
        string? packageFamilyName = null,
        string? displayIcon = null,
        string? uninstallString = null)
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
            DisplayIcon = displayIcon,
            UninstallString = uninstallString,
            ProductCode = productCode,
            RegistrySource = @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Test",
            RegistryRoot = RegistryRootKind.LocalMachine64,
            PackageFamilyNames = packageFamilyName is null ? [] : [packageFamilyName],
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
        IReadOnlyList<string>? acceptedAncestors = null,
        IReadOnlyList<OwnedAncestor>? ownedAncestors = null,
        Discovery.ProvenanceIndex? provenance = null,
        string? packageDataRoot = null)
    {
        var engine = new AttributionEngine(
            apps,
            new AttributionOptions
            {
                MaxExecutableProbes = 0,
                PackageDataRoot = packageDataRoot ?? PackageDataRoot,
            },
            provenance);
        var normalized = TextNormalizer.NormalizePath(path);

        return engine.Evaluate(new AttributionInput
        {
            Path = path,
            NormalizedPath = normalized,
            DirectoryName = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar)),
            Category = category,
            AcceptedAncestorPaths = acceptedAncestors ?? [],
            OwnedAncestors = ownedAncestors ?? [],
            NormalizedScanRoot = ScanRootFor(normalized, category),
            Depth = DepthBelowRoot(normalized, category),
        });
    }

    /// <summary>
    /// Evaluates a path with a caller-supplied binary probe, so the signer and
    /// metadata rules can be exercised without a filesystem or a signed binary.
    /// </summary>
    /// <remarks>
    /// The probe override is still bounded and cached by the engine exactly like the
    /// real probe, so a test cannot make the binary detectors behave differently from
    /// production beyond the values it supplies.
    /// </remarks>
    public static LocationAttribution EvaluateWithProbe(
        string path,
        IReadOnlyList<AppIdentity> apps,
        Func<string, ExecutableProbe> probeOverride,
        LocationCategory category = LocationCategory.ProgramFiles,
        IReadOnlyList<OwnedAncestor>? ownedAncestors = null)
    {
        var engine = new AttributionEngine(
            apps,
            new AttributionOptions
            {
                MaxExecutableProbes = 8,
                ProbeOverride = probeOverride,
                PackageDataRoot = PackageDataRoot,
            });
        var normalized = TextNormalizer.NormalizePath(path);

        return engine.Evaluate(new AttributionInput
        {
            Path = path,
            NormalizedPath = normalized,
            DirectoryName = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar)),
            Category = category,
            OwnedAncestors = ownedAncestors ?? [],
            NormalizedScanRoot = ScanRootFor(normalized, category),
            Depth = DepthBelowRoot(normalized, category),
        });
    }

    /// <summary>
    /// Evaluates a path as the child of an ancestor whose ownership is established.
    /// </summary>
    /// <remarks>
    /// This is what the scanner does for real: it carries an ownership assertion
    /// down so descendants do not each have to rediscover the same owner from their
    /// own names.
    /// </remarks>
    public static LocationAttribution EvaluateDescendant(
        string path,
        IReadOnlyList<AppIdentity> apps,
        string ownedAncestorPath,
        string ownedAncestorAppId,
        LocationCategory category = LocationCategory.LocalAppData,
        Classification ancestorClassification = Classification.Confirmed,
        string? packageDataRoot = null)
        => Evaluate(
            path,
            apps,
            category,
            acceptedAncestors: [ownedAncestorPath],
            ownedAncestors:
            [
                new OwnedAncestor(
                    TextNormalizer.NormalizePath(ownedAncestorPath),
                    ownedAncestorAppId,
                    ancestorClassification),
            ],
            packageDataRoot: packageDataRoot);

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

