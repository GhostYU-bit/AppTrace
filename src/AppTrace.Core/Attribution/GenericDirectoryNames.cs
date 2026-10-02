namespace AppTrace.Core.Attribution;

using AppTrace.Core.Model;

/// <summary>
/// Directory names that carry no product identity. A path segment such as
/// <c>Common</c> or <c>Cache</c> must never be accepted as evidence that one
/// product owns a directory, because every product on the machine may use it.
/// </summary>
/// <remarks>
/// Bulk Crap Uninstaller calls the same idea <c>QuestionableDirectoryNames</c>
/// (score -3 in <c>UninstallTools/UninstallToolsGlobalConfig.cs</c>, Apache-2.0).
/// AppTrace uses a wider list and a harder penalty, following its stated
/// preference for false negatives over false positives.
/// </remarks>
public static class GenericDirectoryNames
{
    private static readonly HashSet<string> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "apps", "application", "applications", "appdata", "bin", "cache",
        "caches", "client", "clients", "common", "common files", "commonfiles",
        "component", "components", "config", "configs", "configuration", "content",
        "crashreports", "crash reports", "data", "database", "db", "default",
        "desktop", "docs", "documents", "download", "downloads", "extensions",
        "files", "framework", "frameworks", "helpers", "install", "installer",
        "lib", "libs", "library", "local", "logs", "media", "microsoft", "modules",
        "new folder", "packages", "plugins", "presets", "profiles", "programs",
        "resources", "roaming", "runtime", "runtimes", "settings", "shared",
        "shared files", "storage", "support", "sys", "system", "temp", "templates",
        "themes", "tmp", "tools", "update", "updates", "updater", "user", "userdata",
        "users", "web", "x64", "x86", "32", "64", "v2", "v3",
    };

    /// <summary>
    /// Names that are structure rather than identity. These appear constantly in
    /// versioned install trees (<c>app-1.2.3</c>, <c>1.0.0</c>) and in vendor
    /// data directories.
    /// </summary>
    public static bool IsGeneric(string directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return true;
        }

        var name = directoryName.Trim();
        if (Exact.Contains(name))
        {
            return true;
        }

        if (name.StartsWith("app-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("v.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Pure version numbers: "1.0.0", "2024.1", "1.2.3.4".
        if (name.Length > 0 && name.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the whole name is just a version string, which makes it useless
    /// as identity evidence but does not by itself mean "shared".
    /// </summary>
    public static bool IsVersionOnly(string directoryName)
    {
        var name = directoryName.Trim();
        return name.Length > 0 && name.All(c => char.IsAsciiDigit(c) || c is '.' or '-' or '_' or 'v' or 'V');
    }

    /// <summary>
    /// True when a directory name may be used as a product-specific signal at all.
    /// </summary>
    /// <remarks>
    /// This guards the strongest evidence AppTrace has. An application may declare
    /// a generic directory as its install location, and its own name may even
    /// resemble that directory, but a name like <c>Google</c> cannot corroborate
    /// the claim that Google <em>Chrome</em> owns it — every product from that
    /// vendor would pass the same test.
    /// </remarks>
    public static bool CanIdentifyAProduct(string directoryName)
        => !string.IsNullOrWhiteSpace(directoryName)
            && !IsGeneric(directoryName)
            && !IsVersionOnly(directoryName);
}
