namespace AppTrace.Core.Scanning;

using AppTrace.Core.Discovery;
using AppTrace.Core.Model;

/// <summary>Options controlling a scan run.</summary>
public sealed class ScanOptions
{
    public ScanLimits Limits { get; init; } = ScanLimits.Default;

    /// <summary>Overrides the default well-known scan roots. Primarily for tests.</summary>
    public IReadOnlyList<ScanRoot>? Roots { get; init; }

    /// <summary>
    /// Subdirectories of the scan roots that are measured but never partitioned.
    /// The default excludes MSIX package payloads, which are opaque to Phase 0
    /// attribution and would otherwise dominate AppData.
    /// </summary>
    public IReadOnlyList<string> ExcludedSubdirectories { get; init; } = [];

    /// <summary>Restricts reporting to locations whose path contains this text.</summary>
    public string? PathFilter { get; init; }

    public static ScanOptions Default { get; } = new();

    /// <summary>Default exclusions applied on top of <see cref="ExcludedSubdirectories"/>.</summary>
    public static IReadOnlyList<string> DefaultExclusions(string localAppData)
        => [Path.Combine(localAppData, "Packages")];
}

/// <summary>The complete result of a scan run.</summary>
public sealed class ScanResult
{
    public required DateTimeOffset StartedAt { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>Every installed application AppTrace could identify.</summary>
    public IReadOnlyList<AppIdentity> Applications { get; init; } = [];

    /// <summary>Roots that were actually inspected.</summary>
    public IReadOnlyList<ScanRoot> Roots { get; init; } = [];

    /// <summary>
    /// One entry per accounted location. The sum of <see cref="FootprintItem.SizeBytes"/>
    /// across all items is the total number of bytes AppTrace measured, with no
    /// byte counted twice.
    /// </summary>
    public IReadOnlyList<FootprintItem> Items { get; init; } = [];

    /// <summary>Non-fatal problems; an incomplete scan is always visible here.</summary>
    public IReadOnlyList<ScanError> Errors { get; init; } = [];

    /// <summary>Total bytes measured across all accounted locations.</summary>
    public long TotalMeasuredBytes => Items.Sum(i => i.SizeBytes);

    public long TotalFileCount => Items.Sum(i => i.FileCount);

    /// <summary>True when a budget stopped the scan before it finished.</summary>
    public bool Truncated { get; init; }
}
