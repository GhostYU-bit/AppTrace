namespace AppTrace.Core.Model;

/// <summary>
/// Which question a piece of evidence answers.
/// </summary>
/// <remarks>
/// <para><b>The core V2 rule: evidence must earn the strength of the claim it
/// supports.</b> Phase 0 treated heterogeneous records as interchangeable values in
/// one additive score, which let <c>NormalizedNameMatch</c> (+30) and
/// <c>KnownApplicationPath</c> (+8) add up to a HIGH ownership claim even though
/// neither record independently shows that the application ever touched the path.
/// A kind makes that category error impossible: an <see cref="Identity"/> record
/// and a <see cref="Structure"/> record answer different questions and can no
/// longer be summed into a claim neither of them supports.</para>
/// <para>There is deliberately no <c>Accounting</c> kind. Measurement completeness
/// (inaccessible files, skipped reparse points, lower-bound sizes, scan errors) is
/// orthogonal to attribution and must never become ownership evidence.</para>
/// </remarks>
public enum EvidenceKind
{
    /// <summary>Answers: does this name or content identify Application X?</summary>
    Identity,

    /// <summary>
    /// Answers: is Application X independently anchored to this path or tree?
    /// Registration data the application itself wrote.
    /// </summary>
    Provenance,

    /// <summary>
    /// Answers: what does this path tell us about the storage structure? Describes
    /// the location, never the owner. <b>Structure does not establish ownership.</b>
    /// </summary>
    Structure,

    /// <summary>
    /// Answers: is this data owned by Application X, or merely about it? No detector
    /// emits this yet; the Owns/RelatedTo model arrives in a later task.
    /// </summary>
    Relationship,

    /// <summary>Answers: what argues against this ownership claim?</summary>
    Contradiction,
}

/// <summary>
/// How strongly a <see cref="EvidenceKind.Contradiction"/> record argues against a
/// claim.
/// </summary>
/// <remarks>
/// A decisive contradiction is a gate: it cannot be outvoted by any amount of weak
/// supporting evidence. A limiting one only caps confidence. Distinguishing the two
/// is the difference between "another application definitely owns this" and "this
/// disagrees, so be careful".
/// </remarks>
public enum ContradictionKind
{
    /// <summary>
    /// The record opposes the claim but is not proof on its own, so it caps
    /// confidence instead of forbidding ownership.
    /// </summary>
    Limiting,

    /// <summary>
    /// The record shows a differently identified application or the system itself
    /// owns this path. No accumulation of weak supporting evidence may override it.
    /// </summary>
    Decisive,
}

/// <summary>
/// The kind of signal a piece of evidence represents.
/// </summary>
/// <remarks>
/// This list is intentionally open-ended: new detectors add a member here and a
/// weight in <c>EvidenceWeights</c>. Nothing else in the pipeline needs to change,
/// because attribution is computed from <see cref="Evidence"/> records rather
/// than from hard-coded per-type branching.
/// </remarks>
public enum EvidenceType
{
    // ---- Supporting signals -------------------------------------------------

    /// <summary>Directory name equals the application name exactly.</summary>
    ExactDirectoryNameMatch,

    /// <summary>Directory name equals the normalized application name.</summary>
    NormalizedNameMatch,

    /// <summary>The application's publisher appears in the path.</summary>
    PublisherMatch,

    /// <summary>Path is (or is inside) the registry-declared install location.</summary>
    InstallLocationMatch,

    /// <summary>
    /// The path <em>is</em> the exact directory the application's own
    /// registration named as its install location.
    /// </summary>
    DeclaredInstallLocation,

    /// <summary>Executable metadata (ProductName/CompanyName/FileDescription) matched.</summary>
    ExecutableMetadataMatch,

    /// <summary>A registry value outside the uninstall keys references this path.</summary>
    RegistryReference,

    /// <summary>Path sits in a well-known per-application data location.</summary>
    KnownApplicationPath,

    /// <summary>
    /// The directory is a product-level boundary directly under a known
    /// application-data root (ProgramData, LocalAppData, Roaming or LocalLow), and
    /// its name specifically identifies the application. Task 07.6.
    /// </summary>
    /// <remarks>
    /// <para>What it proves: this is where an application's own data namespace
    /// begins, rather than a component of something else.</para>
    /// <para>What it does not prove: ownership. It is a
    /// <see cref="EvidenceKind.Structure"/> record worth nothing on its own, because
    /// a familiar-looking folder under a data root is not an owner. It makes the
    /// candidate-generation decision explicit and auditable; the identity and
    /// provenance records are what carry a claim.</para>
    /// </remarks>
    DataRootProductBoundary,

    /// <summary>Path sits under a vendor namespace that is typical for this publisher.</summary>
    KnownPublisherNamespace,

    /// <summary>The parent directory is attributed to the same application.</summary>
    ParentDirectoryMatch,

    /// <summary>A subdirectory of this location is attributed to the application.</summary>
    ChildDirectoryMatch,

    /// <summary>The path name or a registry field contains the product code.</summary>
    ProductCodeMatch,

    /// <summary>The identity was discovered from this exact location.</summary>
    DiscoveryLocationMatch,

    // ---- Contradicting signals ---------------------------------------------

    /// <summary>An installed application with no relation to the owner claims the same path.</summary>
    ConflictingApplicationMatch,

    /// <summary>
    /// The directory name is a vendor namespace, and this application has a
    /// different publisher, so it is a sibling product rather than the owner.
    /// </summary>
    PublisherMismatch,

    /// <summary>The vendor directory is shared by several installed products.</summary>
    SharedPublisherDirectory,

    /// <summary>The directory name carries no identity information ("Common", "Cache").</summary>
    GenericDirectoryName,

    /// <summary>Several applications are equally plausible owners.</summary>
    MultipleCandidateOwners,

    /// <summary>The candidate application is not present in the discovery results.</summary>
    UnknownApplication,

    /// <summary>The path is a Windows-managed or AppTrace-protected location.</summary>
    SystemManagedPath,

    /// <summary>
    /// Ownership was established at an ancestor directory and this location is
    /// inside it. Task 06: evidence about a location that does not come from the
    /// location's own name, which is what lets an application tree be attributed
    /// through children named for their content rather than for the product.
    /// </summary>
    InheritedFromOwner,

    /// <summary>
    /// A directory entry names an installed application as its subject: the location
    /// is <em>about</em> that application without belonging to it. Task 06.
    /// </summary>
    /// <remarks>
    /// This record lives only in <c>CandidateOwner.RelationshipEvidence</c> and is
    /// never scored, so a subject name found inside an owned tree cannot add its way
    /// to ownership of that tree.
    /// </remarks>
    SubjectNameMatch,

    /// <summary>
    /// The application's own uninstall entry references this file through its
    /// <c>DisplayIcon</c> value. Task 07.
    /// </summary>
    /// <remarks>
    /// The strongest of the static provenance records for linkage, because the
    /// registration <em>is</em> the application's own entry. It still proves only
    /// what it says: that the entry references this file, not that the application
    /// owns the file's ancestors.
    /// </remarks>
    DisplayIconMatch,

    /// <summary>
    /// Windows itself shows this application reaching this path, through an App
    /// Paths entry, a service, a scheduled task, a startup entry or a shortcut.
    /// Task 07.
    /// </summary>
    /// <remarks>
    /// <para>What it proves: the application is independently known to launch from,
    /// register or reference the path.</para>
    /// <para>What it does not prove: that the application owns every ancestor
    /// directory, or that a publisher owns every path containing its name. Those are
    /// separate claims, and this record does not make them.</para>
    /// </remarks>
    ProvenanceAnchorMatch,
}

/// <summary>How much a single piece of evidence should move the confidence score.</summary>
public enum EvidenceStrength
{
    /// <summary>Suggestive only; never sufficient on its own.</summary>
    Weak = 1,

    /// <summary>Meaningful support, but ambiguous without corroboration.</summary>
    Moderate = 2,

    /// <summary>Strong support; can carry an attribution together with one other signal.</summary>
    Strong = 3,

    /// <summary>Authoritative; the application itself declared this location.</summary>
    Decisive = 4,
}

/// <summary>Where a piece of evidence came from, so a user can audit it.</summary>
public enum EvidenceSource
{
    Registry,
    Filesystem,
    ExecutableMetadata,
    PathHeuristic,
    Derived,
}

/// <summary>
/// A single machine-readable reason for (or against) attributing a path to an
/// application.
/// </summary>
/// <remarks>
/// Explanations shown to the user are rendered from these records; AppTrace does
/// not store pre-written explanation strings, so the "why" can always be traced
/// back to the detector that fired.
/// </remarks>
public sealed class Evidence
{
    public required EvidenceType Type { get; init; }

    /// <summary>
    /// Which question this record answers. Set from <see cref="Type"/> so a record
    /// cannot be created with a kind that contradicts its type.
    /// </summary>
    public EvidenceKind Kind => EvidenceClassification.KindOf(Type);

    /// <summary>Short human-readable detail, e.g. the two values that matched.</summary>
    public required string Description { get; init; }

    public required EvidenceStrength Strength { get; init; }

    public required EvidenceSource Source { get; init; }

    /// <summary>
    /// True when the record argues for the attribution; false when it argues
    /// against it or bounds it.
    /// </summary>
    public required bool SupportsAttribution { get; init; }

    /// <summary>Signed contribution to the confidence score.</summary>
    public int Weight { get; init; }

    /// <summary>
    /// For a <see cref="EvidenceKind.Contradiction"/>, whether it forbids ownership
    /// or merely caps confidence. Meaningless for the other kinds.
    /// </summary>
    public ContradictionKind Contradiction => EvidenceClassification.ContradictionKindOf(Type);

    /// <summary>
    /// How much of the application's identity this record actually names.
    /// <c>0.0</c> is non-identifying or generic; <c>1.0</c> is fully
    /// product-specific.
    /// </summary>
    /// <remarks>
    /// <para>Only name-derived identity evidence computes a value. For every other
    /// record the default of <c>1.0</c> means "specificity does not apply here",
    /// which is deliberately not the same statement as "fully product-specific" —
    /// specificity can only ever <em>withhold</em> strength, never grant it.</para>
    /// <para>The value is not a probability and is not presented as one. It exists
    /// so a one-token coincidence such as a directory called <c>sdk</c> matching
    /// <c>ASUS Aura SDK</c> can be distinguished from a directory called
    /// <c>Vivaldi</c> matching <c>Vivaldi</c>.</para>
    /// </remarks>
    public double Specificity { get; init; } = 1.0;

    public override string ToString() => Kind == EvidenceKind.Contradiction
        ? $"{Type} ({Contradiction}) {Description}"
        : $"{Type} {Description}";
}
