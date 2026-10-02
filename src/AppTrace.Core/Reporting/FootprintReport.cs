namespace AppTrace.Core.Reporting;

using AppTrace.Core.Model;
using AppTrace.Core.Scanning;

/// <summary>A single attributed location inside an application's report.</summary>
public sealed class AppFootprintLocation
{
    public required FootprintItem Item { get; init; }

    /// <summary>Owner records that AppTrace accepted for this location.</summary>
    public required IReadOnlyList<CandidateOwner> Owners { get; init; }
}

/// <summary>The aggregated footprint of one installed application.</summary>
public sealed class AppFootprint
{
    public required AppIdentity App { get; init; }

    public IReadOnlyList<AppFootprintLocation> Locations { get; init; } = [];

    /// <summary>Bytes in locations classified CONFIRMED or HIGH.</summary>
    public long ConfidentBytes => Sum(l => l.Item.Classification.IsConfident());

    /// <summary>Bytes in locations classified MEDIUM or LOW.</summary>
    public long PossibleBytes => Sum(l => l.Item.Classification.IsUncertain());

    /// <summary>Bytes in locations that are shared with other applications.</summary>
    public long SharedBytes => Sum(l => l.Item.Classification == Classification.Shared);

    /// <summary>Bytes in locations whose ownership could not be decided.</summary>
    public long AmbiguousBytes => Sum(l => l.Item.Classification == Classification.Ambiguous);

    public long TotalAttributedBytes => ConfidentBytes + PossibleBytes + SharedBytes + AmbiguousBytes;

    private long Sum(Func<AppFootprintLocation, bool> predicate)
        => Locations.Where(predicate).Sum(l => l.Item.SizeBytes);

    public bool HasAnyFootprint => Locations.Count > 0;
}

/// <summary>A location that no installed application could be shown to own.</summary>
public sealed class UnattributedLocation
{
    public required string Path { get; init; }

    public required long SizeBytes { get; init; }

    public required LocationCategory Category { get; init; }

    public string? Reason { get; init; }
}

/// <summary>Scan totals, deliberately separated by certainty.</summary>
public sealed class FootprintTotals
{
    public long ConfidentBytes { get; init; }

    public long PossibleBytes { get; init; }

    public long SharedBytes { get; init; }

    public long UnattributedBytes { get; init; }

    public long TotalMeasuredBytes => ConfidentBytes + PossibleBytes + SharedBytes + UnattributedBytes;
}

/// <summary>
/// Turns a raw <see cref="ScanResult"/> into the app-centric view AppTrace is
/// about: per-application footprints, plus the totals that make clear how much
/// of the disk the scan could actually explain.
/// </summary>
public sealed class FootprintReport
{
    private FootprintReport()
    {
    }

    public required ScanResult Scan { get; init; }

    public IReadOnlyList<AppFootprint> Applications { get; init; } = [];

    public IReadOnlyList<UnattributedLocation> Unattributed { get; init; } = [];

    public FootprintTotals Totals { get; init; } = new();

    /// <summary>Applications with no attributable location at all.</summary>
    public IReadOnlyList<AppIdentity> ApplicationsWithoutFootprint { get; init; } = [];

    public static FootprintReport Build(ScanResult scan)
    {
        var byApp = new Dictionary<string, List<AppFootprintLocation>>(StringComparer.Ordinal);
        var unattributed = new List<UnattributedLocation>();

        foreach (var item in scan.Items)
        {
            // The published accounting buckets partition the scan by
            // Classification: CONFIRMED/HIGH, MEDIUM/LOW, SHARED/AMBIGUOUS and
            // UNKNOWN. Unattributed storage is therefore the UNKNOWN bucket, and
            // nothing else. Partitioning it by accepted-owner count instead left
            // every location that stayed UNKNOWN while carrying an accepted but
            // ungraded candidate in no bucket at all — which is why the published
            // totals summed to less than the measured bytes while the
            // per-classification sums reconciled exactly.
            if (item.Classification == Classification.Unknown)
            {
                unattributed.Add(new UnattributedLocation
                {
                    Path = item.Path,
                    SizeBytes = item.SizeBytes,
                    Category = item.Category,
                    Reason = item.UnattributedReason ?? item.StopReason,
                });
                continue;
            }

            var owners = item.AcceptedOwners;
            foreach (var owner in owners)
            {
                if (!byApp.TryGetValue(owner.AppId, out var list))
                {
                    list = [];
                    byApp[owner.AppId] = list;
                }

                list.Add(new AppFootprintLocation { Item = item, Owners = owners });
            }
        }

        var apps = new List<AppFootprint>();
        var withoutFootprint = new List<AppIdentity>();
        foreach (var app in scan.Applications)
        {
            if (byApp.TryGetValue(app.Id, out var locations))
            {
                apps.Add(new AppFootprint
                {
                    App = app,
                    Locations = locations
                        .OrderByDescending(l => l.Item.Classification.IsConfident())
                        .ThenByDescending(l => l.Item.SizeBytes)
                        .ToArray(),
                });
            }
            else
            {
                withoutFootprint.Add(app);
            }
        }

        return new FootprintReport
        {
            Scan = scan,
            Applications = apps
                .OrderByDescending(a => a.ConfidentBytes)
                .ThenByDescending(a => a.TotalAttributedBytes)
                .ThenBy(a => a.App.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Unattributed = unattributed.OrderByDescending(u => u.SizeBytes).ToArray(),
            ApplicationsWithoutFootprint = withoutFootprint,
            Totals = new FootprintTotals
            {
                // These are whole-disk accounting figures, so they are computed
                // from the item list (one entry per byte) rather than by summing
                // the per-application views, which would count a shared location
                // once for every owner.
                //
                // The four buckets are a partition of Classification and must
                // therefore sum to TotalMeasuredBytes exactly:
                //   Confirmed | High          -> confident
                //   Medium | Low              -> possible
                //   Shared | Ambiguous        -> shared
                //   Unknown                   -> unattributed
                ConfidentBytes = scan.Items.Where(i => i.Classification.IsConfident()).Sum(i => i.SizeBytes),
                PossibleBytes = scan.Items.Where(i => i.Classification.IsUncertain()).Sum(i => i.SizeBytes),
                SharedBytes = scan.Items
                    .Where(i => i.Classification is Classification.Shared or Classification.Ambiguous)
                    .Sum(i => i.SizeBytes),
                UnattributedBytes = scan.Items
                    .Where(i => i.Classification == Classification.Unknown)
                    .Sum(i => i.SizeBytes),
            },
        };
    }

    /// <summary>
    /// Locations that are SHARED or AMBIGUOUS, surfaced separately because they
    /// are the cases where AppTrace refuses to pick a single owner.
    /// </summary>
    public IReadOnlyList<FootprintItem> AmbiguousLocations => Scan.Items
        .Where(i => i.Classification is Classification.Shared or Classification.Ambiguous)
        .OrderByDescending(i => i.SizeBytes)
        .ToArray();
}
