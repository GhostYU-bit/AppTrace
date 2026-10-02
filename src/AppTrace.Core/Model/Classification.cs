namespace AppTrace.Core.Model;

/// <summary>
/// The coarse, defensible confidence buckets AppTrace exposes.
/// </summary>
/// <remarks>
/// Phase 0 deliberately has no probabilistic model, so it never publishes a
/// percentage. The internal score exists only to order and bucket evidence; the
/// classification is what users and downstream consumers should rely on.
/// </remarks>
public enum Classification
{
    /// <summary>No usable attribution evidence at all.</summary>
    Unknown = 0,

    /// <summary>Weak, unconfirmed signals only. Treat as a hint, not a fact.</summary>
    Low,

    /// <summary>Plausible attribution supported by more than a name coincidence.</summary>
    Medium,

    /// <summary>Strong attribution; a false positive here should be rare.</summary>
    High,

    /// <summary>Proven by the application's own registration data.</summary>
    Confirmed,

    /// <summary>Several installed applications legitimately share this location.</summary>
    Shared,

    /// <summary>Ownership exists but is genuinely undecidable from available evidence.</summary>
    Ambiguous,
}

/// <summary>Ordinal buckets used for reporting and for the "unknown" fallback.</summary>
public static class ClassificationExtensions
{
    /// <summary>Single-character marker used by the CLI's tree view.</summary>
    public static string Symbol(this Classification classification) => classification switch
    {
        Classification.Confirmed => "CONFIRMED",
        Classification.High => "HIGH",
        Classification.Medium => "MEDIUM",
        Classification.Low => "LOW",
        Classification.Shared => "SHARED",
        Classification.Ambiguous => "AMBIGUOUS",
        _ => "UNKNOWN",
    };

    /// <summary>
    /// True when the classification is strong enough to be counted in the
    /// headline "confirmed / high" footprint total.
    /// </summary>
    public static bool IsConfident(this Classification classification)
        => classification is Classification.Confirmed or Classification.High;

    /// <summary>True when the location is counted as "possible additional footprint".</summary>
    public static bool IsUncertain(this Classification classification)
        => classification is Classification.Medium or Classification.Low;
}
