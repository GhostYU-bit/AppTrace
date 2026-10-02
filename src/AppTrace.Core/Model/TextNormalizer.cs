using System.Globalization;
using System.Text;

namespace AppTrace.Core.Model;

/// <summary>
/// Text normalization shared by discovery and attribution.
/// </summary>
/// <remarks>
/// Normalization is deliberately conservative. Aggressive stemming would create
/// false positives (the single worst failure mode for AppTrace), so only
/// version noise, architecture markers, legal suffixes and punctuation are
/// removed.
/// </remarks>
public static class TextNormalizer
{
    /// <summary>
    /// Trailing tokens that carry no identity information: they appear in
    /// registry display names and in the directory names vendors choose for the
    /// same product.
    /// </summary>
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.Ordinal)
    {
        "app", "application", "bin", "bit", "bits", "client", "desktop", "exe",
        "installer", "launcher", "setup", "software", "stable", "update",
        "updater", "win", "windows", "win32", "win64", "x64", "x86", "amd64",
        "arm64",
    };

    /// <summary>Legal-form suffixes stripped from publisher names.</summary>
    private static readonly string[] PublisherSuffixes =
    [
        "corporation", "corp", "incorporated", "inc", "limited", "ltd",
        "gmbh", "s.a", "s.a.s", "sarl", "bv", "b.v", "nv", "n.v", "ab", "as",
        "aps", "oy", "plc", "pty", "llc", "l.l.c", "co", "company", "kg",
        "srl", "s.r.l", "ag", "sa", "spa", "s.p.a", "pte", "kk", "group",
    ];

    /// <summary>
    /// Produces a comparable token used for directory-name and app-name matches.
    /// Non-alphanumeric characters are dropped and the remainder is lower-cased,
    /// so <c>Discord</c>, <c>discord</c> and <c>Discord.exe</c> all normalize to
    /// <c>discord</c>.
    /// </summary>
    public static string NormalizeName(string? value) => Fold(value);

    /// <summary>
    /// Reduces a display name to significant tokens, dropping version-like and
    /// architecture-like noise. <c>Google Chrome (64-bit)</c> becomes
    /// <c>google chrome</c>.
    /// </summary>
    public static string NormalizeDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var tokens = RawTokens(value)
            .Where(t => !NoiseTokens.Contains(t))
            .Where(t => !IsVersionLike(t))
            .ToArray();

        // Never normalize a name down to nothing: fall back to a plain fold.
        return tokens.Length == 0 ? Fold(value) : string.Join(' ', tokens);
    }

    /// <summary>
    /// Reduces a publisher to a comparable token set, dropping legal-form
    /// suffixes. <c>Adobe Inc.</c> becomes <c>adobe</c>.
    /// </summary>
    public static string NormalizePublisher(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var tokens = RawTokens(value);
        while (tokens.Count > 0 && PublisherSuffixes.Contains(tokens[^1], StringComparer.Ordinal))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return tokens.Count == 0 ? Fold(value) : string.Join(' ', tokens);
    }

    /// <summary>Splits a normalized value into its tokens.</summary>
    public static IReadOnlyList<string> Tokens(string normalized)
        => string.IsNullOrWhiteSpace(normalized)
            ? []
            : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// True when a token looks like a version number, or is a single letter
    /// (which is almost always version noise such as the <c>X</c> in
    /// <c>NVIDIA GeForce Experience X</c>).
    /// </summary>
    public static bool IsVersionLike(string token)
    {
        if (token.Length <= 1)
        {
            return token.Length == 1 && char.IsLetter(token[0]);
        }

        var digits = 0;
        var letters = 0;
        var other = 0;
        foreach (var c in token)
        {
            if (char.IsAsciiDigit(c))
            {
                digits++;
            }
            else if (char.IsAsciiLetter(c))
            {
                letters++;
            }
            else
            {
                other++;
            }
        }

        if (other > 0)
        {
            // "v1.2" style fragments survive Fold as "v12"; treat mixed
            // digit/letter tokens with any separator as version-like.
            return digits > 0;
        }

        // Purely numeric tokens are versions ("2024", "11").
        return digits > 0 && letters == 0;
    }

    /// <summary>Lower-cases and drops every non-alphanumeric character.</summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Case-insensitive containment test on normalized values, used for
    /// "directory name is part of the app name" style evidence.
    /// </summary>
    public static bool ContainsNormalized(string haystack, string needle)
        => needle.Length > 0 && haystack.Contains(needle, StringComparison.Ordinal);

    /// <summary>Normalizes a filesystem path for comparison purposes.</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim().Trim('"');
        try
        {
            trimmed = Path.GetFullPath(trimmed);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Leave the raw value; comparisons will simply fail to match.
        }

        return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
    }

    private static List<string> RawTokens(string value)
    {
        var sb = new StringBuilder(value.Length);
        var tokens = new List<string>();
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length > 0)
        {
            tokens.Add(sb.ToString());
        }

        return tokens;
    }

    /// <summary>Formats a byte count for human-readable output.</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} {1}", bytes, units[unit])
            : string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units[unit]);
    }
}
