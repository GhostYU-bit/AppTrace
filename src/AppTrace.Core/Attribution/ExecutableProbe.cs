namespace AppTrace.Core.Attribution;

using System.Diagnostics;
using System.Runtime.Versioning;

/// <summary>
/// Version metadata read from a representative executable inside a directory.
/// </summary>
/// <remarks>
/// This is the only signal that comes from file content rather than from a name.
/// Version metadata is used the way Bulk Crap Uninstaller uses
/// CompanyName/ProductName matching — as corroboration, never as proof — and the
/// embedded signer names a publisher, which corroborates at publisher level only.
/// Both are read from a small bounded number of files per scan, and both describe
/// the binary rather than the directory that contains it.
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

    private Func<string?>? _signerReader;
    private string? _signerPublisher;
    private bool _signerResolved;

    private ExecutableProbe()
    {
    }

    public IReadOnlyList<KeyValuePair<string, string>> Fields { get; private init; } = [];

    public string? ExecutablePath { get; private init; }

    /// <summary>
    /// Publisher named by the executable's embedded Authenticode certificate, or
    /// <see langword="null"/> when no embedded certificate was observed.
    /// </summary>
    /// <remarks>
    /// <para>Absence is "not observed", never "unsigned": Windows signs many
    /// binaries by catalog rather than by embedding a certificate, so this value
    /// must not be read as a negative signal.</para>
    /// <para>It describes the <em>binary</em>, not the directory containing it, so
    /// it is used only as publisher-level corroboration and never as proof that the
    /// directory belongs to that publisher's product.</para>
    /// <para><b>The certificate is read on first use, not on inspection.</b> The
    /// signer only matters when a candidate already exists, and reading it costs
    /// roughly one Win32 call per binary, so deferring it keeps the extra work off
    /// the directories that produce no candidate at all. It is resolved at most
    /// once per probe.</para>
    /// </remarks>
    public string? SignerPublisher
    {
        get
        {
            if (!_signerResolved)
            {
                _signerPublisher = _signerReader?.Invoke();
                _signerResolved = true;
            }

            return _signerPublisher;
        }
    }

    public bool IsEmpty => Fields.Count == 0;

    /// <summary>
    /// Builds a probe with known contents, so the signer and metadata rules can be
    /// tested without a filesystem or a signed binary.
    /// </summary>
    internal static ExecutableProbe ForTesting(
        IReadOnlyList<KeyValuePair<string, string>> fields,
        string? signerPublisher,
        string? executablePath = null)
    {
        var probe = new ExecutableProbe { Fields = fields, ExecutablePath = executablePath };
        probe._signerPublisher = signerPublisher;
        probe._signerResolved = true;
        return probe;
    }

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

            var probe = new ExecutableProbe { Fields = fields, ExecutablePath = candidate };
            probe._signerReader = () => AuthenticodeSigner.ReadPublisher(candidate);
            return probe;
        }
        catch (Exception e) when (Scanning.DirectoryWalker.IsRecoverable(e))
        {
            var probe = new ExecutableProbe { ExecutablePath = candidate };
            probe._signerReader = () => AuthenticodeSigner.ReadPublisher(candidate);
            return probe;
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
