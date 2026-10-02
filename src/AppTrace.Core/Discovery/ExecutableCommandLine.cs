using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using AppTrace.Core.Model;

namespace AppTrace.Core.Discovery;

/// <summary>
/// Extracts the executable path from the command strings Windows registrations
/// store, and classifies what that executable appears to be.
/// </summary>
/// <remarks>
/// <para>Registration values are untrusted text. The forms that actually occur
/// are:</para>
/// <list type="bullet">
/// <item><c>"C:\Path\App.exe" --flag</c> — quoted, unambiguous.</item>
/// <item><c>C:\Path\App.exe --flag</c> — unquoted; the extension marks where the
/// path ends.</item>
/// <item><c>C:\Path With Spaces\App.exe</c> — unquoted with a space in the
/// directory name; the same rule resolves it.</item>
/// <item><c>C:\Path\App.exe</c> — bare path.</item>
/// <item><c>\SystemRoot\System32\svc.exe</c> and <c>System32\drivers\x.sys</c> —
/// native prefixes that need no resolution because system paths are excluded
/// anyway.</item>
/// </list>
/// <para>An unquoted command line is genuinely ambiguous in general. AppTrace
/// resolves it by cutting at the first executable extension followed by a
/// separator or the end, which is correct for the forms above and conservative for
/// the rest: when nothing matches, the command yields no anchor rather than a
/// guessed path.</para>
/// </remarks>
public static partial class ExecutableCommandLine
{
    private static readonly string[] ExecutableExtensions =
        [".exe", ".com", ".bat", ".cmd", ".sys", ".dll", ".ps1", ".vbs", ".jar"];

    /// <summary>
    /// Extensions that are a legitimate registration target but are not something
    /// Windows launches.
    /// </summary>
    /// <remarks>
    /// A <c>DisplayIcon</c> pointing at an <c>.ico</c> resource is a perfectly valid
    /// registration and still links the uninstall entry to a file, but it is not an
    /// executable anchor. Keeping the two apart stops a real registration from being
    /// counted as a parse failure.
    /// </remarks>
    public static bool IsResourceTarget(string? value)
    {
        var path = StripIndex(value);
        return path is not null
            && (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The bare path of a registration value with any <c>,index</c> suffix removed.
    /// </summary>
    /// <remarks>
    /// Windows writes <c>"C:\Path\App.exe",0</c> and <c>C:\Path\App.dll,-123</c>. The
    /// path is what matters; the index only selects which icon to draw.
    /// </remarks>
    public static string? StripIndex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = Environment.ExpandEnvironmentVariables(value.Trim());
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            if (close > 1)
            {
                return text[1..close];
            }
        }

        // An unquoted value may end with ",0" or ",-101".
        var comma = text.LastIndexOf(',');
        if (comma > 2 && int.TryParse(text[(comma + 1)..].Trim(), out _))
        {
            return text[..comma].Trim().Trim('"');
        }

        return text.Trim().Trim('"');
    }

    /// <summary>Names that identify a role rather than a product.</summary>
    private static readonly string[] UpdaterNames =
        ["update", "updater", "autoupdate", "auto-update", "upgrade", "patch", "squirrel", "arm"];

    private static readonly string[] UninstallerNames =
        ["unins", "uninstall", "uninst", "remove", "setup", "install", "installer", "msiexec"];

    private static readonly string[] LauncherNames =
        ["launcher", "launch", "start", "bootstrap", "loader", "run"];

    private static readonly string[] HelperNames =
        ["helper", "service", "svc", "daemon", "agent", "broker", "host", "crashpad",
         "handler", "monitor", "watcher", "tray", "proxy", "worker", "client"];

    /// <summary>
    /// The absolute executable path a command string names, or null when the string
    /// does not resolve to one.
    /// </summary>
    /// <remarks>
    /// Never touches the filesystem: a registration whose target has since been
    /// deleted is still a statement about what the application reaches, and
    /// demanding the file exist would make anchors depend on the machine's current
    /// state.
    /// </remarks>
    public static string? ResolveExecutable(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var text = Environment.ExpandEnvironmentVariables(commandLine.Trim());

        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            if (close > 1)
            {
                return Clean(text[1..close]);
            }

            // Unterminated quote: fall through and try the unquoted rules.
        }

        return ResolveUnquoted(text);
    }

    private static string? ResolveUnquoted(string text)
    {
        var earliest = -1;
        foreach (var extension in ExecutableExtensions)
        {
            var index = text.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var after = index + extension.Length;
                // The extension must end the path: either the string ends or the
                // next character starts the arguments.
                if (after >= text.Length || text[after] is ' ' or '"' or '\t' or ',')
                {
                    if (earliest < 0 || index < earliest)
                    {
                        earliest = index;
                    }

                    break;
                }

                index = text.IndexOf(extension, index + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (earliest < 0)
        {
            return null;
        }

        var candidate = text[..(earliest + ExtensionLengthAt(text, earliest))];
        return Clean(candidate);
    }

    private static int ExtensionLengthAt(string text, int index)
    {
        foreach (var extension in ExecutableExtensions)
        {
            if (text.AsSpan(index).StartsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return extension.Length;
            }
        }

        return 0;
    }

    /// <summary>
    /// Normalizes prefixes and rejects values that cannot be an application path.
    /// </summary>
    private static string? Clean(string candidate)
    {
        var path = candidate.Trim().Trim('"');
        if (path.Length == 0)
        {
            return null;
        }

        // Native prefixes. These address system files, which are excluded as
        // application anchors anyway, so they are simply not resolved.
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A relative path such as "System32\Drivers\x.sys" is system-relative and
        // never an application anchor.
        if (!Path.IsPathFullyQualified(path))
        {
            return null;
        }

        return path;
    }

    /// <summary>
    /// The role the anchored executable appears to play.
    /// </summary>
    /// <remarks>
    /// <para>This exists so that <c>unins000.exe</c> or <c>helper.exe</c> is not
    /// treated as equivalent to the product's own executable. It is intentionally
    /// crude: filename first, then the registration source, then the metadata the
    /// caller already has.</para>
    /// <para>A role never decides ownership. It is recorded in the explanation and
    /// used only to keep a low-value anchor from corroborating identity it does not
    /// actually corroborate.</para>
    /// </remarks>
    public static ExecutableRole ClassifyRole(
        string? executablePath,
        ProvenanceSource source,
        string? productName = null,
        string? fileDescription = null)
    {
        if (source == ProvenanceSource.Service)
        {
            return ExecutableRole.Service;
        }

        var stem = Stem(executablePath);
        if (stem.Length == 0)
        {
            return ExecutableRole.Unknown;
        }

        // Exact stem matches first, then containment, so "updater" and
        // "360zipUpdate" both land on Updater.
        if (MatchesAny(stem, UpdaterNames))
        {
            return ExecutableRole.Updater;
        }

        if (MatchesAny(stem, UninstallerNames))
        {
            return ExecutableRole.Uninstaller;
        }

        if (MatchesAny(stem, LauncherNames))
        {
            return ExecutableRole.Launcher;
        }

        if (MatchesAny(stem, HelperNames))
        {
            return ExecutableRole.Helper;
        }

        _ = productName;
        _ = fileDescription;
        return ExecutableRole.MainApplication;
    }

    /// <summary>
    /// True when a role can corroborate product identity at all.
    /// </summary>
    /// <remarks>
    /// An uninstaller or an updater is a real anchor — the application does reach
    /// that path — but it says much less about which product owns a directory, so it
    /// must not be what pushes a claim to HIGH on its own.
    /// </remarks>
    public static bool CanCorroborateProductIdentity(ExecutableRole role)
        => role is ExecutableRole.MainApplication or ExecutableRole.Service or ExecutableRole.Unknown;

    private static string Stem(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return string.Empty;
        }

        var name = Path.GetFileNameWithoutExtension(executablePath.Trim());
        return TextNormalizer.Fold(name);
    }

    private static bool MatchesAny(string stem, string[] markers)
    {
        foreach (var marker in markers)
        {
            var folded = TextNormalizer.Fold(marker);
            if (folded.Length == 0)
            {
                continue;
            }

            if (stem.Equals(folded, StringComparison.Ordinal)
                || (folded.Length >= 3 && stem.Contains(folded, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the path is a Windows system executable.</summary>
    public static bool IsSystemExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var normalized = TextNormalizer.NormalizePath(path);
        foreach (var systemRoot in SystemRoots())
        {
            if (normalized.Equals(systemRoot, StringComparison.Ordinal)
                || normalized.StartsWith(systemRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SystemRoots()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows is { Length: > 0 })
        {
            yield return TextNormalizer.NormalizePath(windows);
        }

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (system is { Length: > 0 })
        {
            yield return TextNormalizer.NormalizePath(system);
        }

        // A machine whose variables are unavailable must not silently lose the
        // exclusion, so the conventional location is kept as a last resort.
        yield return @"c:\windows";
    }
}
