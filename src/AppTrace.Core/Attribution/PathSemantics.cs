namespace AppTrace.Core.Attribution;

/// <summary>
/// What kind of storage structure a path segment belongs to.
/// </summary>
/// <remarks>
/// <para>This answers one question and only one:</para>
/// <blockquote>Is this segment part of an established storage structure whose
/// children are <em>content</em> rather than applications?</blockquote>
/// <para>It is deliberately not a filesystem ontology, and it is deliberately not
/// a list of "generic words". Recognising that <c>node_modules</c> is a dependency
/// tree is a reusable, product-independent statement about how software is laid
/// out. Declaring that <c>sdk</c> or <c>helper</c> is "generic" would be quite a
/// different thing — those words are ambiguous vocabulary whose meaning depends on
/// the path around them, and a blacklist of them would memorise today's false
/// positives instead of preventing tomorrow's.</para>
/// <para>Only <see cref="StructureKind.Unknown"/> permits a segment name to
/// establish product identity on its own.</para>
/// </remarks>
public enum StructureKind
{
    /// <summary>Nothing structural is known about this segment.</summary>
    Unknown = 0,

    /// <summary>
    /// A directory of installed dependencies, e.g. <c>node_modules</c>,
    /// <c>site-packages</c>. Everything beneath holds package names.
    /// </summary>
    PackageDependencyTree,

    /// <summary>
    /// A package manager's cache, e.g. <c>npm-cache</c>, <c>_npx</c>, <c>_cacache</c>,
    /// <c>.nuget\packages</c>. Everything beneath holds cache keys and packages.
    /// </summary>
    PackageManagerCache,

    /// <summary>
    /// An embedded runtime or sandbox belonging to a product, e.g.
    /// <c>sandbox_runtime</c>, <c>runtime</c>, <c>cef</c>. Everything beneath holds
    /// runtime components.
    /// </summary>
    ApplicationRuntime,

    /// <summary>
    /// A UI toolkit or framework directory, e.g. <c>QtQuick</c>, <c>Electron</c>.
    /// Everything beneath holds framework modules and styles.
    /// </summary>
    ComponentFramework,

    /// <summary>
    /// An application's profile or user-data store, e.g. <c>User Data</c>.
    /// </summary>
    ApplicationProfile,

    /// <summary>
    /// A cache below an application, e.g. <c>Cache</c>, <c>GPUCache</c>.
    /// </summary>
    ApplicationCache,

    /// <summary>A log directory, e.g. <c>logs</c>.</summary>
    Logs,

    /// <summary>Scratch or promotional content shipped inside a product tree.</summary>
    Temporary,

    /// <summary>
    /// An update or patch staging tree, e.g. <c>update</c>, <c>packages</c>,
    /// <c>SquirrelTemp</c>.
    /// </summary>
    ApplicationUpdateTree,

    /// <summary>
    /// A store whose entries <em>refer to</em> other products rather than being
    /// named after their owner, e.g. <c>Recommendations</c>. A product name found
    /// directly inside one of these is a subject reference, which is what makes a
    /// <c>RelatedTo</c> assertion supportable.
    /// </summary>
    SubjectData,
}

/// <summary>
/// The structure established by one path segment, and whether that structure stops
/// the segment's name from acting as an application identity.
/// </summary>
/// <param name="Segment">The segment this describes.</param>
/// <param name="Kind">What the segment establishes.</param>
/// <param name="SuppressesIdentity">
/// True when a confident, product-specific name match on this segment would be a
/// structural misreading. This is the flag that prevents a leaf token such as
/// <c>sdk</c> or <c>node</c> from being treated as an application name simply
/// because the path above it is a cache, a dependency tree or a runtime.
/// </param>
/// <param name="SuppressedByStructure">
/// True when the suppression comes from an established structure in the ancestry —
/// the segment is <em>content</em> of a dependency tree, a cache or a runtime —
/// rather than merely from its depth.
/// </param>
/// <remarks>
/// The two reasons for suppression are not interchangeable. A deep segment is
/// suppressed because it is probably naming a component of whatever contains it,
/// which is a statement about <em>position</em>; a segment inside
/// <c>node_modules</c> is suppressed because it is a package name, which is a
/// statement about <em>meaning</em>. Only the positional reason may be lifted to let
/// an independently proposed candidate be corroborated by its own name, because a
/// package name is never a product name however it was proposed.
/// </remarks>
public readonly record struct SegmentSemantics(
    string Segment,
    StructureKind Kind,
    bool SuppressesIdentity,
    bool SuppressedByStructure = false);

/// <summary>
/// Reads a path the way a person does: as a sequence of meaning-carrying segments
/// rather than as a bag of directory names.
/// </summary>
/// <remarks>
/// <para><b>The rule this class exists to enforce:</b> a word in a path is not an
/// application identity until the structure around it gives that word the right to
/// mean one. <c>Vivaldi</c> directly under a scan root is a product. <c>node</c>
/// underneath <c>node_modules</c> is a package name. The same string, two different
/// meanings, and the difference is entirely structural.</para>
/// </remarks>
public static class PathSemantics
{
    /// <summary>
    /// Folded segment names that establish a structure whose children are content.
    /// </summary>
    /// <remarks>
    /// Every entry earns its place by being a <em>reusable storage concept</em>, not
    /// by being a word that once appeared in a false positive. This is why the list
    /// is short and why it contains no entry for <c>sdk</c>, <c>helper</c>,
    /// <c>tool</c>, <c>universal</c>, <c>zip</c>, <c>client</c>, <c>service</c> or
    /// <c>manager</c>: those are ambiguous vocabulary, and their meaning has to come
    /// from context and independent evidence rather than from a curated blacklist.
    /// </remarks>
    private static readonly Dictionary<string, StructureKind> Anchors = new(StringComparer.OrdinalIgnoreCase)
    {
        // Dependency trees: the children are installed packages.
        ["node_modules"] = StructureKind.PackageDependencyTree,
        ["site-packages"] = StructureKind.PackageDependencyTree,
        ["dist-packages"] = StructureKind.PackageDependencyTree,
        ["bower_components"] = StructureKind.PackageDependencyTree,

        // "vendor" is deliberately NOT here. It is a dependency directory in some
        // ecosystems, but it is also one of the commonest vendor-namespace names in
        // Program Files, and the two are indistinguishable by name. Treating it as a
        // dependency tree made every product directory below "Program Files\Vendor"
        // invisible, which is a far worse failure than missing a dependency tree that
        // "node_modules" and "site-packages" already cover in practice. Vendor
        // namespaces are handled as ownership boundaries instead, where the evidence
        // for them actually exists.

        // Package manager caches: the children are cache keys and packages.
        ["npm-cache"] = StructureKind.PackageManagerCache,
        ["_npx"] = StructureKind.PackageManagerCache,
        ["_cacache"] = StructureKind.PackageManagerCache,
        ["_logs"] = StructureKind.Logs,
        ["pip"] = StructureKind.PackageManagerCache,
        ["nuget"] = StructureKind.PackageManagerCache,
        ["packages"] = StructureKind.ApplicationUpdateTree,

        // Embedded runtimes and sandboxes: the children are runtime components.
        ["sandbox_runtime"] = StructureKind.ApplicationRuntime,
        ["runtime"] = StructureKind.ApplicationRuntime,
        ["runtimes"] = StructureKind.ApplicationRuntime,
        ["jre"] = StructureKind.ApplicationRuntime,
        ["cef"] = StructureKind.ApplicationRuntime,
        ["webview2"] = StructureKind.ApplicationRuntime,
        ["blink"] = StructureKind.ApplicationRuntime,

        // UI toolkits and embedded application frameworks.
        ["qtquick"] = StructureKind.ComponentFramework,
        ["qt"] = StructureKind.ComponentFramework,
        ["electron"] = StructureKind.ComponentFramework,
        ["chromium"] = StructureKind.ComponentFramework,
        ["cefsharp"] = StructureKind.ComponentFramework,

        // Product-owned stores that hold generated content rather than the product.
        ["user data"] = StructureKind.ApplicationProfile,
        ["userdata"] = StructureKind.ApplicationProfile,
        ["cache"] = StructureKind.ApplicationCache,
        ["caches"] = StructureKind.ApplicationCache,
        ["gpucache"] = StructureKind.ApplicationCache,
        ["code cache"] = StructureKind.ApplicationCache,
        ["shadercache"] = StructureKind.ApplicationCache,
        ["dxcache"] = StructureKind.ApplicationCache,
        ["glcache"] = StructureKind.ApplicationCache,
        ["nv_cache"] = StructureKind.ApplicationCache,

        ["logs"] = StructureKind.Logs,
        ["log"] = StructureKind.Logs,

        ["temp"] = StructureKind.Temporary,
        ["tmp"] = StructureKind.Temporary,
        ["promo"] = StructureKind.Temporary,

        ["update"] = StructureKind.ApplicationUpdateTree,
        ["updates"] = StructureKind.ApplicationUpdateTree,
        ["updater"] = StructureKind.ApplicationUpdateTree,
        ["squirreltemp"] = StructureKind.ApplicationUpdateTree,

        // Stores whose entries refer to other products. These are where a product
        // name legitimately means "this content is about that product" rather than
        // "that product owns this", and they are the only context in which Task 06
        // will assert a relationship.
        ["recommendations"] = StructureKind.SubjectData,
        ["recommendation"] = StructureKind.SubjectData,
        ["catalog"] = StructureKind.SubjectData,
        ["catalogue"] = StructureKind.SubjectData,
        ["favorites"] = StructureKind.SubjectData,
        ["favourites"] = StructureKind.SubjectData,
        ["wishlist"] = StructureKind.SubjectData,
        ["playlists"] = StructureKind.SubjectData,
        ["recent"] = StructureKind.SubjectData,
    };

    /// <summary>
    /// Analyses a path from a scan root down to the directory being judged.
    /// </summary>
    /// <param name="normalizedPath">
    /// Fully qualified, lower-cased path of the directory being judged.
    /// </param>
    /// <param name="normalizedScanRoot">
    /// Fully qualified, lower-cased path of the scan root it sits under, when known.
    /// </param>
    /// <param name="depthBelowRoot">
    /// The depth the scanner reports for this directory: 0 for the root itself and 1
    /// for a directory directly inside it. Used to anchor the positional rule, so it
    /// holds for paths that are not physically rooted in a scan root — a synthetic
    /// fixture, or an install location resolved outside the scanned scope.
    /// </param>
    public static IReadOnlyList<SegmentSemantics> Analyse(
        string normalizedPath,
        string? normalizedScanRoot,
        int depthBelowRoot = 0)
    {
        var segments = Split(normalizedPath);
        if (segments.Count == 0)
        {
            return [];
        }

        var rootSegments = normalizedScanRoot is null ? [] : Split(normalizedScanRoot);

        // Where the scan root ends within this path. The scanner's reported depth for
        // this directory is the authority, because it holds for every path the
        // engine judges — inside a scan root, resolved outside the scanned scope, or
        // synthetic. It places the directory being judged at exactly its reported
        // depth, and its ancestors above that.
        var start = segments.Count - Math.Max(depthBelowRoot, 0);

        // If the root is a real prefix of this path it must agree; when it does not,
        // trusting the root would shift every segment, so the depth wins. Only use
        // the root's own length when the depth is unknown (0).
        if (depthBelowRoot == 0 && rootSegments.Count < segments.Count)
        {
            start = rootSegments.Count;
        }

        var result = new List<SegmentSemantics>(segments.Count);

        // True once some segment strictly above the current one established a
        // structure. An anchor only ever governs what is *inside* it.
        var ancestorEstablishedStructure = false;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var depth = i - start;

            var kind = KindOf(segment);

            // Positional rule: an application's own name appears at the top of its
            // tree, not deep inside another product's structure. Below the first two
            // levels a segment names a component of whatever contains it.
            var tooDeepForProductIdentity = depth >= 2;

            // Structural vocabulary is positional. A directory named "Cache" at
            // product level, for a product whose name is Cache, is legitimate
            // identity evidence; the same word nested inside another product's tree
            // is not. So an anchor suppresses identity when something above it
            // already established a structure, or when the segment is itself nested
            // too deeply to be a product root.
            var suppresses = ancestorEstablishedStructure
                || (kind != StructureKind.Unknown && tooDeepForProductIdentity)
                || tooDeepForProductIdentity;

            result.Add(new SegmentSemantics(
                segment,
                kind,
                suppresses,
                // Only an ancestor's established structure makes this segment
                // content. Depth alone is about position, not about meaning.
                SuppressedByStructure: ancestorEstablishedStructure));

            if (kind != StructureKind.Unknown)
            {
                ancestorEstablishedStructure = true;
            }
        }

        return result;
    }

    /// <summary>
    /// True when the directory's own name must not be read as an application name.
    /// </summary>
    /// <remarks>
    /// The directory being judged is the last segment, so this is simply whether
    /// that segment's meaning has already been fixed by what contains it.
    /// </remarks>
    public static bool SuppressesIdentityForLeaf(IReadOnlyList<SegmentSemantics> semantics)
        => semantics.Count > 0 && semantics[^1].SuppressesIdentity;

    /// <summary>
    /// True when the directory's name is suppressed because it is content of an
    /// established structure rather than merely because of where it sits.
    /// </summary>
    /// <remarks>
    /// This is the reason that may never be lifted: inside <c>node_modules</c> or a
    /// package cache, a segment is a package name, and no independent registration
    /// makes a package name into a product name.
    /// </remarks>
    public static bool SuppressedByStructureForLeaf(IReadOnlyList<SegmentSemantics> semantics)
        => semantics.Count > 0 && semantics[^1].SuppressedByStructure;

    /// <summary>
    /// True when the path is inside an established structure at or above the
    /// directory being judged.
    /// </summary>
    public static StructureKind StructureForLeaf(IReadOnlyList<SegmentSemantics> semantics)
        => semantics.Count == 0 ? StructureKind.Unknown : semantics[^1].Kind;

    /// <summary>
    /// True when the path is a variable-size identifier rather than a name, such as
    /// a content hash, GUID or an index.
    /// </summary>
    /// <remarks>
    /// Stores commonly place one of these between the store and its entries:
    /// <c>Recommendations\&lt;hash&gt;\cities_skylines</c>. It carries no meaning of its
    /// own, so context analysis has to look through it rather than treat it as the
    /// entry's parent.
    /// </remarks>
    public static bool IsAnonymousIntermediary(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return true;
        }

        var value = segment.Trim();

        // Pure digits, or digits and separators: an index or a version.
        if (value.Length > 0 && value.All(c => char.IsAsciiDigit(c) || c is '.' or '-' or '_'))
        {
            return true;
        }

        // A hex string long enough that a human word is implausible: 32 hex
        // characters is the shortest common content hash, and GUIDs of 32 hex digits
        // without dashes are covered too.
        var hex = value.Replace("-", string.Empty, StringComparison.Ordinal);
        return hex.Length >= 32 && hex.All(Uri.IsHexDigit);
    }

    private static StructureKind KindOf(string segment)
        => Anchors.TryGetValue(segment, out var kind) ? kind : StructureKind.Unknown;

    private static List<string> Split(string normalizedPath)
        => normalizedPath
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.Length > 1)
            .ToList();
}
