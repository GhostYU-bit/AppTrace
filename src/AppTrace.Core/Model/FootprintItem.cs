namespace AppTrace.Core.Model;

/// <summary>
/// How an application relates to a filesystem location.
/// </summary>
/// <remarks>
/// <para>Two relations, deliberately no more. The distinction Task 06 exists to
/// draw is between <em>who is responsible for these bytes</em> and <em>what other
/// application the content is about</em>:</para>
/// <list type="bullet">
/// <item><b>Owns</b> — the application is responsible for this path. Only an
/// <c>Owns</c> assertion may receive exclusive bytes.</item>
/// <item><b>RelatedTo</b> — the content of this path concerns that application.
/// The path stays owned by whoever owns it, and the related application receives no
/// bytes at all.</item>
/// </list>
/// <para>A richer vocabulary (<c>GeneratedFor</c>, <c>CacheOf</c>,
/// <c>DependencyOf</c>, <c>UsedBy</c>, <c>InstalledBy</c>, <c>ProducedBy</c>,
/// <c>Contains</c>) would be more expressive and is deliberately not implemented:
/// nothing in the evidence can reliably tell those apart yet, and a relation the
/// evidence cannot support is a claim AppTrace must not make.</para>
/// </remarks>
public enum CandidateRelation
{
    /// <summary>The application is responsible for this path and its bytes.</summary>
    Owns = 0,

    /// <summary>
    /// The path's content concerns this application, which does not own it. A
    /// <c>RelatedTo</c> assertion never receives exclusive bytes.
    /// </summary>
    RelatedTo,
}

/// <summary>
/// One claimed relationship between a filesystem location and an application,
/// together with the evidence for exactly that claim.
/// </summary>
/// <remarks>
/// <para>A location may carry several candidates. Evidence is never hoisted to the
/// location level, because the same evidence rarely supports every candidate
/// equally: a directory named <c>Acrobat</c> under <c>Adobe</c> is strong evidence
/// for Acrobat and only namespace evidence for Photoshop.</para>
/// <para><b>Relationship evidence is kept strictly apart from ownership
/// evidence.</b> <see cref="RelationshipEvidence"/> never participates in
/// <see cref="Score"/>, so a subject name found inside an owned tree cannot add its
/// way to ownership of that tree. The two are separate lists for exactly that
/// reason, not for presentation.</para>
/// </remarks>
public sealed record CandidateOwner
{
    public required string AppId { get; init; }

    /// <summary>Denormalized display name, so JSON output stays readable on its own.</summary>
    public string? AppDisplayName { get; init; }

    /// <summary>How this application relates to the location.</summary>
    public CandidateRelation Relation { get; init; } = CandidateRelation.Owns;

    public IReadOnlyList<Evidence> Evidence { get; init; } = [];

    /// <summary>
    /// Evidence that this application's content is <em>about</em> the location,
    /// without owning it. Never scored, and never able to make the candidate an
    /// owner.
    /// </summary>
    public IReadOnlyList<Evidence> RelationshipEvidence { get; init; } = [];

    /// <summary>
    /// Internal score derived from <see cref="Evidence"/>. Reported for
    /// diagnostics only; consumers should use <see cref="Classification"/>.
    /// </summary>
    public int Score { get; init; }

    /// <summary>
    /// True when this candidate is one of the owners AppTrace actually stands
    /// behind. Locations can have zero, one, or several accepted owners. A
    /// <see cref="CandidateRelation.RelatedTo"/> candidate is never accepted as an
    /// owner.
    /// </summary>
    public bool Accepted { get; init; }

    public Classification Classification { get; init; } = Classification.Unknown;

    /// <summary>True when this candidate asserts ownership rather than relatedness.</summary>
    public bool Owns => Relation == CandidateRelation.Owns;

    public IEnumerable<Evidence> Supporting => Evidence.Where(e => e.SupportsAttribution);

    public IEnumerable<Evidence> Contradicting => Evidence.Where(e => !e.SupportsAttribution);

    public override string ToString()
        => Relation == CandidateRelation.Owns
            ? $"{AppDisplayName ?? AppId} ({Classification.Symbol()}, score {Score})"
            : $"{AppDisplayName ?? AppId} (RELATED_TO)";
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
        => CandidateOwners.Where(c => c.Accepted && c.Owns).ToArray();

    /// <summary>
    /// Applications whose content this location is about, without owning it.
    /// </summary>
    /// <remarks>
    /// These applications receive no bytes. The relationship is an interpretation
    /// of the location, not a second claim on it.
    /// </remarks>
    public IReadOnlyList<CandidateOwner> RelatedApplications
        => CandidateOwners.Where(c => c.Relation == CandidateRelation.RelatedTo).ToArray();

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
