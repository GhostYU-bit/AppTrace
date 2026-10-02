using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AppTrace.Core.Model;
using Microsoft.Win32;

namespace AppTrace.Core.Discovery;

/// <summary>
/// Reads installed-application registration data out of the Windows registry.
/// </summary>
/// <remarks>
/// <para>
/// AppTrace reads four registration surfaces: the 64-bit and 32-bit machine-wide
/// uninstall keys, the per-user uninstall key, and the AppX/MSIX package
/// registration key (which the uninstall keys do not cover).
/// </para>
/// <para>
/// Registry values are treated as untrusted claims. Entries without a display
/// name are skipped because they cannot be attributed or reported meaningfully,
/// and every optional field is read defensively: a malformed or missing value
/// degrades the identity instead of failing discovery.
/// </para>
/// <para>
/// This type is strictly read-only: it opens keys with
/// <see cref="RegistryKeyPermissionCheck.ReadSubTree"/> and never writes.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UninstallRegistry
{
    private const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppxSubKey = @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private static readonly (RegistryHive Hive, RegistryView View, RegistryRootKind Kind, string Prefix)[] Surfaces =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, RegistryRootKind.LocalMachine64, @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, RegistryRootKind.LocalMachine32, @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, RegistryRootKind.CurrentUser, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall"),
    ];

    private readonly List<ScanError> _errors = [];

    /// <summary>Non-fatal problems encountered while reading the registry.</summary>
    public IReadOnlyList<ScanError> Errors => _errors;

    /// <summary>Enumerates every installed application AppTrace can identify.</summary>
    public IReadOnlyList<AppIdentity> Discover()
    {
        var found = new List<AppIdentity>();
        foreach (var surface in Surfaces)
        {
            found.AddRange(ReadSurface(surface.Hive, surface.View, surface.Kind, surface.Prefix));
        }

        // AppX discovery is optional: it uses a different schema, and a failure
        // there must never prevent the uninstall-key results from being reported.
        try
        {
            found.AddRange(ReadAppxPackages());
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            _errors.Add(new ScanError
            {
                Path = @"HKLM\" + AppxSubKey,
                Severity = ScanErrorSeverity.Warning,
                Stage = "registry-appx",
                Message = Explain(e),
            });
        }

        return Deduplicate(found);
    }

    private List<AppIdentity> ReadSurface(RegistryHive hive, RegistryView view, RegistryRootKind kind, string prefix)
    {
        var results = new List<AppIdentity>();

        RegistryKey? baseKey = null;
        RegistryKey? uninstall = null;
        try
        {
            baseKey = RegistryKey.OpenBaseKey(hive, view);
            uninstall = baseKey.OpenSubKey(UninstallSubKey, RegistryKeyPermissionCheck.ReadSubTree);
            if (uninstall is null)
            {
                return results;
            }

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var entry = uninstall.OpenSubKey(subKeyName, RegistryKeyPermissionCheck.ReadSubTree);
                    if (entry is null)
                    {
                        continue;
                    }

                    var identity = BuildIdentity(entry, subKeyName, kind, $@"{prefix}\{subKeyName}");
                    if (identity is not null)
                    {
                        results.Add(identity);
                    }
                }
                catch (Exception e) when (IsRecoverable(e))
                {
                    _errors.Add(new ScanError
                    {
                        Path = $@"{prefix}\{subKeyName}",
                        Severity = ScanErrorSeverity.Warning,
                        Stage = "registry-entry",
                        Message = Explain(e),
                    });
                }
            }
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            // A missing, access-denied or redirected hive is a normal condition
            // (32-bit entries do not exist on 32-bit Windows, for example).
            _errors.Add(new ScanError
            {
                Path = prefix,
                Severity = ScanErrorSeverity.Warning,
                Stage = "registry-open",
                Message = Explain(e),
            });
        }
        finally
        {
            uninstall?.Dispose();
            baseKey?.Dispose();
        }

        return results;
    }

    private static AppIdentity? BuildIdentity(RegistryKey entry, string subKeyName, RegistryRootKind kind, string source)
    {
        var displayName = ReadString(entry, "DisplayName");
        if (string.IsNullOrWhiteSpace(displayName))
        {
            // Nothing to report and nothing to attribute; skipping is safer than
            // inventing a name from the key.
            return null;
        }

        var publisher = ReadString(entry, "Publisher");
        var installLocation = ReadString(entry, "InstallLocation");
        var displayIcon = ReadString(entry, "DisplayIcon");

        return new AppIdentity
        {
            Id = MakeId(displayName, publisher, subKeyName),
            DisplayName = displayName.Trim(),
            NormalizedName = TextNormalizer.NormalizeDisplayName(displayName),
            Publisher = publisher?.Trim(),
            NormalizedPublisher = TextNormalizer.NormalizePublisher(publisher),
            Version = ReadString(entry, "DisplayVersion"),
            InstallLocation = string.IsNullOrWhiteSpace(installLocation) ? null : installLocation.Trim(),
            NormalizedInstallLocation = string.IsNullOrWhiteSpace(installLocation)
                ? null
                : TextNormalizer.NormalizePath(installLocation),
            DisplayIcon = displayIcon,
            UninstallString = ReadString(entry, "UninstallString"),
            RegistrySource = source,
            ProductCode = subKeyName,
            RegistryRoot = kind,
            DiscoveryKind = DiscoveryKind.UninstallRegistry,
        };
    }

    /// <summary>
    /// Reads MSIX/AppX package registrations. These never appear in the uninstall
    /// keys, but they do own real per-user data under
    /// <c>%LOCALAPPDATA%\Packages</c>.
    /// </summary>
    private List<AppIdentity> ReadAppxPackages()
    {
        var results = new List<AppIdentity>();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var packages = baseKey.OpenSubKey(AppxSubKey, RegistryKeyPermissionCheck.ReadSubTree);
        if (packages is null)
        {
            return results;
        }

        foreach (var packageFullName in packages.GetSubKeyNames())
        {
            try
            {
                using var entry = packages.OpenSubKey(packageFullName, RegistryKeyPermissionCheck.ReadSubTree);
                if (entry is null)
                {
                    continue;
                }

                var displayName = ReadString(entry, "DisplayName") ?? ReadString(entry, "PackageName");
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                // Many system packages store an indirect resource reference such
                // as "@{windows.immersivecontrolpanel_...?ms-resource://...}"
                // instead of a real name. Those cannot be resolved without the
                // resource loader, so they are skipped rather than reported as
                // applications with a meaningless name.
                if (displayName.StartsWith("@{", StringComparison.Ordinal)
                    || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var publisher = ReadString(entry, "PublisherDisplayName") ?? ReadString(entry, "Publisher");
                var installLocation = ReadString(entry, "PackageRootFolder");

                // Windows' own AppX components live under %WINDIR% and are not
                // independently installed applications; including them would bury
                // the real results in noise.
                if (installLocation is { Length: > 0 } location && IsUnderWindowsDirectory(location))
                {
                    continue;
                }

                results.Add(new AppIdentity
                {
                    Id = MakeId(displayName, publisher, packageFullName),
                    DisplayName = displayName.Trim(),
                    NormalizedName = TextNormalizer.NormalizeDisplayName(displayName),
                    Publisher = publisher?.Trim(),
                    NormalizedPublisher = TextNormalizer.NormalizePublisher(publisher),
                    Version = ReadString(entry, "Version"),
                    InstallLocation = string.IsNullOrWhiteSpace(installLocation) ? null : installLocation.Trim(),
                    NormalizedInstallLocation = string.IsNullOrWhiteSpace(installLocation)
                        ? null
                        : TextNormalizer.NormalizePath(installLocation),
                    RegistrySource = $@"HKLM\{AppxSubKey}\{packageFullName}",
                    ProductCode = packageFullName,
                    RegistryRoot = RegistryRootKind.LocalMachine64,
                    DiscoveryKind = DiscoveryKind.MsixPackage,
                });
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                _errors.Add(new ScanError
                {
                    Path = $@"HKLM\{AppxSubKey}\{packageFullName}",
                    Severity = ScanErrorSeverity.Info,
                    Stage = "registry-appx-entry",
                    Message = Explain(e),
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Collapses duplicate registrations of the same product across hives or
    /// registry views. The first surface wins because the surfaces are ordered
    /// machine-64, machine-32, then per-user.
    /// </summary>
    private static List<AppIdentity> Deduplicate(List<AppIdentity> identities)
    {
        var byKey = new Dictionary<string, AppIdentity>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            var key = $"{TextNormalizer.Fold(identity.NormalizedName)}|{TextNormalizer.Fold(identity.NormalizedPublisher)}";
            if (key == "|")
            {
                continue;
            }

            if (!byKey.TryAdd(key, identity))
            {
                var existing = byKey[key];
                // Keep the richer of the two entries; a per-user entry often
                // carries the InstallLocation while the machine entry does not.
                if (existing.InstallLocation is null && identity.InstallLocation is not null)
                {
                    byKey[key] = identity;
                }
            }
        }

        return byKey.Values
            .OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string MakeId(string displayName, string? publisher, string productCode)
    {
        var seed = $"{TextNormalizer.Fold(displayName)}|{TextNormalizer.Fold(publisher)}|{productCode.ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        // A short, stable, human-diffable identifier. Not a security token.
        return "app-" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string? ReadString(RegistryKey key, string name)    {
        try
        {
            return key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
            {
                string s => s,
                string[] a => a.FirstOrDefault(),
                _ => null,
            };
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            return null;
        }
    }

    private static bool IsUnderWindowsDirectory(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
        {
            return false;
        }

        var normalized = TextNormalizer.NormalizePath(path);
        var normalizedWindows = TextNormalizer.NormalizePath(windows);
        return normalized.Equals(normalizedWindows, StringComparison.Ordinal)
            || normalized.StartsWith(normalizedWindows + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsRecoverable(Exception e)
        => e is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException
            or ObjectDisposedException
            or InvalidOperationException
            or ArgumentException
            or PlatformNotSupportedException;

    private static string Explain(Exception e) => $"{e.GetType().Name}: {e.Message}";
}
