namespace AppTrace.Core.Attribution;

using AppTrace.Core.Model;
using AppTrace.Core.Scanning;

/// <summary>Everything the engine needs to judge one directory.</summary>
public sealed class AttributionInput
{
    public required string Path { get; init; }

    /// <summary>Normalized (lower-cased, fully qualified) form of <see cref="Path"/>.</summary>
    public required string NormalizedPath { get; init; }

    public required string DirectoryName { get; init; }

    public required LocationCategory Category { get; init; }

    /// <summary>Ancestor directories already accepted by the engine, outermost first.</summary>
    public IReadOnlyList<string> AcceptedAncestorPaths { get; init; } = [];

    /// <summary>
    /// Normalized paths of ancestors whose ownership was <em>established</em>, with
    /// the application that owns them, outermost first.
    /// </summary>
    /// <remarks>
    /// Ownership established at an ancestor is evidence about its descendants. A
    /// real application tree is full of child names that have nothing to do with the
    /// product name — <c>User Data</c>, <c>Default</c>, <c>Cache</c>, <c>logs</c> —
    /// and requiring each of them to rediscover the same owner independently would
    /// both lose most of the tree and invite the very name coincidences Task 05
    /// removed. The ancestor assertion supplies what the child name cannot.
    /// </remarks>
    public IReadOnlyList<OwnedAncestor> OwnedAncestors { get; init; } = [];

    /// <summary>
    /// Normalized path of the scan root this directory sits under.
    /// </summary>
    /// <remarks>
    /// Path semantics needs it to know how deep a segment is below the scope the
    /// scanner actually judges. That depth is what makes "product level" a
    /// positional fact: an application's own name appears at the top of its tree,
    /// not several levels inside another product's structure.
    /// </remarks>
    public string? NormalizedScanRoot { get; init; }

    /// <summary>Depth beneath the scan root; root children are depth 1.</summary>
    public int Depth { get; init; }
}

/// <summary>
/// An ancestor directory whose ownership was established, and by which application.
/// </summary>
/// <param name="NormalizedPath">Normalized path of the ancestor.</param>
/// <param name="AppId">The application that owns it.</param>
/// <param name="Classification">The classification ownership was established at.</param>
public readonly record struct OwnedAncestor(string NormalizedPath, string AppId, Classification Classification);

/// <summary>The engine's verdict for a single directory.</summary>
public sealed class LocationAttribution
{
    public required string Path { get; init; }

    public IReadOnlyList<CandidateOwner> Candidates { get; init; } = [];

    /// <summary>Classification of the location itself (not of any single owner).</summary>
    public Classification Classification { get; init; } = Classification.Unknown;

    /// <summary>False when the engine wants to look at subdirectories.</summary>
    public bool OwnershipEstablished { get; init; }

    /// <summary>Human-readable justification for <see cref="OwnershipEstablished"/>.</summary>
    public required string StopReason { get; init; }

    /// <summary>
    /// Owners AppTrace stands behind. A <c>RelatedTo</c> candidate is never one of
    /// them, which is what keeps a subject name from becoming a claim on the bytes.
    /// </summary>
    public IReadOnlyList<CandidateOwner> AcceptedOwners
        => Candidates.Where(c => c.Accepted && c.Owns).ToArray();

    /// <summary>
    /// Applications this location's content is about, which do not own it and
    /// receive no bytes.
    /// </summary>
    public IReadOnlyList<CandidateOwner> RelatedApplications
        => Candidates.Where(c => c.Relation == CandidateRelation.RelatedTo).ToArray();

    public override string ToString() => $"{Path} [{Classification.Symbol()}] {StopReason}";
}

/// <summary>
/// Decides which installed application, if any, owns a filesystem location, and
/// records the evidence for every candidate it considered.
/// </summary>
/// <remarks>
/// <para><b>Pipeline.</b> For each location the engine runs a fixed set of
/// detectors. Each detector can emit <see cref="Evidence"/> for several
/// applications, because a location legitimately has several plausible owners.
/// Evidence is then scored per candidate, bucketed into a
/// <see cref="Classification"/>, and finally reduced to a decision about whether
/// to stop descending or keep partitioning the tree.</para>
/// <para><b>Evidence first.</b> Nothing here stores a pre-written explanation:
/// the CLI renders the "why" from the returned <see cref="Evidence"/> records.</para>
/// <para><b>Prefer false negatives.</b> A path is only accepted as owned when a
/// product-specific signal exists. Vendor namespaces alone never accept an
/// owner, they only raise candidates that keep the descent going.</para>
/// </remarks>
public sealed class AttributionEngine
{
    private const int MinimumUsefulTokenLength = 3;

    /// <summary>
    /// How many words of a product's name a directory must account for before
    /// AppTrace will assert that the directory's content is <em>about</em> it.
    /// </summary>
    /// <remarks>
    /// Two, because one word is never enough. A relationship carries no size and no
    /// score, so a reader cannot weigh it; a wrong "this file is about X" is
    /// therefore worse than saying nothing, and the measured false positives
    /// (<c>node</c>, <c>sdk</c>, <c>helper</c>, <c>tool</c>, <c>zip</c>) are all
    /// exactly one word.
    /// </remarks>
    private const int MinimumRelationshipTokens = 2;

    /// <summary>
    /// Score at which a single owner may stop the descent.
    /// </summary>
    private const int StopScoreThreshold = 45;

    /// <summary>
    /// Minimum name coverage before identity evidence may corroborate a provenance
    /// anchor into a HIGH claim.
    /// </summary>
    /// <remarks>
    /// Measured during the Task 02 spike: every observed false positive covers at
    /// most 0.33 of the application's name tokens, and every observed correct
    /// attribution covers at least 0.50. It is a discriminator that fits the cases
    /// we have, not a statistical law, and it is only ever used to <em>withhold</em>
    /// strength.
    /// </remarks>
    private const double MinimumCorroboratingSpecificity = 0.5;

    /// <summary>
    /// Score at which an application may be reported as a co-owner of a directory
    /// while the scanner continues descending. Deliberately lower than
    /// <see cref="StopScoreThreshold"/>: naming a sibling in the report is cheap and
    /// reversible, whereas closing a subtree on weak evidence is not.
    /// </summary>
    private const int NamespaceEvidenceBar = 20;

    /// <summary>Two candidates this close together are treated as equally plausible.</summary>
    private const int TieScoreWindow = 20;

    private readonly IReadOnlyList<AppIdentity> _apps;
    private readonly Dictionary<string, AppIdentity> _appsById;

    /// <summary>
    /// Publisher names carried by more than one installed application, as folded
    /// tokens. A directory named after such a publisher is a vendor namespace: it
    /// is shared by definition, and no single product may be credited with it.
    /// Computed once because the set is a property of the machine, not of a path.
    /// </summary>
    private readonly HashSet<string> _vendorNamespaces = new(StringComparer.Ordinal);

    /// <summary>Folded application name → applications with that exact name.</summary>
    private readonly Dictionary<string, List<AppIdentity>> _appsByFoldedName = new(StringComparer.Ordinal);

    /// <summary>Folded name token → applications whose name contains that token.</summary>
    private readonly Dictionary<string, List<AppIdentity>> _appsByToken = new(StringComparer.Ordinal);

    /// <summary>Folded publisher → applications from that publisher.</summary>
    private readonly Dictionary<string, List<AppIdentity>> _appsByPublisherToken = new(StringComparer.Ordinal);

    /// <summary>
    /// Paths Windows itself shows each application reaching, discovered once per
    /// scan. Empty when provenance discovery was not run, in which case the engine
    /// behaves exactly as it did before Task 07.
    /// </summary>
    private readonly Discovery.ProvenanceIndex _provenance;

    private readonly AttributionOptions _options;
    private readonly Dictionary<string, ExecutableProbe> _executableProbeCache = new(StringComparer.OrdinalIgnoreCase);
    private int _executableProbes;

    public AttributionEngine(
        IReadOnlyList<AppIdentity> apps,
        AttributionOptions? options = null,
        Discovery.ProvenanceIndex? provenance = null)
    {
        _apps = apps;
        _provenance = provenance ?? Discovery.ProvenanceIndex.Empty;
        _appsById = new Dictionary<string, AppIdentity>(StringComparer.Ordinal);
        foreach (var app in apps)
        {
            _appsById[app.Id] = app;
        }

        foreach (var group in apps
                     .Where(a => a.NormalizedPublisher.Length >= MinimumUsefulTokenLength)
                     .GroupBy(a => TextNormalizer.Fold(a.NormalizedPublisher), StringComparer.Ordinal))
        {
            if (group.Count() > 1 && group.Key.Length > 0)
            {
                _vendorNamespaces.Add(group.Key);
            }
        }

        // Identity indexes, built once per scan so candidate generation is a lookup
        // rather than a comparison against every installed application.
        foreach (var app in apps)
        {
            var folded = TextNormalizer.Fold(app.NormalizedName);
            if (folded.Length >= MinimumUsefulTokenLength)
            {
                AddToIndex(_appsByFoldedName, folded, app);
            }

            foreach (var token in NameSegments(app.NormalizedName).Distinct(StringComparer.Ordinal))
            {
                if (token.Length >= MinimumUsefulTokenLength)
                {
                    AddToIndex(_appsByToken, token, app);
                }
            }

            var publisherToken = TextNormalizer.Fold(app.NormalizedPublisher);
            if (publisherToken.Length >= MinimumUsefulTokenLength)
            {
                AddToIndex(_appsByPublisherToken, publisherToken, app);
            }
        }

        _options = options ?? new AttributionOptions();
    }

    private static void AddToIndex(
        Dictionary<string, List<AppIdentity>> index,
        string key,
        AppIdentity app)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        list.Add(app);
    }

    /// <summary>Evaluates one directory.</summary>
    public LocationAttribution Evaluate(AttributionInput input)
    {
        var evidenceByApp = new Dictionary<string, List<Evidence>>(StringComparer.Ordinal);

        CollectDeclaredInstallLocationEvidence(input, evidenceByApp);
        CollectInheritedOwnershipEvidence(input, evidenceByApp);
        CollectProvenanceAnchorEvidence(input, evidenceByApp);
        CollectNameEvidence(input, evidenceByApp);
        CollectPublisherEvidence(input, evidenceByApp);
        CollectContextEvidence(input, evidenceByApp);
        CollectExecutableMetadataEvidence(input, evidenceByApp);
        AddBoundingEvidence(input, evidenceByApp);

        var candidates = BuildCandidates(evidenceByApp);
        var (decided, established, reason) = DecideOwnership(candidates);
        var classification = ClassifyLocation(decided);

        // Relatedness is decided only after ownership, because a relationship is a
        // statement about a location that something else already owns: "this file
        // belongs to A while being about B". Without an owner there is nothing for B
        // to be related *to*, and inventing one would trade an honest UNKNOWN for a
        // claim the evidence does not support.
        var withRelations = AppendRelatedApplications(input, decided);

        return new LocationAttribution
        {
            Path = input.Path,
            Candidates = withRelations,
            Classification = classification,
            OwnershipEstablished = established,
            StopReason = reason,
        };
    }

    // ---------------------------------------------------------------------
    // Detectors
    // ---------------------------------------------------------------------

    /// <summary>
    /// Matches the location against every application's declared install location,
    /// which is the strongest evidence AppTrace has.
    /// </summary>
    /// <remarks>
    /// <para>Two distinct cases, deliberately weighted differently:</para>
    /// <list type="bullet">
    /// <item>This directory <em>is</em> the registered install location. That is
    /// decisive, but only when the application's own name anchors to the directory
    /// name. Vendors routinely register the shared parent
    /// (<c>C:\Program Files\Adobe</c>, <c>C:\Program Files\Google</c>) as the
    /// install location of every product beneath it, so without that check the
    /// registry would hand the whole namespace to whichever product is processed
    /// first.</item>
    /// <item>This directory is <em>inside</em> a registered install location. That
    /// is strong evidence the product's own files live here, but it is not proof
    /// that the product owns everything below, so it is recorded as
    /// <see cref="EvidenceType.InstallLocationMatch"/> rather than as a decisive
    /// claim.</item>
    /// </list>
    /// </remarks>
    private void CollectDeclaredInstallLocationEvidence(
        AttributionInput input,
        Dictionary<string, List<Evidence>> evidenceByApp)
    {
        // A vendor namespace such as "Google" or "Adobe", shared by more than one
        // installed product, may not be owned by any single product — however its
        // uninstall registration is worded, and regardless of processing order. The
        // scanner descends into it to find the per-product boundary instead.
        if (_vendorNamespaces.Contains(TextNormalizer.Fold(input.DirectoryName)))
        {
            CollectVendorNamespaceEvidence(input, evidenceByApp);
            return;
        }

        foreach (var app in _apps)
        {
            if (app.NormalizedInstallLocation is not { Length: > 0 } install
                || !IsSameOrDescendant(input.NormalizedPath, install))
            {
                continue;
            }

            if (!string.Equals(input.NormalizedPath, install, StringComparison.Ordinal))
            {
                // Nested inside a declared location: only meaningful for the
                // application that declared it.
                Add(
                    evidenceByApp,
                    app,
                    EvidenceType.InstallLocationMatch,
                    EvidenceStrength.Strong,
                    EvidenceSource.Registry,
                    $"Inside the install location registered for \"{app.DisplayName}\" ({app.InstallLocation}).",
                    true);
                continue;
            }

            Add(
                evidenceByApp,
                app,
                EvidenceType.DeclaredInstallLocation,
                EvidenceStrength.Decisive,
                EvidenceSource.Registry,
                $"Windows registers this exact directory as the install location of \"{app.DisplayName}\".",
                true);
        }
    }

    /// <summary>
    /// Handles a directory named after a publisher that ships more than one
    /// installed product.
    /// </summary>
    /// <remarks>
    /// This is the "AppData\Local\Google" case. Every product that declared this
    /// namespace as its install location becomes an equally strong candidate, and
    /// the location is reported as SHARED rather than being silently handed to
    /// whichever product the registry happened to list first. Nothing here is
    /// strong enough to stop the descent, which is the point: the per-product
    /// boundary lives in the subdirectories.
    /// </remarks>
    private void CollectVendorNamespaceEvidence(
        AttributionInput input,
        Dictionary<string, List<Evidence>> evidenceByApp)
    {
        foreach (var app in _apps)
        {
            if (app.NormalizedInstallLocation is { Length: > 0 } install
                && string.Equals(input.NormalizedPath, install, StringComparison.Ordinal))
            {
                Add(
                    evidenceByApp,
                    app,
                    EvidenceType.PublisherMatch,
                    EvidenceStrength.Weak,
                    EvidenceSource.PathHeuristic,
                    $"Directory is the \"{app.Publisher}\" vendor namespace, which is registered as an install location by more than one installed product.",
                    true);
            }
        }

    }

    private static bool IsSameOrDescendant(string normalizedPath, string normalizedContainer)
        => normalizedPath.Equals(normalizedContainer, StringComparison.Ordinal)
            || normalizedPath.StartsWith(normalizedContainer + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// True when the application's own registration names exactly this directory as
    /// its install location.
    /// </summary>
    private static bool DeclaresThisExactDirectory(AppIdentity app, string normalizedPath)
        => app.NormalizedInstallLocation is { Length: > 0 } install
            && string.Equals(normalizedPath, install, StringComparison.Ordinal);

    /// <summary>
    /// Generates and validates name-derived identity candidates for one directory.
    /// </summary>
    /// <remarks>
    /// <para><b>Candidate generation is an explicit stage.</b> Phase 0 compared the
    /// directory with every installed application and kept whatever a string
    /// similarity accepted. This looks the directory name up in a prebuilt index
    /// instead, so the applications compared against a path are the few whose names
    /// can actually relate to it — and it emits the reason each candidate was
    /// generated, so the WHY output can explain candidacy as well as acceptance.</para>
    /// <para><b>Similarity validates; it does not manufacture.</b> Two independent
    /// gates must pass before a name may propose an owner:</para>
    /// <list type="number">
    /// <item><b>Structure.</b> If the path is inside an established structure — a
    /// dependency tree, a package-manager cache, a runtime, a logs directory — the
    /// segment names beneath it are content, and a name coincidence there is a
    /// structural misreading rather than evidence of an owner.</item>
    /// <item><b>Specificity.</b> A match must account for enough of the
    /// application's name to be distinctive, and must not be a <em>near miss</em>
    /// that requires part of the directory name to be discarded.</item>
    /// </list>
    /// </remarks>
    private void CollectNameEvidence(AttributionInput input, Dictionary<string, List<Evidence>> evidenceByApp)
    {
        if (GenericDirectoryNames.IsGeneric(input.DirectoryName))
        {
            return;
        }

        var directorySegments = NameSegments(input.DirectoryName);
        var directoryToken = TextNormalizer.Fold(input.DirectoryName);
        if (directoryToken.Length < MinimumUsefulTokenLength || directorySegments.Count == 0)
        {
            return;
        }

        // Structural suppression is checked before anything else and is never lifted.
        // A segment inside a dependency tree, a package cache or an embedded runtime is
        // a package or component name, and no registration elsewhere turns a package
        // name into a product name. This must come first because the exact-name
        // shortcut below would otherwise return before it was ever consulted.
        if (IsStructurallySuppressed(input))
        {
            return;
        }

        // Snapshot which applications independent evidence has already proposed,
        // before this detector adds anything. The name evidence added below must never
        // count as its own justification.
        var proposedIndependently = evidenceByApp
            .Where(kv => kv.Value.Any(e => e.SupportsAttribution && e.Kind != EvidenceKind.Identity))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);

        // Indexed candidate generation: only applications whose folded name or
        // tokens actually relate to this segment are even considered.
        var matched = new List<(AppIdentity App, bool Exact)>();
        var sawNearMissOnly = false;
        foreach (var app in CandidatesFor(directoryToken, directorySegments))
        {
            if (app.NormalizedName.Length < MinimumUsefulTokenLength)
            {
                continue;
            }

            var appToken = TextNormalizer.Fold(app.NormalizedName);
            if (appToken.Length < MinimumUsefulTokenLength)
            {
                continue;
            }

            if (string.Equals(directoryToken, appToken, StringComparison.Ordinal))
            {
                matched.Add((app, true));
                continue;
            }

            if (!IsScopedNameMatch(directorySegments, NameSegments(app.NormalizedName)))
            {
                continue;
            }

            // A deep segment may not propose an application on its own, but it may
            // corroborate one that independent evidence already proposed. Positional
            // suppression exists to stop a name coincidence from *creating* a
            // candidate; it was never meant to stop a candidate from being confirmed
            // by a name that genuinely agrees with it. Without this, a product root
            // two levels below a scan root could never reach HIGH however strong its
            // Windows registrations were, because provenance alone is capped at
            // MEDIUM by design.
            // Lifting positional suppression is what lets a product root two levels
            // below a scan root reach HIGH: provenance proposes it, and its own name
            // then corroborates. Structural suppression was already handled above and
            // is never lifted.
            if (!MaySegmentNameItsOwnProduct(input) && !proposedIndependently.Contains(app.Id))
            {
                continue;
            }

            if (IsNearMiss(input, app))
            {
                // "Cities Skylines II" is not "Cities: Skylines". When the best
                // available explanation requires discarding part of the directory
                // name, the honest answer is UNKNOWN rather than the nearest
                // installed application — especially when the correct product is
                // simply not installed.
                sawNearMissOnly = true;
                continue;
            }

            matched.Add((app, false));
        }

        if (matched.Count != 1)
        {
            // Zero matches: nothing to say. Several matches: the name alone cannot
            // decide, so the path stays a candidate set rather than gaining a
            // strong identity signal.
            _ = sawNearMissOnly;
            return;
        }

        var (owner, exact) = matched[0];
        var appSegments = NameSegments(owner.NormalizedName);
        if (!IsNameSpecificEnough(directorySegments, appSegments))
        {
            // The name accounts for too little of the product to identify it. A
            // segment like "sdk" or "tool" that is one word of a five-word product
            // name is ambiguous vocabulary: it may mean that product, or a hundred
            // other things.
            return;
        }

        Add(
            evidenceByApp,
            owner,
            exact ? EvidenceType.ExactDirectoryNameMatch : EvidenceType.NormalizedNameMatch,
            exact ? EvidenceStrength.Strong : EvidenceStrength.Moderate,
            EvidenceSource.PathHeuristic,
            exact
                ? $"Directory name \"{input.DirectoryName}\" equals the application name \"{owner.DisplayName}\"."
                : $"Directory name \"{input.DirectoryName}\" contains the application name \"{owner.DisplayName}\" as a whole word.",
            true,
            CoverageOf(input.DirectoryName, appSegments));
    }

    /// <summary>
    /// True when the directory's own name is allowed to identify a product.
    /// </summary>
    /// <remarks>
    /// A registered path is always allowed: structure may observe that a directory
    /// "looks like a cache", but it must never override the application's own
    /// registration. That is what keeps a real product legitimately named
    /// <c>Cache</c>, or a directory a product explicitly registered, from being
    /// suppressed by vocabulary alone.
    /// </remarks>
    private static bool MaySegmentNameItsOwnProduct(AttributionInput input)
    {
        // The depth the scanner reports is the authority on position; the scan root
        // only sharpens it when the path really is inside that root.
        var semantics = PathSemantics.Analyse(input.NormalizedPath, input.NormalizedScanRoot, input.Depth);
        return !PathSemantics.SuppressesIdentityForLeaf(semantics);
    }

    /// <summary>
    /// True when the directory's name is suppressed because it is content of an
    /// established structure.
    /// </summary>
    /// <remarks>
    /// The one reason for suppression that is never lifted. A segment inside
    /// <c>node_modules</c>, a package cache or an embedded runtime is a package or
    /// component name, and no registration elsewhere makes a package name into a
    /// product name.
    /// </remarks>
    private static bool IsStructurallySuppressed(AttributionInput input)
    {
        var semantics = PathSemantics.Analyse(input.NormalizedPath, input.NormalizedScanRoot, input.Depth);
        return PathSemantics.SuppressedByStructureForLeaf(semantics);
    }

    /// <summary>
    /// True when evidence <em>other than a name resemblance</em> has already proposed
    /// this application here.
    /// </summary>
    /// <remarks>
    /// Positional suppression exists to stop a name coincidence from <em>creating</em>
    /// a candidate. It must not stop a candidate that provenance, a registration or an
    /// ancestor already proposed from being corroborated by a name that agrees with
    /// it — otherwise a product root below the top of a scan root could never reach
    /// HIGH however many Windows registrations pointed at it, because provenance alone
    /// is capped at MEDIUM by design.
    /// </remarks>
    private static bool ProposedBySomethingOtherThanItsName(
        Dictionary<string, List<Evidence>> evidenceByApp,
        string appId)
        => evidenceByApp.TryGetValue(appId, out var records)
            && records.Any(e => e.SupportsAttribution && e.Kind != EvidenceKind.Identity);

    /// <summary>
    /// Proposes applications that Windows itself shows reaching this directory or a
    /// path inside it.
    /// </summary>
    /// <remarks>
    /// <para><b>The question this answers.</b> Not "does this path look like the
    /// application", but "can Windows show that the application actually reaches it".
    /// A service that launches a file here, a task that runs it, a startup entry that
    /// starts it, a shortcut that targets it, an App Paths registration that resolves
    /// to it, or the application's own uninstall entry referencing it, are all
    /// independent registrations rather than name resemblances.</para>
    /// <para><b>What it does not prove.</b> A registration is about the file it
    /// names. It does not establish that the application owns every ancestor
    /// directory, so this detector proposes a candidate for the directory the anchor
    /// applies to and leaves the confidence ladder to decide how much that is worth.
    /// Only an anchor that is genuinely <em>at or below</em> the directory, or a
    /// declared install location, can carry a confident claim; a bare registration
    /// with no matching identity stays at MEDIUM.</para>
    /// <para>A low-value anchor — an updater or an uninstaller — is still reported,
    /// because the application really does reach that path, but it is marked as such
    /// in the explanation so it is not read as equivalent to the product's own
    /// executable.</para>
    /// </remarks>
    private void CollectProvenanceAnchorEvidence(
        AttributionInput input,
        Dictionary<string, List<Evidence>> evidenceByApp)
    {
        var anchors = _provenance.ForDirectory(input.NormalizedPath);
        if (anchors.Count == 0)
        {
            return;
        }

        foreach (var anchor in anchors)
        {
            if (!_appsById.TryGetValue(anchor.AppId, out var app))
            {
                continue;
            }

            var roleNote = anchor.Role switch
            {
                Discovery.ExecutableRole.MainApplication => string.Empty,
                Discovery.ExecutableRole.Unknown => string.Empty,
                _ => $" (the file is an {anchor.Role} rather than the product's own executable)",
            };

            var pathRelative = RelativeTo(input.NormalizedPath, anchor.NormalizedPath);

            Add(
                evidenceByApp,
                app,
                anchor.Source == Discovery.ProvenanceSource.DisplayIcon
                    ? EvidenceType.DisplayIconMatch
                    : EvidenceType.ProvenanceAnchorMatch,
                EvidenceStrength.Strong,
                EvidenceSource.Registry,
                $"{DescribeSource(anchor.Source)} shows \"{app.DisplayName}\" reaching " +
                $"\"{anchor.Path}\", {pathRelative} this directory{roleNote}.",
                true);
        }
    }

    private static string RelativeTo(string directory, string anchoredPath)
        => anchoredPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? "inside"
            : anchoredPath.Equals(directory, StringComparison.Ordinal)
                ? "at"
                : "containing";

    private static string DescribeSource(Discovery.ProvenanceSource source) => source switch
    {
        Discovery.ProvenanceSource.DisplayIcon => "The application's own uninstall entry",
        Discovery.ProvenanceSource.AppPath => "An App Paths registration",
        Discovery.ProvenanceSource.Service => "A Windows service",
        Discovery.ProvenanceSource.ScheduledTask => "A scheduled task",
        Discovery.ProvenanceSource.RunKey => "A startup registration",
        Discovery.ProvenanceSource.Shortcut => "A Start Menu shortcut",
        _ => "A Windows registration",
    };

    /// <summary>
    /// Carries ownership established at an ancestor down to this directory.
    /// </summary>
    /// <remarks>
    /// <para>This is the only detector that may propose an application without any
    /// evidence drawn from the directory's own name. That is the point: inside an
    /// established application tree, most child directories are named for what they
    /// contain (<c>User Data</c>, <c>Default</c>, <c>Cache</c>, <c>NvBackend</c>),
    /// not for the product, so the ancestor assertion is the only thing that can
    /// attribute them at all.</para>
    /// <para><b>It proposes; it does not decide.</b> The inherited record is
    /// deliberately weak, so independent evidence at this directory can outrank it
    /// and a decisive contradiction still forbids the claim outright. That is what
    /// makes a descendant with its own registration a boundary rather than a
    /// takeover.</para>
    /// </remarks>
    private void CollectInheritedOwnershipEvidence(
        AttributionInput input,
        Dictionary<string, List<Evidence>> evidenceByApp)
    {
        if (input.OwnedAncestors.Count == 0)
        {
            return;
        }

        // Nearest established ancestor wins: it is the tightest statement about
        // this directory's scope.
        var ancestor = input.OwnedAncestors[^1];
        if (!_appsById.TryGetValue(ancestor.AppId, out var app))
        {
            return;
        }

        // A vendor namespace is a boundary. Ownership of a vendor root does not
        // extend into it, because the products below it are separately owned.
        if (VendorNamespaceOf(input.DirectoryName) is not null)
        {
            return;
        }

        Add(
            evidenceByApp,
            app,
            EvidenceType.InheritedFromOwner,
            EvidenceStrength.Weak,
            EvidenceSource.Derived,
            $"Ownership of \"{app.DisplayName}\" is established at the ancestor " +
            $"\"{ancestor.NormalizedPath}\", and this directory is inside it.",
            true);
    }

    /// <summary>
    /// Proposes the applications this location's content is <em>about</em>, without
    /// letting them own it.
    /// </summary>
    /// <remarks>
    /// <para>Conservative by construction. A relationship requires all of:</para>
    /// <list type="number">
    /// <item>an established owner already exists for this directory, so there is
    /// something for the other application to be related to;</item>
    /// <item>the directory's parent is a <see cref="StructureKind.SubjectData"/>
    /// store, so the name in it is an entry rather than a claim;</item>
    /// <item>the entry names an installed product specifically — the same
    /// specificity floor that candidate generation uses, and no near miss.</item>
    /// </list>
    /// <para>This is why <c>node</c>, <c>sdk</c>, <c>helper</c>, <c>tool</c> and
    /// <c>zip</c> do not become relationships merely because a matching application
    /// is installed: they are either not specific enough, or they sit in structures
    /// that are not subject stores.</para>
    /// </remarks>
    private IReadOnlyList<CandidateOwner> AppendRelatedApplications(
        AttributionInput input,
        IReadOnlyList<CandidateOwner> decided)
    {
        var owners = decided.Where(c => c.Accepted && c.Owns).ToArray();
        if (owners.Length == 0)
        {
            return decided;
        }

        if (!IsInsideSubjectDataStore(input))
        {
            return decided;
        }

        var directorySegments = NameSegments(input.DirectoryName);
        var directoryToken = TextNormalizer.Fold(input.DirectoryName);
        if (directoryToken.Length < MinimumUsefulTokenLength || directorySegments.Count == 0)
        {
            return decided;
        }

        var related = new List<CandidateOwner>();
        foreach (var app in CandidatesFor(directoryToken, directorySegments))
        {
            // The owner of a directory is not "related to" it; it owns it.
            if (owners.Any(o => string.Equals(o.AppId, app.Id, StringComparison.Ordinal)))
            {
                continue;
            }

            var appSegments = NameSegments(app.NormalizedName);
            if (appSegments.Count == 0
                || !IsScopedNameMatch(directorySegments, appSegments)
                || IsNearMiss(input, app)
                || !IsNameSpecificEnough(directorySegments, appSegments))
            {
                continue;
            }

            // Stricter than ownership: a relationship must be named by more than one
            // word of the product. "helper", "tool", "zip", "sdk" and "node" each
            // cover exactly one word of some installed product's name, and a single
            // ambiguous word is not enough to assert that a file is about a
            // particular application. Being wrong here is worse than saying nothing,
            // because the reader has no size or score to weigh it against.
            if (MatchedTokenCount(directorySegments, appSegments) < MinimumRelationshipTokens)
            {
                continue;
            }

            var ownerNames = string.Join(", ", owners.Select(o => o.AppDisplayName ?? o.AppId));
            related.Add(new CandidateOwner
            {
                AppId = app.Id,
                AppDisplayName = app.DisplayName,
                Relation = CandidateRelation.RelatedTo,
                Accepted = false,
                Classification = Classification.Unknown,
                Score = 0,
                RelationshipEvidence =
                [
                    new Evidence
                    {
                        Type = EvidenceType.SubjectNameMatch,
                        Description =
                            $"Directory name \"{input.DirectoryName}\" identifies the installed application " +
                            $"\"{app.DisplayName}\", and its parent is a store whose entries refer to other products " +
                            $"rather than to their owner. The location is owned by {ownerNames}.",
                        Strength = EvidenceStrength.Moderate,
                        Source = EvidenceSource.Derived,
                        SupportsAttribution = false,
                        Weight = 0,
                        Specificity = CoverageOf(input.DirectoryName, appSegments),
                    },
                ],
            });
        }

        return related.Count == 0 ? decided : [.. decided, .. related];
    }

    /// <summary>
    /// True when the directory sits inside a store whose entries refer to other
    /// products.
    /// </summary>
    /// <remarks>
    /// Walks up past anonymous intermediates (content hashes, GUIDs, indices) and
    /// past segments that carry no structural meaning, and returns true when the
    /// first meaningful ancestor is a subject store. Stores commonly insert one
    /// there — <c>Recommendations\&lt;hash&gt;\cities_skylines</c> — and stopping at the
    /// immediate parent would miss exactly the case this exists for.
    /// </remarks>
    private static bool IsInsideSubjectDataStore(AttributionInput input)
    {
        var segments = input.NormalizedPath
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var scanRootSegments = input.NormalizedScanRoot is { Length: > 0 } root
            ? root.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length
            : 0;

        // Walk up from the leaf, ignoring the leaf itself and any anonymous or
        // structurally meaningless segment. Bounded by the path, so it terminates.
        for (var start = segments.Length - 1; start > scanRootSegments; start--)
        {
            var candidate = string.Join(Path.DirectorySeparatorChar, segments.Take(start));
            var semantics = PathSemantics.Analyse(
                candidate,
                input.NormalizedScanRoot,
                Math.Max(input.Depth - (segments.Length - start), 0));

            if (semantics.Count == 0)
            {
                break;
            }

            var governing = semantics[^1];
            if (governing.Kind == StructureKind.SubjectData)
            {
                return true;
            }

            // Stop at the first meaningful segment: it is the entry's real context,
            // and continuing past it would connect unrelated parts of the tree.
            if (governing.Kind != StructureKind.Unknown
                || !PathSemantics.IsAnonymousIntermediary(governing.Segment))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// How many words of the product's name the directory name accounts for.
    /// </summary>
    private static int MatchedTokenCount(
        IReadOnlyList<string> directorySegments,
        IReadOnlyList<string> appSegments)
        => appSegments.Count(segment => directorySegments.Contains(segment));

    /// <summary>
    /// The applications whose names can plausibly relate to a directory segment,
    /// found through the identity indexes rather than by scanning every installed
    /// application.
    /// </summary>
    /// <remarks>
    /// This is the "candidate generation" stage. It is deliberately high-recall and
    /// cheap: it narrows the comparison set, and validation decides what survives.
    /// </remarks>
    private IEnumerable<AppIdentity> CandidatesFor(string directoryToken, IReadOnlyList<string> directorySegments)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (_appsByFoldedName.TryGetValue(directoryToken, out var exactName))
        {
            foreach (var app in exactName)
            {
                if (seen.Add(app.Id))
                {
                    yield return app;
                }
            }
        }

        // Token index: an application whose name contains this segment as a word.
        foreach (var segment in directorySegments)
        {
            if (!_appsByToken.TryGetValue(segment, out var byToken))
            {
                continue;
            }

            foreach (var app in byToken)
            {
                if (seen.Add(app.Id))
                {
                    yield return app;
                }
            }
        }

        // Publisher namespace: a directory named after a publisher is a real
        // relationship and is how vendor roots are resolved at all.
        if (_appsByPublisherToken.TryGetValue(directoryToken, out var byPublisher))
        {
            foreach (var app in byPublisher)
            {
                if (seen.Add(app.Id))
                {
                    yield return app;
                }
            }
        }
    }

    /// <summary>
    /// True when the directory name is a near miss rather than a name for this
    /// application.
    /// </summary>
    /// <remarks>
    /// <para>A near miss is a superset: the directory says more than the product's
    /// name does, and the extra says something other than the publisher. The
    /// distinction matters in both directions:</para>
    /// <list type="bullet">
    /// <item><c>Cities Skylines II</c> against <c>Cities: Skylines</c> — the extra
    /// <c>ii</c> is a different product, so this is a near miss.</item>
    /// <item><c>Microsoft Edge</c> against <c>Edge</c> — the extra <c>microsoft</c>
    /// is the publisher, which is legitimate and must keep matching, because that
    /// is exactly how per-user data directories are named.</item>
    /// </list>
    /// </remarks>
    private static bool IsNearMiss(AttributionInput input, AppIdentity app)
    {
        var directory = TextNormalizer.Fold(input.DirectoryName);
        var product = TextNormalizer.Fold(app.NormalizedName);
        if (directory.Length <= product.Length || !directory.Contains(product, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = directory.Replace(product, string.Empty, StringComparison.Ordinal);
        if (remainder.Length == 0)
        {
            return false;
        }

        // A publisher prefix or suffix such as "microsoft" or "adobe" explains the
        // extra words and is not a near miss.
        var publisher = TextNormalizer.Fold(app.NormalizedPublisher);
        if (publisher.Length > 0 && publisher.Contains(remainder, StringComparison.Ordinal))
        {
            return false;
        }

        // Something the application's own display name contains also explains it.
        // NormalizeDisplayName drops generic tokens, so "Vendor App" normalizes to
        // "vendor" and the leftover "app" comes from the application's own name rather
        // than from the directory saying something extra. An unrelated suffix such as
        // the "II" in "Cities Skylines II" is not in the display name and still counts
        // as a near miss.
        var display = NameSegments(app.DisplayName);
        if (display.Count > 0 && ExplainsRemainder(remainder, display))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when the leftover characters are entirely accounted for by these words.
    /// </summary>
    private static bool ExplainsRemainder(string remainder, IReadOnlyList<string> words)
    {
        foreach (var word in words.OrderByDescending(w => w.Length))
        {
            if (word.Length == 0)
            {
                continue;
            }

            if (remainder.Contains(word, StringComparison.Ordinal))
            {
                remainder = remainder.Replace(word, string.Empty, StringComparison.Ordinal);
            }
        }

        return remainder.Length == 0;
    }

    /// <summary>
    /// True when the leftover characters are exactly the dropped tokens.
    /// </summary>
    private static bool ExplainsRemainder(string remainder, List<string> dropped)
    {
        var ordered = dropped.OrderByDescending(t => t.Length).ToList();
        foreach (var token in ordered)
        {
            if (remainder.Contains(token, StringComparison.Ordinal))
            {
                remainder = remainder.Replace(token, string.Empty, StringComparison.Ordinal);
            }
        }

        return remainder.Length == 0;
    }

    /// <summary>
    /// True when the match accounts for enough of the application's name to identify
    /// it rather than merely overlap with it.
    /// </summary>
    /// <remarks>
    /// The floor is the same measured discriminator used for corroboration, for the
    /// same reason and with the same caveat: it describes the cases we have, and it
    /// is only ever used to withhold a claim, never to grant one.
    /// </remarks>
    private static bool IsNameSpecificEnough(
        List<string> directorySegments,
        List<string> appSegments)
    {
        if (appSegments.Count <= directorySegments.Count)
        {
            // The directory names all of the product's words or a contiguous run of
            // them, so there is nothing ambiguous left over.
            return true;
        }

        return CoverageOf(string.Join(' ', directorySegments), appSegments) >= MinimumCorroboratingSpecificity;
    }

    /// <summary>
    /// Splits a name into comparable segments on spaces, hyphens and
    /// underscores. <c>Acrobat DC</c> becomes <c>[acrobat, dc]</c>.
    /// </summary>
    internal static List<string> NameSegments(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([' ', '\t', '-', '_', '.', ',', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
            .Select(TextNormalizer.Fold)
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Decides whether two product names overlap closely enough to be worth
    /// considering, without allowing a vendor namespace to be claimed by one of
    /// its products.
    /// </summary>
    /// <remarks>
    /// The overlap must be <em>whole words, anchored at one end</em>: the
    /// directory's words have to be a leading or trailing run of the application's
    /// words, or the other way round. So <c>Acrobat DC</c> matches
    /// <c>Adobe Acrobat DC</c>, and <c>Chrome</c> matches <c>Google Chrome</c>,
    /// while the vendor directory <c>Google</c> does not match
    /// <c>Google Chrome</c> — it is neither a prefix nor a suffix of the words.
    /// Plain substring containment is deliberately not used: it would let
    /// <c>google</c> (folded from the vendor folder) match <c>googlechrome</c>.
    /// </remarks>
    private static bool IsScopedNameMatch(
        IReadOnlyList<string> directorySegments,
        IReadOnlyList<string> appSegments)
    {
        return IsAnchoredWordRun(directorySegments, appSegments)
            || IsAnchoredWordRun(appSegments, directorySegments);
    }

    /// <summary>
    /// True when <paramref name="run"/> is a leading or trailing sequence of
    /// <paramref name="words"/>.
    /// </summary>
    private static bool IsAnchoredWordRun(IReadOnlyList<string> run, IReadOnlyList<string> words)
    {
        if (run.Count == 0 || run.Count > words.Count)
        {
            return false;
        }

        var atStart = true;
        for (var i = 0; i < run.Count && atStart; i++)
        {
            atStart = string.Equals(run[i], words[i], StringComparison.Ordinal);
        }

        if (atStart)
        {
            return true;
        }

        var offset = words.Count - run.Count;
        for (var i = 0; i < run.Count; i++)
        {
            if (!string.Equals(run[i], words[offset + i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void CollectPublisherEvidence(AttributionInput input, Dictionary<string, List<Evidence>> evidenceByApp)
    {
        var segments = PathSegments(input.NormalizedPath);
        if (segments.Count == 0)
        {
            return;
        }

        var directoryToken = TextNormalizer.Fold(input.DirectoryName);

        foreach (var app in _apps)
        {
            var publisherToken = TextNormalizer.Fold(app.NormalizedPublisher);
            if (publisherToken.Length < MinimumUsefulTokenLength)
            {
                continue;
            }

            // Segment equality avoids "Adobe Systems" matching an unrelated path
            // that merely contains those letters. This is deliberately the weak
            // KnownPublisherNamespace record: sharing a name with a publisher does
            // not identify a product, so it corroborates but never decides. The
            // stronger PublisherMatch is emitted only by
            // CollectVendorNamespaceEvidence, for a directory that is genuinely a
            // shared vendor namespace.
            if (!segments.Any(s => string.Equals(s, publisherToken, StringComparison.Ordinal)))
            {
                continue;
            }

            Add(
                evidenceByApp,
                app,
                EvidenceType.KnownPublisherNamespace,
                EvidenceStrength.Weak,
                EvidenceSource.PathHeuristic,
                $"Path is inside the \"{app.Publisher}\" namespace of \"{app.DisplayName}\".",
                true);
        }
    }

    /// <summary>
    /// Adds evidence that describes the location's <em>context</em>: which
    /// well-known area it sits in, whether an ancestor is already attributed, and
    /// whether the directory belongs to a different publisher's namespace.
    /// </summary>
    /// <remarks>
    /// Context evidence is applied only to applications that a product-specific
    /// detector already proposed. Without that restriction every installed
    /// application becomes a candidate of every directory on the machine, which
    /// is both meaningless (a per-user data path says nothing about which of 200
    /// applications owns a folder) and quadratic in memory.
    /// </remarks>
    private void CollectContextEvidence(AttributionInput input, Dictionary<string, List<Evidence>> evidenceByApp)
    {
        if (evidenceByApp.Count == 0)
        {
            return;
        }

        // If the directory name is some *other* publisher's namespace, this
        // application is a sibling product rather than the owner. Without this, a
        // vendor folder such as "Netease" or "NVIDIA" inherits the ownership of
        // whichever product happened to be resolved above it.
        var vendorNamespace = VendorNamespaceOf(input.DirectoryName);

        var inApplicationData = input.Category is LocationCategory.LocalAppData
            or LocationCategory.RoamingAppData
            or LocationCategory.LocalLowAppData;

        var normalizedParent = ParentOf(input.NormalizedPath);
        var parentIsAttributed = normalizedParent is not null
            && input.AcceptedAncestorPaths.Any(a => string.Equals(TextNormalizer.NormalizePath(a), normalizedParent, StringComparison.Ordinal));

        if (!inApplicationData && !parentIsAttributed && vendorNamespace is null)
        {
            return;
        }

        foreach (var app in ProposedCandidates(evidenceByApp).ToList())
        {
            var appPublisher = TextNormalizer.Fold(app.NormalizedPublisher);

            if (vendorNamespace is not null && !string.Equals(appPublisher, vendorNamespace, StringComparison.Ordinal))
            {
                Add(
                    evidenceByApp,
                    app,
                    EvidenceType.PublisherMismatch,
                    EvidenceStrength.Moderate,
                    EvidenceSource.PathHeuristic,
                    $"Directory name is the \"{vendorNamespace}\" vendor namespace, which is a different publisher from \"{app.Publisher}\".",
                    false);
                continue;
            }

            if (inApplicationData)
            {
                Add(
                    evidenceByApp,
                    app,
                    EvidenceType.KnownApplicationPath,
                    EvidenceStrength.Weak,
                    EvidenceSource.PathHeuristic,
                    $"Location is a per-user application data area ({input.Category.Label()}).",
                    true);
            }

            if (parentIsAttributed)
            {
                Add(
                    evidenceByApp,
                    app,
                    EvidenceType.ParentDirectoryMatch,
                    EvidenceStrength.Weak,
                    EvidenceSource.Derived,
                    "Parent directory is already attributed within this application's scope.",
                    true);
            }
        }
    }

    /// <summary>
    /// The publisher token when the directory name is exactly some installed
    /// publisher's namespace, or null when it is not.
    /// </summary>
    /// <remarks>
    /// Only a single-word directory name counts: <c>Adobe</c> is a vendor
    /// namespace, whereas <c>Adobe Photoshop</c> is a product.
    /// </remarks>
    private string? VendorNamespaceOf(string directoryName)
    {
        var segments = NameSegments(directoryName);
        return segments.Count == 1 && _vendorNamespaces.Contains(segments[0]) ? segments[0] : null;
    }

    /// <summary>
    /// The applications that a detector has already proposed. Context evidence
    /// may corroborate these, never create new candidates.
    /// </summary>
    private IEnumerable<AppIdentity> ProposedCandidates(Dictionary<string, List<Evidence>> evidenceByApp)
    {
        foreach (var appId in evidenceByApp.Keys)
        {
            if (_appsById.TryGetValue(appId, out var app))
            {
                yield return app;
            }
        }
    }

    /// <summary>
    /// Reads version metadata from a representative executable in the directory.
    /// This is the only detector that touches file content; the probe result is
    /// cached per directory and the number of probes is bounded.
    /// </summary>
    /// <remarks>
    /// <para>Matches on <em>whole folded tokens</em>, never on substring
    /// containment. A measured real-machine failure is the reason:
    /// <c>Program Files\Blackmagic Design\DaVinci Resolve\Electron\electron.exe</c>
    /// reports <c>CompanyName = "GitHub, Inc."</c>, and a substring test let
    /// <c>git</c> match <c>githubinc</c>, attributing a component of DaVinci Resolve
    /// to the Git installation.</para>
    /// <para>Executable metadata describes the binary, not the directory. It is
    /// therefore <see cref="EvidenceKind.Identity"/>: it corroborates an owner, and
    /// cannot by itself establish one.</para>
    /// </remarks>
    private void CollectExecutableMetadataEvidence(
        AttributionInput input,
        Dictionary<string, List<Evidence>> evidenceByApp)
    {
        if (_executableProbes >= _options.MaxExecutableProbes)
        {
            return;
        }

        if (!_executableProbeCache.TryGetValue(input.Path, out ExecutableProbe? binaryMetadata))
        {
            _executableProbes++;
            binaryMetadata = ExecutableProbe.Inspect(input.Path);
            _executableProbeCache[input.Path] = binaryMetadata;
        }

        if (binaryMetadata.IsEmpty)
        {
            return;
        }

        var directorySegments = NameSegments(input.DirectoryName);

        foreach (var app in _apps)
        {
            var appSegments = NameSegments(app.NormalizedName);
            if (appSegments.Count == 0)
            {
                continue;
            }

            foreach (var (field, value) in binaryMetadata.Fields)
            {
                if (!IdentifiesWholeProduct(value, appSegments, RawFoldedTokens(app.DisplayName)))
                {
                    continue;
                }

                // A component binary in this directory that names the product is
                // identity agreement. Its specificity is how much of the product's
                // name the metadata actually accounted for, capped by how much the
                // directory name did, so metadata cannot make a one-token
                // directory match look fully specific.
                var specificity = Math.Min(
                    CoverageOf(value, appSegments),
                    directorySegments.Count == 0 ? 1.0 : CoverageOf(input.DirectoryName, appSegments));

                Add(
                    evidenceByApp,
                    app,
                    EvidenceType.ExecutableMetadataMatch,
                    EvidenceStrength.Strong,
                    EvidenceSource.ExecutableMetadata,
                    $"Executable {field} \"{value}\" names the application \"{app.DisplayName}\".",
                    true,
                    specificity);
                break;
            }
        }
    }

    /// <summary>
    /// True when a metadata value names the whole product rather than sharing a
    /// single fragment of a word with it.
    /// </summary>
    /// <remarks>
    /// <para>Accepted forms: the folded value equals a folded product name, or
    /// <em>every word</em> of the value is a word of the product name. Both the
    /// app's normalized name and its raw tokenization are accepted, because
    /// normalization deliberately drops generic tokens such as <c>App</c> and
    /// <c>Update</c> that a vendor's own version resource legitimately contains.</para>
    /// <para>So <c>"Google Chrome"</c> identifies Google Chrome, <c>"NVIDIA App"</c>
    /// identifies NVIDIA App, and <c>"GitHub, Inc."</c> does <b>not</b> identify
    /// <c>Git</c> — <c>github</c> is a different word, not a longer form of
    /// <c>git</c>. That distinction is a measured real-machine failure:
    /// <c>DaVinci Resolve\Electron\electron.exe</c> reports
    /// <c>CompanyName = "GitHub, Inc."</c>, and matching on a shared prefix
    /// attributed a DaVinci Resolve component to the Git installation.</para>
    /// </remarks>
    internal static bool IdentifiesWholeProduct(string? metadataValue, IReadOnlyList<string> appSegments)
        => IdentifiesWholeProduct(metadataValue, appSegments, []);

    /// <summary>
    /// As above, also accepting the application's raw tokenization.
    /// </summary>
    internal static bool IdentifiesWholeProduct(
        string? metadataValue,
        IReadOnlyList<string> appSegments,
        IReadOnlyList<string> appRawTokens)
    {
        // InternalName and FileDescription are often filenames; the extension is
        // structure, not identity.
        var candidate = metadataValue is null
            ? null
            : System.Text.RegularExpressions.Regex.Replace(metadataValue, @"\.(exe|dll|com|sys)$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var valueSegments = NameSegments(candidate);
        if (valueSegments.Count == 0)
        {
            return false;
        }

        return MatchesAgainst(valueSegments, appSegments) || MatchesAgainst(valueSegments, appRawTokens);
    }

    /// <summary>
    /// True when the metadata names the same product as the application, tolerating
    /// the legal-form suffixes vendors append to their own company name
    /// (<c>"Acme, Inc."</c> for the product <c>Acme</c>).
    /// </summary>
    /// <remarks>
    /// A company name is not a product name, so this only reaches the same verdict
    /// when the remaining words are a subset of the product's — the DaVinci/Electron
    /// case still fails because <c>github</c> is not <c>git</c>.
    /// </remarks>
    private static bool MatchesIgnoringLegalSuffix(IReadOnlyList<string> valueSegments, IReadOnlyList<string> appSegments)
    {
        if (appSegments.Count == 0)
        {
            return false;
        }

        var trimmed = valueSegments
            .Where(t => !LegalFormTokens.Contains(t))
            .ToArray();

        return trimmed.Length > 0 && trimmed.All(appSegments.Contains);
    }

    private static readonly HashSet<string> LegalFormTokens = new(StringComparer.Ordinal)
    {
        "inc", "llc", "ltd", "limited", "corp", "corporation", "incorporated",
        "gmbh", "plc", "pty", "srl", "bv", "nv", "ag", "sa", "spa", "co", "company", "group",
    };

    private static bool MatchesAgainst(IReadOnlyList<string> valueSegments, IReadOnlyList<string> appSegments)
    {
        if (appSegments.Count == 0)
        {
            return false;
        }

        if (string.Equals(string.Concat(valueSegments), string.Concat(appSegments), StringComparison.Ordinal))
        {
            return true;
        }

        return MatchesIgnoringLegalSuffix(valueSegments, appSegments);
    }

    /// <summary>Splits a value into folded words without applying noise-word stripping.</summary>
    private static List<string> RawFoldedTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([' ', '\t', '-', '_', '.', ',', '(', ')', '[', ']', '®', '™', '©'], StringSplitOptions.RemoveEmptyEntries)
            .Select(TextNormalizer.Fold)
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>
    /// The fraction of the application's name tokens that <paramref name="value"/>
    /// accounts for, as a 0..1 specificity.
    /// </summary>
    /// <remarks>
    /// The measured discriminating feature from the Task 02 spike. It is a
    /// heuristic, not a law: <c>0.50</c> happens to separate every observed false
    /// positive from every observed success, and it is used as a floor rather than
    /// as proof.
    /// </remarks>
    internal static double CoverageOf(string? value, IReadOnlyList<string> appSegments)
    {
        if (appSegments.Count == 0)
        {
            return 0.0;
        }

        var valueSegments = NameSegments(value);
        if (valueSegments.Count == 0)
        {
            return 0.0;
        }

        var matched = appSegments.Count(token => valueSegments.Contains(token));
        return Math.Min(1.0, (double)matched / appSegments.Count);
    }

    /// <summary>
    /// Adds the signals that <em>reduce</em> confidence, including the ones that
    /// describe the whole location rather than one candidate.
    /// </summary>
    private void AddBoundingEvidence(AttributionInput input, Dictionary<string, List<Evidence>> evidenceByApp)
    {
        // A vendor namespace containing several installed products is the
        // canonical shared-ownership case: "AppData\Local\Google" must not
        // silently become "Google Chrome".
        var perPublisher = new Dictionary<string, List<AppIdentity>>(StringComparer.Ordinal);
        foreach (var app in _apps)
        {
            if (app.NormalizedPublisher.Length == 0)
            {
                continue;
            }

            if (!perPublisher.TryGetValue(app.NormalizedPublisher, out var list))
            {
                list = [];
                perPublisher[app.NormalizedPublisher] = list;
            }

            list.Add(app);
        }

        var directoryToken = TextNormalizer.Fold(input.DirectoryName);
        foreach (var (publisherToken, products) in perPublisher)
        {
            if (products.Count < 2
                || !string.Equals(directoryToken, publisherToken, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var product in products)
            {
                if (DeclaresThisExactDirectory(product, input.NormalizedPath))
                {
                    // The products themselves registered this directory as their
                    // install location. That is a genuine shared vendor namespace,
                    // so it must be reported as SHARED rather than talked down:
                    // penalising it here would subtract from the very evidence that
                    // identifies it, and a vendor directory is not an error, it is
                    // a boundary AppTrace descends through.
                    Add(
                        evidenceByApp,
                        product,
                        EvidenceType.PublisherMatch,
                        EvidenceStrength.Weak,
                        EvidenceSource.Registry,
                        $"Directory is a vendor namespace shared by {products.Count} installed products, and " +
                        $"\"{product.DisplayName}\" registers it as its install location.",
                        true);
                    continue;
                }

                Add(
                    evidenceByApp,
                    product,
                    EvidenceType.SharedPublisherDirectory,
                    EvidenceStrength.Moderate,
                    EvidenceSource.PathHeuristic,
                    $"Vendor directory is shared by {products.Count} installed products from the same publisher " +
                    $"({string.Join(", ", products.Select(p => p.DisplayName).Take(4))}).",
                    false);
            }
        }

        // If several applications still look plausible, say so explicitly: this
        // is the evidence that turns a decision into SHARED or AMBIGUOUS.
        var plausible = evidenceByApp
            .Where(kv => kv.Value.Any(e => e.SupportsAttribution && e.Strength >= EvidenceStrength.Moderate))
            .Select(kv => kv.Key)
            .ToList();

        if (plausible.Count > 1)
        {
            foreach (var appId in plausible)
            {
                Add(
                    evidenceByApp,
                    _appsById[appId],
                    EvidenceType.MultipleCandidateOwners,
                    EvidenceStrength.Moderate,
                    EvidenceSource.Derived,
                    $"{plausible.Count} installed applications are plausible owners of this location.",
                    false);
            }
        }
    }

    // ---------------------------------------------------------------------
    // Scoring and classification
    // ---------------------------------------------------------------------

    /// <summary>Upper bound on stored candidates per location, to bound memory.</summary>
    private const int MaxCandidatesPerLocation = 16;

    /// <summary>
    /// Materialises the scored candidates for one location.
    /// </summary>
    /// <remarks>
    /// Applications with nothing but contradicting records are dropped unless
    /// they were near the top: a shared vendor namespace can make hundreds of
    /// applications technically "candidates", and keeping all of them costs
    /// memory per directory across a whole-disk scan while telling the user
    /// nothing.
    /// </remarks>
    private static List<CandidateOwner> BuildCandidates(Dictionary<string, List<Evidence>> evidenceByApp)
    {
        var scored = new List<CandidateOwner>(evidenceByApp.Count);
        foreach (var (appId, evidence) in evidenceByApp)
        {
            if (evidence.Count == 0)
            {
                continue;
            }

            var hasSupport = evidence.Any(e => e.SupportsAttribution);
            var score = evidence.Sum(e => e.Weight);
            if (!hasSupport && score <= -15)
            {
                // Purely a "not this one" note. Useful only for an application
                // that is otherwise plausible, which is checked below.
                continue;
            }

            scored.Add(new CandidateOwner
            {
                AppId = appId,
                Evidence = evidence
                    .OrderByDescending(e => e.SupportsAttribution)
                    .ThenByDescending(e => e.Weight)
                    .ToArray(),
                Score = score,
                Accepted = false,
            });
        }

        return scored
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.AppId, StringComparer.Ordinal)
            .Take(MaxCandidatesPerLocation)
            .ToList();
    }

    /// <summary>
    /// The classification ladder. Evidence kind decides the ceiling; the score
    /// only orders and corroborates within it.
    /// </summary>
    /// <remarks>
    /// <para><b>The rule this implements: evidence must earn the strength of the
    /// claim it supports.</b> Phase 0 summed heterogeneous records, so
    /// <c>NormalizedNameMatch</c> (+30) plus <c>KnownApplicationPath</c> (+8)
    /// reached HIGH even though neither record shows the application ever touched
    /// the path. Adding the generic structure of "this is an application-data
    /// location" to a name resemblance is not evidence of ownership.</para>
    /// <list type="bullet">
    /// <item><b>Decisive contradiction</b> forbids the claim outright, however many
    /// weak supporting records accumulate.</item>
    /// <item><b>CONFIRMED</b> still means the application's own registration names
    /// this exact directory. Existing decisive semantics are preserved rather than
    /// flattened for uniformity.</item>
    /// <item><b>HIGH</b> now means the application has independent provenance
    /// linking it to this path, <em>and</em> identity evidence that is specific
    /// enough to agree. A name match alone can no longer reach it.</item>
    /// <item><b>MEDIUM</b> is the honest ceiling for identity evidence without
    /// provenance, however strong the name match looks.</item>
    /// </list>
    /// </remarks>
    private static Classification ClassifyCandidate(CandidateOwner candidate)
    {
        var supporting = candidate.Supporting.ToArray();
        if (supporting.Length == 0)
        {
            return Classification.Unknown;
        }

        var contradicting = candidate.Contradicting.ToArray();

        // GATE 1: a decisive contradiction is not a weight. PublisherMismatch says a
        // differently identified owner holds this scope; no amount of weak support
        // may outvote that.
        if (contradicting.Any(e => e.Contradiction == ContradictionKind.Decisive))
        {
            return Classification.Unknown;
        }

        var kinds = supporting.Select(e => e.Kind).ToHashSet();
        var types = supporting.Select(e => e.Type).ToHashSet();

        var hasProvenance = kinds.Contains(EvidenceKind.Provenance);
        var hasIdentity = kinds.Contains(EvidenceKind.Identity);

        // Identity strong enough to corroborate a provenance anchor: an exact name
        // match, product metadata, or a scoped match that accounts for at least half
        // the application's name tokens. The 0.5 floor is a measured discriminator
        // from the Task 02 spike, not a statistical law.
        var corroboratingIdentity = supporting.Any(e =>
            e.Kind == EvidenceKind.Identity
            && e.Specificity >= MinimumCorroboratingSpecificity
            && e.Type is EvidenceType.ExactDirectoryNameMatch
                or EvidenceType.NormalizedNameMatch
                or EvidenceType.ExecutableMetadataMatch);

        // GATE 2: the application's own registration names this exact directory.
        if (types.Contains(EvidenceType.DeclaredInstallLocation))
        {
            return Classification.Confirmed;
        }

        if (hasProvenance && corroboratingIdentity)
        {
            return Classification.High;
        }

        if (hasProvenance)
        {
            // Anchored but not identified: the registry says the application lives
            // in this tree, while the name does not corroborate that this exact
            // directory is the application's. Strong, but not a HIGH claim.
            return Classification.Medium;
        }

        // No provenance. Identity evidence alone may never exceed MEDIUM, and
        // structure (including KnownApplicationPath) may not raise it.
        if (hasIdentity)
        {
            return Classification.Medium;
        }

        if (contradicting.Length == 0
            && supporting.Length >= 2
            && supporting.Any(e => e.Strength >= EvidenceStrength.Moderate))
        {
            return Classification.Medium;
        }

        return Classification.Low;
    }

    /// <summary>
    /// Classifies the location as a whole. A location with several accepted
    /// owners is SHARED when those owners are equally plausible, and AMBIGUOUS
    /// when one of them leads without being conclusive.
    /// </summary>
    private static Classification ClassifyLocation(IReadOnlyList<CandidateOwner> candidates)
    {
        var accepted = candidates.Where(c => c.Accepted).ToList();
        if (accepted.Count == 0)
        {
            return Classification.Unknown;
        }

        if (accepted.Count == 1)
        {
            return accepted[0].Classification;
        }

        var top = accepted[0].Score;
        return accepted.Count(c => top - c.Score <= 1) > 1
            ? Classification.Shared
            : Classification.Ambiguous;
    }

    /// <summary>
    /// Selects the candidates AppTrace stands behind and derives the
    /// classification for each one.
    /// </summary>
    /// <remarks>
    /// A candidate is accepted when it carries supporting evidence and scores
    /// within <see cref="TieScoreWindow"/> of the best candidate. Accepting
    /// several candidates is a feature, not a failure: it is how the SHARED and
    /// AMBIGUOUS outcomes are represented without forcing a single owner.
    /// The decision loop runs after the initial classification so a second pass
    /// can distinguish SHARED from AMBIGUOUS.
    /// </remarks>
    /// <summary>
    /// True when a candidate's evidence is strong enough to close a directory
    /// subtree: it reaches a classification that means "this application owns this
    /// location", namely HIGH or CONFIRMED.
    /// </summary>
    /// <remarks>
    /// Derived from the classification rather than from a separate list of evidence
    /// types, so "may stop descending" and "may claim confident ownership" can never
    /// drift apart. MEDIUM and below keep the descent going, which is what makes a
    /// vendor namespace resolve into its products instead of being absorbed.
    /// </remarks>
    private static bool MeetsOwnershipBar(CandidateOwner candidate)
        => ClassifyCandidate(candidate) is Classification.High or Classification.Confirmed;

    private (List<CandidateOwner> Candidates, bool Established, string Reason) DecideOwnership(
        List<CandidateOwner> candidates)
    {
        var decided = new List<CandidateOwner>(candidates.Count);
        if (candidates.Count > 0)
        {
            var best = candidates[0].Score;

            // Whether anything here is strong enough to close a subtree. When
            // nothing is, the only outcome available is to report candidate owners
            // and keep descending, so a weaker bar applies to the candidates.
            var anythingMeetsStrongBar = candidates.Any(MeetsOwnershipBar);

            foreach (var candidate in candidates)
            {
                // A candidate must carry at least one supporting record. Structure
                // alone never establishes ownership, and since Task 04 the
                // classification ladder enforces the rest: identity without
                // provenance can corroborate but cannot reach HIGH.
                var hasSupportingEvidence = candidate.Supporting.Any();
                var meetsBar = MeetsOwnershipBar(candidate)
                    || (!anythingMeetsStrongBar && candidate.Score >= NamespaceEvidenceBar);

                var accepted = candidate.Score > 0
                    && hasSupportingEvidence
                    && meetsBar
                    && best - candidate.Score <= TieScoreWindow;

                decided.Add(candidate with
                {
                    Accepted = accepted,
                    Classification = accepted ? ClassifyCandidate(candidate) : Classification.Unknown,
                });
            }
        }

        var acceptedOwners = decided.Where(c => c.Accepted).ToList();
        if (acceptedOwners.Count == 0)
        {
            return (decided, false, "No application-specific evidence was found; descending to look for a tighter ownership boundary.");
        }

        if (acceptedOwners.Count == 1)
        {
            var only = acceptedOwners[0];
            if (only.Score >= StopScoreThreshold)
            {
                return (decided, true, $"Ownership established for \"{NameOf(only.AppId)}\" by {only.Supporting.Count()} supporting evidence record(s).");
            }

            return (decided, false, $"Only weak evidence for \"{NameOf(only.AppId)}\" (score {only.Score}); descending to confirm or refute.");
        }

        // Several owners. SHARED means the candidates are equally plausible;
        // AMBIGUOUS means one leads but not by enough to exclude the others.
        var top = acceptedOwners[0].Score;
        var tiedAtTop = acceptedOwners.Count(c => top - c.Score <= 1);
        var kind = tiedAtTop > 1 ? "shared by" : "ambiguous between";
        return (decided, false, $"Location is {kind} {acceptedOwners.Count} applications " +
            $"({string.Join(", ", acceptedOwners.Select(c => NameOf(c.AppId)))}); descending to separate their data.");
    }

    private string NameOf(string appId)
        => _appsById.TryGetValue(appId, out var app) ? app.DisplayName ?? appId : appId;

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static void Add(
        Dictionary<string, List<Evidence>> evidenceByApp,
        AppIdentity app,
        EvidenceType type,
        EvidenceStrength strength,
        EvidenceSource source,
        string description,
        bool supports,
        double specificity = 1.0)
    {
        if (!evidenceByApp.TryGetValue(app.Id, out var list))
        {
            list = [];
            evidenceByApp[app.Id] = list;
        }

        var weight = EvidenceWeights.WeightOf(type);
        if (!supports && weight > 0)
        {
            // A contradicting record always reduces the score.
            weight = -Math.Abs(weight);
        }

        list.Add(new Evidence
        {
            Type = type,
            Description = description,
            Strength = strength,
            Source = source,
            SupportsAttribution = supports,
            Weight = weight,
            Specificity = specificity,
        });
    }

    private static string? ParentOf(string normalizedPath)
    {
        var index = normalizedPath.LastIndexOf(Path.DirectorySeparatorChar);
        return index <= 2 ? null : normalizedPath[..index];
    }

    private static List<string> PathSegments(string normalizedPath)
        => normalizedPath
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.Length > 1)
            .ToList();
}

/// <summary>Tunables for the attribution engine.</summary>
public sealed class AttributionOptions
{
    /// <summary>Upper bound on executable metadata reads per scan.</summary>
    public int MaxExecutableProbes { get; init; } = 500;
}
