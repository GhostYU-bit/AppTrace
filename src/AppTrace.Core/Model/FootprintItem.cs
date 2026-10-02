namespace AppTrace.Core.Model;

/// <summary>
/// One claimed ownership relationship between a filesystem location and an
/// application, together with the evidence for exactly that claim.
/// </summary>
/// <remarks>
/// A location may carry several candidates. Evidence is never hoisted to the
/// location level, because the same evidence rarely supports every candidate
/// equally: a directory named <c>Acrobat</c> under <c>Adobe</c> is strong
/// evidence for Acrobat and only namespace evidence for Photoshop.
/// </remarks>
public sealed record CandidateOwner
{
    public required string AppId { get; init; }

    /// <summary>Denormalized display name, so JSON output stays readable on its own.</summary>
    public string? AppDisplayName { get; init; }

    public IReadOnlyList<Evidence> Evidence { get; init; } = [];

    /// <summary>
    /// Internal score derived from <see cref="Evidence"/>. Reported for
    /// diagnostics only; consumers should use <see cref="Classification"/>.
    /// </summary>
    public int Score { get; init; }

    /// <summary>
    /// True when this candidate is one of the owners AppTrace actually stands
    /// behind. Locations can have zero, one, or several accepted owners.
    /// </summary>
    public bool Accepted { get; init; }

    public Classification Classification { get; init; } = Classification.Unknown;

    public IEnumerable<Evidence> Supporting => Evidence.Where(e => e.SupportsAttribution);

    public IEnumerable<Evidence> Contradicting => Evidence.Where(e => !e.SupportsAttribution);

    public override string ToString()
        => $"{AppDisplayName ?? AppId} ({Classification.Symbol()}, score {Score})";
}

/// <summary>
/// A scanned filesystem location with its size, candidates and classification.
/// </summary>
/// <remarks>
/// Accounting rules that make double counting impossible:
/// <list type="bullet">
/// <item><see cref="SizeBytes"/> is the recursive size of the entire directory,
/// and ownership of that subtree belongs to this item.</item>
/// <item><see cref="ExclusiveSizeBytes"/> is the part of that subtree not
/// itemised anywhere else. The sum of <see cref="ExclusiveSizeBytes"/> over all
/// items equals the total number of bytes the scan measured.</item>
/// <item>A child location is only emitted as a separate item when the parent's
/// ownership could not be established. A parent's residual and its itemised
/// children are therefore nested paths but disjoint byte ranges, and a directory
/// AppTrace stops at is never expanded again.</item>
/// </list>
/// </remarks>
public sealed class FootprintItem
{
    /// <summary>Absolute path of this location.</summary>
    public required string Path { get; init; }

    public LocationCategory Category { get; init; } = LocationCategory.Unknown;

    /// <summary>
    /// Bytes attributable to this location, excluding any emitted descendant
    /// location that is accounted for separately.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Recursive size of the directory as measured, before accounting. For a
    /// directory that was partitioned, this is larger than
    /// <see cref="SizeBytes"/>; use <see cref="MeasuredSizeBytes"/> for "how big
    /// is this directory on disk?" and <see cref="SizeBytes"/> for "how many
    /// bytes does this item account for?".
    /// </summary>
    public long MeasuredSizeBytes { get; init; }

    /// <summary>
    /// The bytes this item is the sole owner of. Unlike <see cref="SizeBytes"/>
    /// (which is exclusive by construction), this field makes the accounting
    /// contract explicit and directly checkable.
    /// </summary>
    public long ExclusiveSizeBytes => SizeBytes;

    /// <summary>Number of files measured beneath this location, including descendants.</summary>
    public long FileCount { get; init; }

    /// <summary>Depth relative to the root that was scanned (root children are depth 1).</summary>
    public int Depth { get; init; }

    public IReadOnlyList<CandidateOwner> CandidateOwners { get; init; } = [];

    /// <summary>
    /// Classification accepted for this location. For shared or ambiguous
    /// locations this describes the location, not a single owner.
    /// </summary>
    public Classification Classification { get; init; } = Classification.Unknown;

    /// <summary>
    /// Why AppTrace stopped its recursive attribution at this path. Null when
    /// the location was a leaf of the scan scope.
    /// </summary>
    public string? StopReason { get; init; }

    /// <summary>
    /// Set when no application-specific owner was accepted for this location, so
    /// the reason is stated rather than implied by an empty candidate list.
    /// </summary>
    public string? UnattributedReason { get; init; }

    /// <summary>Links this item to its parent item, when one was emitted.</summary>
    public string? ParentPath { get; init; }

    public IReadOnlyList<ScanError> Errors { get; init; } = [];

    public IReadOnlyList<CandidateOwner> AcceptedOwners
        => CandidateOwners.Where(c => c.Accepted).ToArray();

    /// <summary>Primary accepted owner, when there is exactly one.</summary>
    public CandidateOwner? PrimaryOwner
    {
        get
        {
            var accepted = AcceptedOwners;
            return accepted.Count == 1 ? accepted[0] : null;
        }
    }

    public string DirectoryName
    {
        get
        {
            var name = System.IO.Path.GetFileName(
                Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? Path : name;
        }
    }

    public override string ToString() => $"{Path} [{Classification.Symbol()}]";
}
