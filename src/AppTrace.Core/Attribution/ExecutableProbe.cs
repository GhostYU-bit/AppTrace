namespace AppTrace.Core.Attribution;

using System.Diagnostics;
using System.Runtime.Versioning;

/// <summary>
/// Version metadata read from a representative executable inside a directory.
/// </summary>
/// <remarks>
/// Binary metadata is the only signal in Phase 0 that comes from file content
/// rather than from a name. It is used the way Bulk Crap Uninstaller uses
/// CompanyName/ProductName matching: as corroboration, never as proof, and only
/// from a small bounded number of files per scan.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ExecutableProbe
{
    /// <summary>Executable names that carry no product identity.</summary>
    private static readonly HashSet<string> IgnoredExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "unins000.exe", "unins001.exe", "uninstall.exe", "setup.exe", "install.exe",
        "update.exe", "updater.exe", "vc_redist.x64.exe", "vc_redist.x86.exe",
        "dotnetfx.exe", "python.exe", "node.exe", "crashpad_handler.exe",
    };

    private ExecutableProbe()
    {
    }

    public IReadOnlyList<KeyValuePair<string, string>> Fields { get; private init; } = [];

    public string? ExecutablePath { get; private init; }

    public bool IsEmpty => Fields.Count == 0;

    /// <summary>
    /// Inspects at most one executable directly inside <paramref name="directory"/>.
    /// Returns an empty probe when there is nothing readable, which is a normal
    /// outcome rather than an error.
    /// </summary>
    public static ExecutableProbe Inspect(string directory)
    {
        string? candidate = null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (IgnoredExecutables.Contains(name))
                {
                    continue;
                }

                candidate = file;
                break;
            }
        }
        catch (Exception e) when (Scanning.DirectoryWalker.IsRecoverable(e))
        {
            return new ExecutableProbe();
        }

        if (candidate is null)
        {
            return new ExecutableProbe();
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(candidate);
            var fields = new List<KeyValuePair<string, string>>(4);
            AddField(fields, "ProductName", info.ProductName);
            AddField(fields, "FileDescription", info.FileDescription);
            AddField(fields, "CompanyName", info.CompanyName);
            AddField(fields, "InternalName", info.InternalName);

            return new ExecutableProbe { Fields = fields, ExecutablePath = candidate };
        }
        catch (Exception e) when (Scanning.DirectoryWalker.IsRecoverable(e))
        {
            return new ExecutableProbe { ExecutablePath = candidate };
        }
    }

    private static void AddField(List<KeyValuePair<string, string>> fields, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields.Add(new KeyValuePair<string, string>(name, value.Trim()));
        }
    }
}
