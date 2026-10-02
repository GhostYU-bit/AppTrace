using AppTrace.Core.Model;

namespace AppTrace.Core.Discovery;

/// <summary>
/// Answers, for one directory, which applications Windows itself shows a reach
/// into it.
/// </summary>
/// <remarks>
/// <para>Built once per scan from the anchors <see cref="ProvenanceDiscovery"/>
/// produced, then queried cheaply for every directory. Enumerating services, tasks
/// or shortcuts during attribution would be quadratic, so discovery and querying
/// are separated deliberately.</para>
/// <para>Two directions are indexed, because a registration connects a directory to
/// an application in both:</para>
/// <list type="bullet">
/// <item>The directory <em>contains</em> the registered file, so the anchor is
/// inside it.</item>
/// <item>The directory <em>is an ancestor</em> of the registered file, which is the
/// case that lets an anchor at a product executable establish the owner of the
/// product root.</item>
/// </list>
/// <para>The ancestor direction is bounded. A registration for a file buried deep in
/// one product must not promote every directory above it, so only a limited number
/// of levels are considered and the caller is told which were used.</para>
/// </remarks>
public sealed class ProvenanceIndex
{
    /// <summary>How many levels above a registered file an anchor still applies.</summary>
    public const int MaxAncestorLevels = 2;

    private readonly Dictionary<string, List<ProvenanceAnchor>> _byDirectory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ProvenanceAnchor>> _byAncestor = new(StringComparer.Ordinal);

    public ProvenanceIndex(IReadOnlyList<ProvenanceAnchor> anchors)
    {
        Anchors = anchors;

        foreach (var anchor in anchors)
        {
            Add(_byDirectory, anchor.Directory, anchor);

            // Register the anchor against its bounded ancestors as well.
            var ancestor = anchor.Directory;
            for (var level = 0; level < MaxAncestorLevels; level++)
            {
                var parent = Path.GetDirectoryName(ancestor);
                if (parent is null || parent.Length < 3 || string.Equals(parent, ancestor, StringComparison.Ordinal))
                {
                    break;
                }

                Add(_byAncestor, parent, anchor);
                ancestor = parent;
            }
        }
    }

    public IReadOnlyList<ProvenanceAnchor> Anchors { get; }

    public static ProvenanceIndex Empty { get; } = new([]);

    /// <summary>
    /// The anchors that apply to a directory: those registered inside it, and those
    /// registered at or below it within the bounded ancestor window.
    /// </summary>
    public IReadOnlyList<ProvenanceAnchor> ForDirectory(string normalizedPath)
    {
        if (_byDirectory.Count == 0 && _byAncestor.Count == 0)
        {
            return [];
        }

        var key = normalizedPath.TrimEnd(Path.DirectorySeparatorChar);
        if (!_byDirectory.TryGetValue(key, out var inside))
        {
            inside = null;
        }

        if (!_byAncestor.TryGetValue(key, out var above))
        {
            above = null;
        }

        if (inside is null)
        {
            return (IReadOnlyList<ProvenanceAnchor>?)above ?? [];
        }

        if (above is null)
        {
            return inside;
        }

        var combined = new List<ProvenanceAnchor>(inside.Count + above.Count);
        combined.AddRange(inside);
        combined.AddRange(above);
        return combined;
    }

    /// <summary>Total anchors, for reporting.</summary>
    public int Count => Anchors.Count;

    private static void Add(Dictionary<string, List<ProvenanceAnchor>> index, string key, ProvenanceAnchor anchor)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        list.Add(anchor);
    }
}
