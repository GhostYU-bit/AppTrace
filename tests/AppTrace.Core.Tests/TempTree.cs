using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AppTrace.Core.Tests;

/// <summary>
/// Creates and disposes a synthetic directory tree so filesystem behaviour can be
/// tested without depending on the developer's real machine.
/// </summary>
/// <remarks>
/// The base directory is probed once per process: some environments confine the
/// test host so that it cannot create directories in the shared temp folder, and
/// a test that fails for that reason tells us nothing about AppTrace.
/// </remarks>
internal sealed class TempTree : IDisposable
{
    private static readonly ConcurrentDictionary<string, bool> BaseCandidates = new(StringComparer.OrdinalIgnoreCase);
    private static string? _resolvedBase;

    private readonly string _root;

    public TempTree()
    {
        _root = Path.Combine(ResolveBase(), "apptrace-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public string Dir(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Creates a file of exactly <paramref name="bytes"/> bytes.</summary>
    public string File(string relativePath, int bytes)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None);
        if (bytes > 0)
        {
            var buffer = new byte[bytes];
            RandomNumberGenerator.Fill(buffer);
            stream.Write(buffer);
        }

        return full;
    }

    /// <summary>Total bytes currently stored beneath this tree, used to check accounting.</summary>
    public long MeasureWithSystemApis()
        => Directory
            .EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }

    private static string ResolveBase()
    {
        if (_resolvedBase is not null)
        {
            return _resolvedBase;
        }

        foreach (var candidate in Candidates())
        {
            if (!BaseCandidates.ContainsKey(candidate) && !TryPrepare(candidate))
            {
                continue;
            }

            BaseCandidates[candidate] = true;
            _resolvedBase = candidate;
            return candidate;
        }

        throw new InvalidOperationException(
            "No writable directory is available for test fixtures. Checked: " + string.Join(", ", Candidates()));
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(Path.GetTempPath(), "apptrace-fixtures");
        yield return Path.Combine(AppContext.BaseDirectory, "apptrace-fixtures");
    }

    private static bool TryPrepare(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".write-probe-" + Guid.NewGuid().ToString("N"));
            System.IO.File.WriteAllText(probe, "probe");
            System.IO.File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
