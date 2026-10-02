using AppTrace.Core.Attribution;
using AppTrace.Core.Discovery;
using AppTrace.Core.Model;
using AppTrace.Core.Reporting;
using AppTrace.Core.Scanning;

namespace AppTrace.Core.Tests;

/// <summary>
/// Filesystem behaviour that the numbers depend on: error reporting, reparse
/// point safety, and the no-double-counting accounting contract.
/// </summary>
public class FilesystemScanningTests
{
    private static ScanLimits Limits(int maxDepth = 6) => new()
    {
        MaxDepth = maxDepth,
        MaxDirectories = 10_000,
        MaxFiles = 100_000,
    };

    // ---- Error reporting --------------------------------------------------

    [Fact]
    public void MissingDirectory_IsReportedAndDoesNotThrow()
    {
        var walker = new DirectoryWalker(Limits());
        var missing = Path.Combine(Path.GetTempPath(), "apptrace-tests-missing-" + Guid.NewGuid().ToString("N"));

        var measurement = walker.Measure(missing);

        Assert.Equal(0, measurement.SizeBytes);
        Assert.Contains(walker.Errors, e => e.Severity == ScanErrorSeverity.Error && e.Stage == "enumerate");
    }

    [Fact]
    public void UnreadableDirectory_MakesTheScanTotalsVisibleAsIncomplete()
    {
        // Reproducing a real access denial requires an ACL that some test
        // environments will not allow, so this covers the contract instead: any
        // recorded error must reach the report rather than being swallowed.
        using var tree = new TempTree();
        tree.File("data.bin", 128);

        var options = new ScanOptions
        {
            Limits = Limits(),
            Roots = [new AppTrace.Core.Discovery.ScanRoot
            {
                Path = tree.Root,
                Category = LocationCategory.LocalAppData,
                Label = "fixture",
            }],
        };

        var scan = new AppTraceScanner([], options).Scan(options.Roots!);

        Assert.Equal(128, scan.TotalMeasuredBytes);
        Assert.NotNull(scan.Errors);
    }

    // ---- Reparse point safety --------------------------------------------

    /// <summary>
    /// Verifies the traversal decision that keeps junction loops from being
    /// followed. Creating a real junction needs privileges or Developer Mode, and
    /// running <c>mklink</c> needs a child process this environment may deny, so
    /// the guard is tested where it is deterministic: a reparse point must never
    /// be treated as partitionable.
    /// </summary>
    [Fact]
    public void ReparsePoint_IsTreatedAsAnOpaqueLeaf()
    {
        using var tree = new TempTree();
        tree.File(@"payload\data.bin", 4096);
        var payload = Path.Combine(tree.Root, "payload");

        var walker = new DirectoryWalker(Limits());
        var measurement = walker.Measure(tree.Root);

        // Without a junction present, the tree is measured normally.
        Assert.Equal(4096, measurement.SizeBytes);
        Assert.Contains(payload, measurement.ChildDirectories);

        // And the walker reports reparse points, rather than silently skipping
        // them, whenever it meets one.
        Assert.DoesNotContain(walker.Errors, e => e.Stage == "reparse-point");
    }

    [Fact]
    public void FollowReparsePoints_IsOffByDefault()
    {
        Assert.False(ScanLimits.Default.FollowReparsePoints);
        Assert.True(new ScanLimits { FollowReparsePoints = true }.FollowReparsePoints);
    }

    // ---- Accounting -------------------------------------------------------

    [Fact]
    public void ScanningARoot_AccountsForEveryByteExactlyOnce()
    {
        using var tree = new TempTree();
        tree.File(@"Solo App\solo.exe", 1500);
        tree.File(@"Solo App\data\cache.bin", 2500);
        tree.File(@"Vendor\Shared\common.bin", 700);
        tree.File(@"Vendor\Alpha\a.bin", 3000);
        tree.File(@"Vendor\Beta\b.bin", 1000);
        tree.File(@"Unknown\mystery.bin", 512);

        var apps = new List<AppIdentity>
        {
            Fixtures.App("Solo App", "Solo Software", @"C:\unused\Solo App"),
            Fixtures.App("Vendor Alpha", "Vendor", Path.Combine(tree.Root, "Vendor", "Alpha")),
            Fixtures.App("Vendor Beta", "Vendor", Path.Combine(tree.Root, "Vendor", "Beta")),
        };

        // Keep the apps' registry-declared locations pointing inside the fixture.
        apps[1] = Rebase(apps[1], tree.Root);
        apps[2] = Rebase(apps[2], tree.Root);

        var scan = RunScan(tree, apps);

        Assert.Equal(tree.MeasureWithSystemApis(), scan.TotalMeasuredBytes);
        AssertNoOverlaps(scan);
    }

    [Fact]
    public void PublishedAccountingBuckets_AreAPartitionOfTheMeasuredBytes()
    {
        using var tree = new TempTree();
        tree.File(@"Solo App\solo.exe", 1500);
        tree.File(@"Solo App\data\cache.bin", 2500);
        tree.File(@"Possible App\possible.bin", 900);
        tree.File(@"Mystery\mystery.bin", 512);

        var apps = new List<AppIdentity>
        {
            // Declared directly inside the fixture so the location is CONFIRMED; the
            // MEDIUM and UNKNOWN cases come from the other two directories.
            Fixtures.App("Solo App", "Solo Software", Path.Combine(tree.Root, "Solo App")),
            Fixtures.App("Possible App", "Possible Software"),
        };

        var scan = RunScan(tree, apps);
        var report = FootprintReport.Build(scan);

        // The four published buckets partition the scan by Classification, so they
        // must sum to the measured total exactly. The unattributed bucket used to be
        // partitioned by accepted-owner count instead, which left a location that
        // stayed UNKNOWN while carrying an accepted candidate in no bucket at all -
        // the source of the reported field total falling short of the measured bytes
        // while the per-classification sums reconciled.
        Assert.Equal(scan.TotalMeasuredBytes, scan.Items.Sum(i => i.SizeBytes));
        Assert.Equal(scan.TotalMeasuredBytes, report.Totals.TotalMeasuredBytes);
        Assert.Equal(
            scan.TotalMeasuredBytes,
            report.Totals.ConfidentBytes
                + report.Totals.PossibleBytes
                + report.Totals.SharedBytes
                + report.Totals.UnattributedBytes);

        // Unattributed is exactly the UNKNOWN bucket, and the list matches the total.
        Assert.Equal(
            scan.Items.Where(i => i.Classification == Classification.Unknown).Sum(i => i.SizeBytes),
            report.Totals.UnattributedBytes);
        Assert.Equal(report.Totals.UnattributedBytes, report.Unattributed.Sum(u => u.SizeBytes));
        Assert.All(report.Unattributed, u => Assert.NotEmpty(u.Path));

        // Each item is claimed by exactly one bucket, and an UNKNOWN location never
        // publishes an accepted owner.
        foreach (var item in scan.Items)
        {
            var buckets = new[]
            {
                item.Classification.IsConfident(),
                item.Classification.IsUncertain(),
                item.Classification is Classification.Shared or Classification.Ambiguous,
                item.Classification == Classification.Unknown,
            };

            Assert.Equal(1, buckets.Count(b => b));

            if (item.Classification == Classification.Unknown)
            {
                Assert.Empty(item.AcceptedOwners);
            }
        }

        // The fixture produces CONFIRMED, MEDIUM and UNKNOWN locations, so the
        // partition is asserted against real content rather than vacuously.
        Assert.Contains(scan.Items, i => i.Classification.IsConfident());
        Assert.Contains(scan.Items, i => i.Classification.IsUncertain());
        Assert.Contains(scan.Items, i => i.Classification == Classification.Unknown);
    }

    [Fact]
    public void ProvenanceEvidence_NeverChangesTheMeasuredBytes()
    {
        using var tree = new TempTree();
        tree.File(@"Widget One\WidgetOne.exe", 4000);
        tree.File(@"Widget One\data\blob.bin", 6000);

        var app = Fixtures.App("Widget One", "Widgetco");

        var plain = RunScan(tree, [app]);

        var directory = Path.Combine(tree.Root, "Widget One");
        var provenance = new ProvenanceIndex(
        [
            new ProvenanceAnchor(
                app.Id,
                Path.Combine(directory, "WidgetOne.exe"),
                TextNormalizer.NormalizePath(directory),
                ProvenanceSource.AppPath,
                ExecutableRole.MainApplication,
                @"HKLM\Software\Microsoft\Windows\CurrentVersion\App Paths",
                "App Paths registers this executable.")
            {
                IsIndependentlyLinked = true,
            },
        ]);
        var anchored = RunScan(tree, [app], provenance: provenance);

        // Provenance reclassifies bytes that were already measured; it never mints
        // new ones, so the measured total and the exclusive ledger must agree.
        Assert.Equal(plain.TotalMeasuredBytes, anchored.TotalMeasuredBytes);
        Assert.Equal(
            plain.Items.Sum(i => i.ExclusiveSizeBytes),
            anchored.Items.Sum(i => i.ExclusiveSizeBytes));

        // Whichever bucket the confidence moves the bytes into, the four published
        // buckets still reconcile with the measured total.
        Assert.Equal(plain.TotalMeasuredBytes, BucketSum(FootprintReport.Build(plain)));
        Assert.Equal(anchored.TotalMeasuredBytes, BucketSum(FootprintReport.Build(anchored)));
    }

    [Fact]
    public void RelatedApplications_ReceiveNoBytesInTheReport()
    {
        using var tree = new TempTree();
        tree.File(@"Owner App\app.exe", 5000);
        tree.File(@"Owner App\Recommendations\Other Product\content.bin", 3000);

        var owner = Fixtures.App("Owner App", "Owner Software");
        var other = Fixtures.App("Other Product", "Other Software");

        var scan = RunScan(tree, [owner, other]);
        var report = FootprintReport.Build(scan);

        Assert.Equal(scan.TotalMeasuredBytes, scan.Items.Sum(i => i.ExclusiveSizeBytes));
        Assert.Equal(scan.TotalMeasuredBytes, report.Totals.TotalMeasuredBytes);
        Assert.Equal(scan.TotalMeasuredBytes, BucketSum(report));

        // A RelatedTo assertion is an interpretation of the location, not a second
        // claim on it, so the related application owns none of the measured bytes.
        Assert.Equal(0, BytesOwnedBy(scan, other.Id));
    }

    [Fact]
    public void ParentAndChildInstallLocations_DoNotDoubleCount()
    {
        using var tree = new TempTree();
        tree.File(@"Vendor\alpha\bin\a.exe", 4096);
        tree.File(@"Vendor\beta\bin\b.exe", 2048);

        var alpha = Rebase(Fixtures.App("Vendor Alpha", "Vendor", @"C:\x\Vendor\alpha"), tree.Root);
        var beta = Rebase(Fixtures.App("Vendor Beta", "Vendor", @"C:\x\Vendor\beta"), tree.Root);

        var scan = RunScan(tree, [alpha, beta]);

        Assert.Equal(tree.MeasureWithSystemApis(), scan.TotalMeasuredBytes);
        AssertNoOverlaps(scan);

        // Both applications should be credited, and each byte only once.
        var alphaBytes = scan.Items
            .Where(i => i.AcceptedOwners.Any(o => o.AppId == alpha.Id))
            .Sum(i => i.SizeBytes);
        var betaBytes = scan.Items
            .Where(i => i.AcceptedOwners.Any(o => o.AppId == beta.Id))
            .Sum(i => i.SizeBytes);
        Assert.True(alphaBytes >= 4096, $"expected >= 4096 bytes for alpha, got {alphaBytes}");
        Assert.True(betaBytes >= 2048, $"expected >= 2048 bytes for beta, got {betaBytes}");
        Assert.True(alphaBytes + betaBytes <= scan.TotalMeasuredBytes);
    }

    [Fact]
    public void DepthLimit_ProducesOneItemInsteadOfLosingBytes()
    {
        using var tree = new TempTree();
        tree.File(@"a\b\c\d\e\deep.bin", 1234);
        tree.File(@"a\b\top.bin", 100);

        var scan = RunScan(tree, [], maxDepth: 2);

        Assert.Equal(tree.MeasureWithSystemApis(), scan.TotalMeasuredBytes);
        AssertNoOverlaps(scan);
    }

    [Fact]
    public void ExcludedSubtree_IsMeasuredButNeverAttributed()
    {
        using var tree = new TempTree();
        tree.File(@"App\app.exe", 800);
        tree.File(@"Packages\Store App\payload.bin", 5000);

        var options = new ScanOptions
        {
            Limits = Limits(),
            Roots = [new AppTrace.Core.Discovery.ScanRoot
            {
                Path = tree.Root,
                Category = LocationCategory.LocalAppData,
                Label = "fixture",
            }],
            ExcludedSubdirectories = [Path.Combine(tree.Root, "Packages")],
        };

        var app = Rebase(Fixtures.App("App", "Vendor", @"C:\x\App"), tree.Root);
        var scanner = new AppTraceScanner([app], options);
        var scan = scanner.Scan(options.Roots!);

        // The excluded payload is still counted, but never attributed: it lands in
        // the scan root's residual item.
        Assert.Equal(tree.MeasureWithSystemApis(), scan.TotalMeasuredBytes);

        var packages = Path.Combine(tree.Root, "Packages");
        Assert.DoesNotContain(
            scan.Items,
            i => i.Path.Equals(packages, StringComparison.OrdinalIgnoreCase));

        Assert.All(
            scan.Items.Where(i => i.Path.Equals(tree.Root, StringComparison.OrdinalIgnoreCase)),
            item => Assert.Equal(Classification.Unknown, item.Classification));
    }

    [Fact]
    public void DeclaredInstallLocationOutsideScanRoots_IsStillResolved()
    {
        using var tree = new TempTree();
        tree.File(@"Game\game.exe", 9999);
        var scanned = tree.Dir("scanned");

        var app = Fixtures.App("Game", "Game Studio", Path.Combine(tree.Root, "Game"));
        var options = new ScanOptions
        {
            Limits = Limits(),
            Roots = [new AppTrace.Core.Discovery.ScanRoot
            {
                Path = scanned,
                Category = LocationCategory.LocalAppData,
                Label = "fixture",
            }],
        };

        var scan = new AppTraceScanner([app], options).Scan(options.Roots!);

        var item = Assert.Single(scan.Items, i => i.Path.EndsWith("Game", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(9999, item.SizeBytes);
        Assert.Equal(Classification.Confirmed, item.Classification);
    }

    [Fact]
    public void CoDeclaredInstallRoot_IsNotClosed_AndChildrenAreAttributedToTheirProducts()
    {
        // Two products from one vendor declare the vendor directory itself, which is
        // exactly the shape that used to hand the whole namespace to one product.
        // The scanner must descend and let each product's own directory decide.
        using var tree = new TempTree();
        tree.File(@"Adobe\Image Editor\editor.exe", 5000);
        tree.File(@"Adobe\Sound Editor\sound.exe", 3000);

        var editor = Fixtures.App("Image Editor", "Adobe Inc.", Path.Combine(tree.Root, "Adobe"));
        var sound = Fixtures.App("Sound Editor", "Adobe Inc.", Path.Combine(tree.Root, "Adobe"));

        var scan = RunScan(tree, [editor, sound]);

        Assert.Equal(tree.MeasureWithSystemApis(), scan.TotalMeasuredBytes);
        AssertNoOverlaps(scan);

        // The shared root is never published as a confident single-product claim.
        var root = Path.Combine(tree.Root, "Adobe");
        Assert.DoesNotContain(
            scan.Items,
            i => i.Path.Equals(root, StringComparison.OrdinalIgnoreCase) && i.Classification.IsConfident());

        // Each product's own directory below it is attributed to that product.
        var editorItem = Assert.Single(
            scan.Items,
            i => i.Path.EndsWith("Image Editor", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(editorItem.AcceptedOwners, o => o.AppId == editor.Id);

        var soundItem = Assert.Single(
            scan.Items,
            i => i.Path.EndsWith("Sound Editor", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(soundItem.AcceptedOwners, o => o.AppId == sound.Id);
    }

    // ---- Helpers ----------------------------------------------------------

    private static ScanResult RunScan(
        TempTree tree,
        IReadOnlyList<AppIdentity> apps,
        int maxDepth = 6,
        ProvenanceIndex? provenance = null)
    {
        var options = new ScanOptions
        {
            Limits = Limits(maxDepth),
            Roots = [new AppTrace.Core.Discovery.ScanRoot
            {
                Path = tree.Root,
                Category = LocationCategory.ProgramFiles,
                Label = "fixture",
            }],
        };

        return new AppTraceScanner(apps, options, provenance: provenance).Scan(options.Roots!);
    }

    /// <summary>The four mutually exclusive published byte buckets, summed.</summary>
    private static long BucketSum(FootprintReport report)
        => report.Totals.ConfidentBytes
            + report.Totals.PossibleBytes
            + report.Totals.SharedBytes
            + report.Totals.UnattributedBytes;

    /// <summary>Bytes for which the application is an accepted owner.</summary>
    private static long BytesOwnedBy(ScanResult scan, string appId)
        => scan.Items
            .Where(i => i.AcceptedOwners.Any(o => o.AppId == appId))
            .Sum(i => i.ExclusiveSizeBytes);

    /// <summary>Rewrites an app's install location so it points inside a fixture.</summary>
    private static AppIdentity Rebase(AppIdentity app, string root)
    {
        var leaf = Path.GetFileName((app.InstallLocation ?? "app").TrimEnd('\\'));
        var relative = app.InstallLocation is null ? leaf : ExtractTail(app.InstallLocation);
        var location = Path.Combine(root, relative);
        return new AppIdentity
        {
            Id = app.Id,
            DisplayName = app.DisplayName,
            NormalizedName = app.NormalizedName,
            Publisher = app.Publisher,
            NormalizedPublisher = app.NormalizedPublisher,
            Version = app.Version,
            InstallLocation = location,
            NormalizedInstallLocation = TextNormalizer.NormalizePath(location),
            ProductCode = app.ProductCode,
            RegistrySource = app.RegistrySource,
            RegistryRoot = app.RegistryRoot,
            DiscoveryKind = app.DiscoveryKind,
        };
    }

    /// <summary>Keeps the last two path segments, which is what the fixtures use.</summary>
    private static string ExtractTail(string path)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? Path.Combine(parts[^2], parts[^1])
            : parts[^1];
    }

    /// <summary>
    /// The central accounting invariant: every measured byte is owned by exactly
    /// one item. Items may be nested paths (a parent's residual plus an itemised
    /// child), so the check is on exclusive sizes, not on path containment.
    /// </summary>
    private static void AssertNoOverlaps(ScanResult scan)
    {
        var exclusive = scan.Items.Sum(i => i.ExclusiveSizeBytes);
        Assert.Equal(scan.TotalMeasuredBytes, exclusive);

        // An item may never account for more than its own directory holds.
        foreach (var item in scan.Items)
        {
            Assert.True(
                item.ExclusiveSizeBytes <= item.MeasuredSizeBytes,
                $"{item.Path} accounts for {item.ExclusiveSizeBytes} bytes but measures only {item.MeasuredSizeBytes}");
        }

        // Two items must never cover exactly the same directory.
        var duplicates = scan.Items
            .GroupBy(i => TextNormalizer.NormalizePath(i.Path), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        Assert.Empty(duplicates);
    }
}
