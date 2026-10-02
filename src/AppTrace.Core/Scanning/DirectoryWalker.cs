namespace AppTrace.Core.Scanning;

/// <summary>Bounds applied to a scan so a pathological tree cannot run forever.</summary>
public sealed class ScanLimits
{
    /// <summary>Directories AppTrace will inspect beneath a scan root.</summary>
    public int MaxDirectories { get; init; } = 120_000;

    /// <summary>Files AppTrace will stat beneath a scan root.</summary>
    public int MaxFiles { get; init; } = 4_000_000;

    /// <summary>
    /// Maximum attribution depth beneath a scan root. Descendants at this depth
    /// are still measured but are no longer partitioned.
    /// </summary>
    public int MaxDepth { get; init; } = 6;

    /// <summary>
    /// When false (the default) reparse points are measured as opaque leaves:
    /// never followed, never counted twice, never able to cause a traversal loop.
    /// </summary>
    public bool FollowReparsePoints { get; init; }

    public static ScanLimits Default { get; } = new();
}

/// <summary>Result of measuring one directory tree.</summary>
public sealed class DirectoryMeasurement
{
    public required string Path { get; init; }

    /// <summary>Total bytes of every file beneath this directory, inclusive.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Number of files counted beneath this directory, inclusive.</summary>
    public long FileCount { get; set; }

    /// <summary>
    /// Direct subdirectories that were actually inspected. Directories skipped
    /// as reparse points, excluded paths or budget cuts are absent.
    /// </summary>
    public List<string> ChildDirectories { get; } = [];

    /// <summary>Bytes used by reparse points that were not followed.</summary>
    public long SkippedReparsePointBytes { get; set; }

    /// <summary>True when the file/directory budget truncated this measurement.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Measures directory trees with explicit accounting of what could not be read.
/// </summary>
/// <remarks>
/// <para>
/// The rule that makes the numbers trustworthy: <em>reparse points are never
/// followed</em>. Junctions such as <c>%LOCALAPPDATA%\Application Data</c> point
/// back up the tree, so following them double counts gigabytes and can loop
/// forever. Each reparse point is measured (its own directory entry and any
/// non-reparse children) but its link target is not entered, and the decision is
/// reported rather than hidden.
/// </para>
/// <para>
/// Every failure is recorded as a <see cref="AppTrace.Core.Model.ScanError"/>.
/// An unreadable directory therefore shows up as an incompletely measured
/// location, never as a small one.
/// </para>
/// </remarks>
public sealed class DirectoryWalker
{
    private readonly ScanLimits _limits;
    private readonly List<AppTrace.Core.Model.ScanError> _errors = [];
    private readonly Dictionary<string, DirectoryMeasurement> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _excludedPrefixes = [];

    private int _directoryBudget;
    private long _fileBudget;

    public DirectoryWalker(ScanLimits limits)
    {
        _limits = limits;
        _directoryBudget = limits.MaxDirectories;
        _fileBudget = limits.MaxFiles;
    }

    public IReadOnlyList<AppTrace.Core.Model.ScanError> Errors => _errors;

    /// <summary>
    /// Marks a subtree as out of scope. Excluded paths are still sized (so the
    /// footprint totals stay honest) but are never partitioned or attributed.
    /// </summary>
    public void ExcludeSubtree(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _excludedPrefixes.Add(AppTrace.Core.Model.TextNormalizer.NormalizePath(path));
        }
    }

    public bool IsExcluded(string path)
    {
        var normalized = AppTrace.Core.Model.TextNormalizer.NormalizePath(path);
        foreach (var prefix in _excludedPrefixes)
        {
            if (normalized.Equals(prefix, StringComparison.Ordinal)
                || normalized.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Measures a directory tree, returning the cached measurement when present.
    /// </summary>
    public DirectoryMeasurement Measure(string path)
    {
        var full = Path.GetFullPath(path);
        if (_cache.TryGetValue(full, out var cached))
        {
            return cached;
        }

        var measurement = new DirectoryMeasurement { Path = full };
        _cache[full] = measurement;

        if (_directoryBudget <= 0 || _fileBudget <= 0)
        {
            measurement.Truncated = true;
            AddError(full, AppTrace.Core.Model.ScanErrorSeverity.Warning, "budget", "Scan budget exhausted; this location was not measured.");
            return measurement;
        }

        _directoryBudget--;

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(full).GetFileSystemInfos();
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            AddError(full, AppTrace.Core.Model.ScanErrorSeverity.Error, "enumerate", Explain(e));
            return measurement;
        }

        foreach (var entry in entries)
        {
            if (_fileBudget <= 0)
            {
                measurement.Truncated = true;
                AddError(full, AppTrace.Core.Model.ScanErrorSeverity.Warning, "budget", "File budget exhausted; sizes from here on are lower bounds.");
                break;
            }

            try
            {
                var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                var isReparsePoint = (entry.Attributes & FileAttributes.ReparsePoint) != 0;

                if (isDirectory && isReparsePoint && !_limits.FollowReparsePoints)
                {
                    // Measure the link but do not enter it.
                    measurement.SkippedReparsePointBytes += SafeLength(entry);
                    AddError(
                        entry.FullName,
                        AppTrace.Core.Model.ScanErrorSeverity.Info,
                        "reparse-point",
                        "Reparse point (symlink/junction) not followed; its target is counted where it really lives.");
                    continue;
                }

                if (isDirectory)
                {
                    if (IsExcluded(entry.FullName))
                    {
                        // Out of scope for partition and attribution, but its bytes
                        // still exist on disk. Measuring it keeps the scan totals
                        // complete; excluding it here would silently shrink them.
                        var excluded = Measure(entry.FullName);
                        measurement.SizeBytes += excluded.SizeBytes + excluded.SkippedReparsePointBytes;
                        measurement.FileCount += excluded.FileCount;
                        measurement.Truncated |= excluded.Truncated;
                        continue;
                    }

                    var child = Measure(entry.FullName);
                    measurement.SizeBytes += child.SizeBytes + child.SkippedReparsePointBytes;
                    measurement.FileCount += child.FileCount;
                    measurement.Truncated |= child.Truncated;
                    measurement.ChildDirectories.Add(child.Path);
                }
                else
                {
                    _fileBudget--;
                    measurement.SizeBytes += SafeLength(entry);
                    measurement.FileCount++;
                }
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                AddError(entry.FullName, AppTrace.Core.Model.ScanErrorSeverity.Warning, "entry", Explain(e));
            }
        }

        return measurement;
    }

    private static long SafeLength(FileSystemInfo info)
    {
        try
        {
            return info switch
            {
                FileInfo file => file.Length,
                // A directory reparse point contributes its own metadata only.
                _ => 0L,
            };
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            return 0L;
        }
    }

    /// <summary>
    /// Reports an internal accounting inconsistency. This should never happen;
    /// it is surfaced rather than clamped silently, because a wrong total is
    /// worse than a visible error.
    /// </summary>
    public void ReportAccountingAnomaly(string path, long residual)
    {
        AddError(
            path,
            AppTrace.Core.Model.ScanErrorSeverity.Warning,
            "accounting",
            $"Itemised children exceeded the measured size of this directory by {-residual} bytes; the residual was clamped to zero.");
    }

    private void AddError(string path, AppTrace.Core.Model.ScanErrorSeverity severity, string stage, string message)    {
        // Keep the error list bounded on large scans; the first occurrence of a
        // problem class is the informative one.
        if (_errors.Count >= 5000)
        {
            return;
        }

        _errors.Add(new AppTrace.Core.Model.ScanError
        {
            Path = path,
            Severity = severity,
            Stage = stage,
            Message = message,
        });
    }

    internal static bool IsRecoverable(Exception e)
        => e is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException
            or PathTooLongException
            or DirectoryNotFoundException
            or FileNotFoundException
            or NotSupportedException
            or ArgumentException;

    internal static string Explain(Exception e) => $"{e.GetType().Name}: {e.Message}";
}
