namespace AppTrace.Core.Scanning;

using AppTrace.Core.Attribution;
using AppTrace.Core.Discovery;
using AppTrace.Core.Model;

/// <summary>
/// Runs a complete read-only scan: discover applications, measure the filesystem,
/// attribute locations, and account for every measured byte exactly once.
/// </summary>
/// <remarks>
/// <para><b>Accounting contract.</b> Each byte measured by the scan appears in
/// exactly one <see cref="FootprintItem"/>. A directory that AppTrace stops at
/// keeps its whole subtree. A directory AppTrace descends into does not become an
/// item itself; instead its children are itemised, and the leftover (shared or
/// unattributable bytes such as an excluded subtree) is emitted as a single
/// residual item. <c>ResolveAccountingTests</c> asserts this invariant on
/// synthetic fixtures.</para>
/// <para><b>Read-only.</b> This type only opens directories for enumeration and
/// reads file metadata and executable version resources. It never creates,
/// writes, moves or deletes anything.</para>
/// </remarks>
public sealed class AppTraceScanner
{
    private readonly IReadOnlyList<AppIdentity> _apps;
    private readonly ScanOptions _options;
    private readonly DirectoryWalker _walker;
    private readonly AttributionEngine _engine;
    private readonly ProvenanceYield? _provenanceYield;

    private readonly Dictionary<string, long> _measuredByNormalizedPath = new(StringComparer.Ordinal);

    private readonly List<ItemBuilder> _items = [];
    private bool _truncated;

    public AppTraceScanner(
        IReadOnlyList<AppIdentity> apps,
        ScanOptions? options = null,
        DirectoryWalker? walker = null,
        ProvenanceIndex? provenance = null,
        ProvenanceYield? provenanceYield = null)
    {
        _apps = apps;
        _options = options ?? ScanOptions.Default;
        _walker = walker ?? new DirectoryWalker(_options.Limits);
        _provenanceYield = provenanceYield;
        _engine = new AttributionEngine(
            apps,
            new AttributionOptions { MaxExecutableProbes = _options.Limits.MaxDirectories > 0 ? 500 : 0 },
            provenance);
    }

    /// <summary>Per-source provenance counts, when provenance discovery was run.</summary>
    public ProvenanceYield? ProvenanceYield => _provenanceYield;

    /// <summary>
    /// Discovers and indexes every static Windows provenance anchor for the
    /// discovered applications.
    /// </summary>
    /// <remarks>
    /// Separated from the constructor because it reads the registry, the task store
    /// and the shell, and a caller that only wants name-based attribution should not
    /// pay for or depend on that. Discovery runs once, its anchors are indexed once,
    /// and attribution then queries the index cheaply.
    /// </remarks>
    public static (ProvenanceIndex Index, ProvenanceYield Yield, IReadOnlyList<ScanError> Errors) DiscoverProvenance(
        IReadOnlyList<AppIdentity> apps)
    {
        var discovery = new ProvenanceDiscovery();
        var anchors = discovery.Discover(apps);
        return (new ProvenanceIndex(anchors), discovery.Yield, discovery.Errors);
    }

    public ScanResult Scan(IReadOnlyList<ScanRoot> roots)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var effectiveRoots = roots;
        if (_options.PathFilter is { Length: > 0 } filter)
        {
            effectiveRoots = roots
                .Where(r => r.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        foreach (var exclusion in ScanOptions.DefaultExclusions(KnownFolders.LocalAppData() ?? string.Empty)
                     .Concat(_options.ExcludedSubdirectories))
        {
            _walker.ExcludeSubtree(exclusion);
        }

        // 1. Install locations are resolved first so that "the application is
        //    installed here" always wins over any later name-based guess.
        ResolveDeclaredInstallLocations(roots);

        // 2. The well-known roots.
        foreach (var root in effectiveRoots)
        {
            ResolveRoot(root);
        }

        var errors = _walker.Errors.ToList();
        if (_truncated)
        {
            errors.Add(new ScanError
            {
                Path = string.Empty,
                Severity = ScanErrorSeverity.Warning,
                Stage = "budget",
                Message = "The scan budget was exhausted; some locations were measured only partially or not at all.",
            });
        }

        stopwatch.Stop();
        return new ScanResult
        {
            StartedAt = startedAt,
            Duration = stopwatch.Elapsed,
            Applications = _apps,
            Roots = effectiveRoots,
            Items = _items.Select(i => i.Build()).OrderByDescending(i => i.SizeBytes).ToArray(),
            Errors = errors,
            Truncated = _truncated || errors.Any(e => e.Stage == "budget"),
        };
    }

    // ---------------------------------------------------------------------
    // Resolution
    // ---------------------------------------------------------------------

    private void ResolveRoot(ScanRoot root)
    {
        // The root is a container, not an application location, so it is never
        // attributed. It is still resolved as a node: whatever is not itemised
        // beneath it (loose files at the root, excluded subtrees, budget cuts)
        // becomes a residual item, so the totals stay complete and every byte is
        // still counted exactly once.
        ResolveDirectory(
            root.Path,
            root.Category,
            depth: 0,
            parent: null,
            acceptedAncestors: [],
            ownedAncestors: [],
            isScanRoot: true,
            scanRoot: TextNormalizer.NormalizePath(root.Path));
    }

    /// <summary>
    /// Resolves install locations that fall outside the scan scope, such as a game
    /// installed on a second drive or a portable tool in a user folder.
    /// </summary>
    /// <remarks>
    /// Locations inside a scan root are not resolved here: the normal walk reaches
    /// them, and the engine recognises them directly from each application's
    /// registered install location. The containment check uses the
    /// <em>unfiltered</em> root set on purpose — a <c>--filter</c> run narrows what
    /// is walked, and must not turn every install location into a separately
    /// resolved tree.
    /// </remarks>
    private void ResolveDeclaredInstallLocations(IReadOnlyList<ScanRoot> unfilteredRoots)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in _apps)
        {
            if (app.NormalizedInstallLocation is not { Length: > 0 } normalized)
            {
                continue;
            }

            if (!seen.Add(normalized) || _walker.IsExcluded(normalized))
            {
                continue;
            }

            if (unfilteredRoots.Any(r => IsSameOrDescendant(normalized, TextNormalizer.NormalizePath(r.Path))))
            {
                continue;
            }

            ResolveDirectory(
                normalized,
                LocationCategory.InstallLocation,
                depth: 0,
                parent: null,
                acceptedAncestors: [],
                ownedAncestors: []);
        }
    }

    /// <summary>
    /// Resolves one directory into either a single item or a set of child items
    /// plus a residual item. Returns the bytes accounted for beneath
    /// <paramref name="path"/> so the caller can compute its own residual.
    /// </summary>
    /// <param name="isScanRoot">
    /// True for a well-known location such as <c>%ProgramData%</c>. A scan root is
    /// never attributed to an application: it is a container that always gets
    /// partitioned, and its residual carries no owner.
    /// </param>
    /// <param name="scanRoot">
    /// Normalized path of the root this directory was reached from. Path semantics
    /// uses it to know how deep a segment sits below the scope the scanner actually
    /// judges, which is what makes "product level" a positional fact.
    /// </param>
    private long ResolveDirectory(
        string path,
        LocationCategory category,
        int depth,
        string? parent,
        IReadOnlyList<string> acceptedAncestors,
        IReadOnlyList<OwnedAncestor> ownedAncestors,
        bool isScanRoot = false,
        string? scanRoot = null)
    {
        var normalized = TextNormalizer.NormalizePath(path);
        if (_measuredByNormalizedPath.TryGetValue(normalized, out var alreadyAccounted))
        {
            // Already accounted for by an earlier pass; never count a subtree twice.
            return alreadyAccounted;
        }

        var measurement = _walker.Measure(path);
        _truncated |= measurement.Truncated;
        _measuredByNormalizedPath[normalized] = measurement.SizeBytes;

        var directoryName = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar));
        var attribution = isScanRoot
            ? Unattributed(path)
            : _engine.Evaluate(new AttributionInput
            {
                Path = path,
                NormalizedPath = normalized,
                DirectoryName = directoryName,
                Category = category,
                AcceptedAncestorPaths = acceptedAncestors,
                OwnedAncestors = ownedAncestors,
                NormalizedScanRoot = scanRoot,
                Depth = depth,
            });

        var accepted = attribution.AcceptedOwners;

        // Case 1: ownership is established. The whole measured subtree is this
        // application's, and the subtree is not partitioned further.
        if (attribution.OwnershipEstablished && accepted.Count > 0)
        {
            Emit(path, category, measurement.SizeBytes, measurement, attribution, accepted, depth, parent, stopReason: attribution.StopReason, unattributedReason: null);
            return measurement.SizeBytes;
        }

        var atDepthLimit = depth >= _options.Limits.MaxDepth;
        var budgetExhausted = measurement.Truncated;
        var hasUsableChildren = !atDepthLimit
            && !budgetExhausted
            && measurement.ChildDirectories.Any(IsPartitionable);

        if (!hasUsableChildren)
        {
            // Nothing more can be learned. Report the subtree as one location,
            // with whatever attribution evidence we have, and say why we stopped.
            var reason = atDepthLimit
                ? $"Reached the configured attribution depth ({_options.Limits.MaxDepth}) before ownership was established."
                : budgetExhausted
                    ? "The scan budget stopped this location from being partitioned."
                    : "No subdirectory of this location was available for a finer ownership boundary.";
            Emit(path, category, measurement.SizeBytes, measurement, attribution, accepted, depth, parent, stopReason: reason, unattributedReason: null);
            return measurement.SizeBytes;
        }

        // Case 2: descend. Children are itemised; whatever is left over stays with
        // this directory as a single residual item, so every byte is counted once.
        var childAncestors = accepted.Count > 0
            ? acceptedAncestors.Concat([path]).ToArray()
            : acceptedAncestors;

        // Ownership established here is evidence about everything below it, so it is
        // carried down as an assertion rather than left for each child to rediscover
        // from its own name. Children that own themselves independently outrank it.
        var childOwnedAncestors = attribution.OwnershipEstablished && accepted.Count == 1
            ? ownedAncestors.Concat([new OwnedAncestor(normalized, accepted[0].AppId, attribution.Classification)]).ToArray()
            : ownedAncestors;

        long accounted = 0;
        var childCount = 0;
        foreach (var childPath in measurement.ChildDirectories)
        {
            if (!IsPartitionable(childPath))
            {
                continue;
            }

            childCount++;
            accounted += ResolveDirectory(childPath, category, depth + 1, path, childAncestors, childOwnedAncestors, scanRoot: scanRoot);
        }

        var residual = measurement.SizeBytes - accounted;
        if (residual < 0)
        {
            // A negative residual would mean double counting. Clamp and surface it
            // rather than silently reporting a wrong number.
            _walker.ReportAccountingAnomaly(path, residual);
            residual = 0;
        }

        if (residual > 0 || childCount == 0)
        {
            Emit(
                path,
                category,
                residual,
                measurement,
                attribution,
                accepted,
                depth,
                parent,
                stopReason: childCount == 0
                    ? "Nothing further to partition here."
                    : $"{childCount} subdirectories were itemised separately; the remainder is not attributable to a single application.",
                unattributedReason: childCount == 0
                    ? "No application-specific evidence was found for this location."
                    : "Residual bytes not covered by any itemised subdirectory.");
        }

        return measurement.SizeBytes;
    }

    /// <summary>
    /// The verdict used for locations AppTrace never attributes: scan roots and
    /// the residual bytes left after partitioning. It carries no candidates, so
    /// nothing can accidentally be credited to an application.
    /// </summary>
    private static LocationAttribution Unattributed(string path)
    {
        return new LocationAttribution
        {
            Path = path,
            Candidates = [],
            Classification = Classification.Unknown,
            OwnershipEstablished = false,
            StopReason = "This location is a container or leftover; AppTrace does not attribute it to an application.",
        };
    }

    private bool IsPartitionable(string path)
        => !_walker.IsExcluded(path) && (IsReparsePoint(path) is false);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception e) when (DirectoryWalker.IsRecoverable(e))
        {
            // If the attributes cannot be read, the walker will record the real
            // failure when it measures the directory.
            return false;
        }
    }

    private void Emit(
        string path,
        LocationCategory category,
        long sizeBytes,
        DirectoryMeasurement measurement,
        LocationAttribution attribution,
        IReadOnlyList<CandidateOwner> accepted,
        int depth,
        string? parent,
        string stopReason,
        string? unattributedReason)
    {
        var builder = new ItemBuilder
        {
            Path = path,
            Category = category,
            SizeBytes = sizeBytes,
            MeasuredSizeBytes = measurement.SizeBytes,
            FileCount = measurement.FileCount,
            Depth = depth,
            ParentPath = parent,
            Classification = accepted.Count > 0 ? attribution.Classification : Classification.Unknown,
            CandidateOwners = attribution.Candidates,
            StopReason = stopReason,
            UnattributedReason = unattributedReason ?? (accepted.Count == 0 ? "AppTrace found no evidence that a specific installed application owns this location." : null),
        };

        _items.Add(builder);
    }

    /// <summary>True when <paramref name="normalizedCandidate"/> is the container itself or inside it.</summary>
    private static bool IsSameOrDescendant(string normalizedCandidate, string normalizedContainer)
        => normalizedCandidate.Equals(normalizedContainer, StringComparison.Ordinal)
            || normalizedCandidate.StartsWith(normalizedContainer + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private sealed class ItemBuilder
    {
        public required string Path { get; init; }

        public required LocationCategory Category { get; init; }

        public required long SizeBytes { get; init; }

        public required long MeasuredSizeBytes { get; init; }

        public required long FileCount { get; init; }

        public required int Depth { get; init; }

        public string? ParentPath { get; init; }

        public required Classification Classification { get; init; }

        public required IReadOnlyList<CandidateOwner> CandidateOwners { get; init; }

        public required string StopReason { get; init; }

        public string? UnattributedReason { get; init; }

        public FootprintItem Build() => new()
        {
            Path = Path,
            Category = Category,
            SizeBytes = SizeBytes,
            MeasuredSizeBytes = MeasuredSizeBytes,
            FileCount = FileCount,
            Depth = Depth,
            ParentPath = ParentPath,
            Classification = Classification,
            CandidateOwners = CandidateOwners,
            StopReason = StopReason,
            UnattributedReason = UnattributedReason,
            Errors = [],
        };
    }
}
