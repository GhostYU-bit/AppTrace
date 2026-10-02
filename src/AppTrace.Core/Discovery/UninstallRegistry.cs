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
/// AppTrace reads five registration surfaces: the 64-bit and 32-bit machine-wide
/// uninstall keys, the per-user uninstall key, and the machine-wide and per-user
/// AppX/MSIX package registration keys (which the uninstall keys do not cover).
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

    /// <summary>
    /// The registration surfaces for MSIX/AppX packages.
    /// </summary>
    /// <remarks>
    /// The per-user surface is the one that matters: a package installed for the
    /// current user is registered under <c>HKCU</c>, and the machine-wide key
    /// typically holds only the handful of system packages. Reading both costs one
    /// enumeration each and is deduplicated by package family name afterwards.
    /// </remarks>
    private static readonly (RegistryHive Hive, RegistryView View, string Prefix)[] AppxSurfaces =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"HKLM\" + AppxSubKey),
        (RegistryHive.CurrentUser, RegistryView.Registry64, @"HKCU\" + AppxSubKey),
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
        foreach (var surface in AppxSurfaces)
        {
            try
            {
                found.AddRange(ReadAppxPackages(surface.Hive, surface.View, surface.Prefix));
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                _errors.Add(new ScanError
                {
                    Path = surface.Prefix,
                    Severity = ScanErrorSeverity.Warning,
                    Stage = "registry-appx",
                    Message = Explain(e),
                });
            }
        }

        return Deduplicate(ReconcilePackages(found));
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
    /// keys, but Windows registers each package's own data namespace as
    /// <c>%LOCALAPPDATA%\Packages\&lt;package family name&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>Three structural filters, and nothing name-based:</para>
    /// <list type="bullet">
    /// <item>A package whose <c>Framework</c> value is set is a framework package:
    /// shared infrastructure that other packages depend on, not a product the user
    /// installed. It is not discovered as an application, so its data namespace
    /// stays honestly UNKNOWN rather than being credited either to the framework or
    /// to whichever product depends on it.</item>
    /// <item>A package with no resolvable display name cannot be reported or
    /// attributed meaningfully, exactly as for an uninstall entry. Many packages
    /// store an indirect resource reference (<c>@{...?ms-resource://...}</c>) which
    /// cannot be resolved without the resource loader, so they are skipped rather
    /// than reported as applications with a meaningless name.</item>
    /// <item>A package whose install root is inside the Windows directory is one of
    /// Windows' own components. It is still a package identity — it still governs
    /// its own data namespace — but the install root is not recorded, because
    /// AppTrace does not treat <c>%WINDIR%</c> as an installed application
    /// location.</item>
    /// </list>
    /// <para>The package family name is derived from the package full name, which
    /// is the registration key: <c>Name_Version_Architecture_ResourceId__PublisherId</c>
    /// yields <c>Name_PublisherId</c>. Deriving it rather than reading a value is
    /// necessary because the repository surface stores no package family name of
    /// its own.</para>
    /// </remarks>
    private List<AppIdentity> ReadAppxPackages(RegistryHive hive, RegistryView view, string prefix)
    {
        var results = new List<AppIdentity>();
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
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

                if (ReadInt(entry, "Framework") == 1)
                {
                    continue;
                }

                var displayName = ReadString(entry, "DisplayName");
                if (string.IsNullOrWhiteSpace(displayName)
                    || displayName.StartsWith("@{", StringComparison.Ordinal)
                    || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var packageFamilyName = PackageFamilyNameOf(packageFullName);
                if (packageFamilyName is null)
                {
                    continue;
                }

                var packageRoot = ReadString(entry, "PackageRootFolder");
                var installLocation = packageRoot is { Length: > 0 } && !IsUnderWindowsDirectory(packageRoot)
                    ? packageRoot.Trim()
                    : null;

                results.Add(new AppIdentity
                {
                    Id = MakeId(displayName, null, packageFullName),
                    DisplayName = displayName.Trim(),
                    NormalizedName = TextNormalizer.NormalizeDisplayName(displayName),
                    Version = ReadString(entry, "Version"),
                    InstallLocation = installLocation,
                    NormalizedInstallLocation = installLocation is null
                        ? null
                        : TextNormalizer.NormalizePath(installLocation),
                    RegistrySource = $@"{prefix}\{packageFullName}",
                    ProductCode = packageFullName,
                    RegistryRoot = hive == RegistryHive.CurrentUser
                        ? RegistryRootKind.CurrentUser
                        : RegistryRootKind.LocalMachine64,
                    DiscoveryKind = DiscoveryKind.MsixPackage,
                    PackageFamilyNames = [packageFamilyName],
                });
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                _errors.Add(new ScanError
                {
                    Path = $@"{prefix}\{packageFullName}",
                    Severity = ScanErrorSeverity.Info,
                    Stage = "registry-appx-entry",
                    Message = Explain(e),
                });
            }
        }

        return results;
    }

    /// <summary>
    /// The package family name of a package full name, or <see langword="null"/>
    /// when the name is not shaped like one.
    /// </summary>
    /// <remarks>
    /// <c>Microsoft.WindowsCalculator_11.2210.0.0_x64__8wekyb3d8bbwe</c> becomes
    /// <c>Microsoft.WindowsCalculator_8wekyb3d8bbwe</c>: the name before the first
    /// separator and the publisher id after the last one. The resource id in the
    /// middle is deliberately dropped, because a package family groups a package
    /// with its resource and bundle variants, which is exactly the granularity
    /// <c>%LOCALAPPDATA%\Packages</c> uses.
    /// </remarks>
    internal static string? PackageFamilyNameOf(string packageFullName)
    {
        var first = packageFullName.IndexOf('_');
        var last = packageFullName.LastIndexOf('_');
        if (first <= 0 || last <= first)
        {
            return null;
        }

        var name = packageFullName[..first];
        var publisherId = packageFullName[(last + 1)..];
        return name.Length > 0 && publisherId.Length > 0 ? $"{name}_{publisherId}" : null;
    }

    /// <summary>
    /// Reconciles package registrations with the classic registrations that
    /// describe the same user-facing application.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> One application can be visible through both
    /// registration surfaces at once, and reporting it twice would present a
    /// duplicate "application" that owns half the storage the single application
    /// really owns.</para>
    /// <para><b>Why it is deliberately narrow.</b> Two registrations are treated as
    /// the same application only on a structural agreement about where the product
    /// lives, never on a string resemblance: either the display names agree
    /// <em>and</em> the package root is the classic record's install location or
    /// lies inside it, or the classic record's own registered executable lies inside
    /// the package root. Either way the merge needs exactly <em>one</em> classic
    /// candidate; anything else is kept as two identities, because an unresolved
    /// duplicate is a visible, honest ambiguity whereas a false merge silently
    /// hides one application inside another.</para>
    /// <para><b>Why the classic record survives.</b> It carries the install root
    /// and the uninstall entry a user actually sees. It gains the package family
    /// name, so the package's own data namespace is still attributed to it and
    /// nothing is lost by dropping the package record.</para>
    /// </remarks>
    internal static List<AppIdentity> ReconcilePackages(List<AppIdentity> identities)
    {
        var classic = new List<AppIdentity>();
        var packages = new List<AppIdentity>();
        foreach (var identity in identities)
        {
            (identity.DiscoveryKind == DiscoveryKind.MsixPackage ? packages : classic).Add(identity);
        }

        var packageNamesByHost = new Dictionary<AppIdentity, List<string>>(ReferenceEqualityComparer.Instance);
        var unresolved = new List<AppIdentity>();
        foreach (var package in packages)
        {
            var hosts = classic.Where(c => DescribesTheSameApplication(c, package)).Take(2).ToList();
            if (hosts.Count != 1)
            {
                unresolved.Add(package);
                continue;
            }

            var host = hosts[0];
            if (!packageNamesByHost.TryGetValue(host, out var names))
            {
                names = [];
                packageNamesByHost[host] = names;
            }

            foreach (var name in package.PackageFamilyNames)
            {
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }

        var result = new List<AppIdentity>(identities.Count);
        foreach (var identity in classic)
        {
            result.Add(packageNamesByHost.TryGetValue(identity, out var names)
                ? WithPackageFamilyNames(identity, names)
                : identity);
        }

        result.AddRange(unresolved);
        return result;
    }

    /// <summary>
    /// True when a package registration and a classic registration describe the
    /// same user-facing application.
    /// </summary>
    /// <remarks>
    /// Both qualifying forms are structural, and neither is a name resemblance:
    /// <list type="number">
    /// <item>the package root <em>is</em> the classic record's install location or
    /// lies inside it, and the display names agree;</item>
    /// <item>the classic record's own registered executable — its
    /// <c>DisplayIcon</c> or <c>UninstallString</c> — lies inside the package root.
    /// Here the registration itself names a file in the package's payload, so the
    /// display names need not agree: a product Windows registers both ways may be
    /// titled "Microsoft OneDrive" in the uninstall entry and "OneDrive" in its
    /// package manifest, while both point at the same versioned install root.</item>
    /// </list>
    /// </remarks>
    private static bool DescribesTheSameApplication(AppIdentity classic, AppIdentity package)
    {
        if (package.NormalizedInstallLocation is not { Length: > 0 } root)
        {
            return false;
        }

        if (string.Equals(
                TextNormalizer.Fold(classic.NormalizedName),
                TextNormalizer.Fold(package.NormalizedName),
                StringComparison.Ordinal)
            && classic.NormalizedInstallLocation is { Length: > 0 } install
            && (root.Equals(install, StringComparison.Ordinal)
                || root.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            return true;
        }

        return DeclaresExecutableInside(classic, root);
    }

    /// <summary>
    /// True when the classic record's own registration resolves to a file inside
    /// <paramref name="packageRoot"/>.
    /// </summary>
    private static bool DeclaresExecutableInside(AppIdentity classic, string packageRoot)
    {
        foreach (var registration in new[] { classic.DisplayIcon, classic.UninstallString })
        {
            if (ExecutableCommandLine.ResolveExecutable(registration) is not { Length: > 0 } target)
            {
                continue;
            }

            if (TextNormalizer.NormalizePath(target) is { Length: > 0 } normalized
                && normalized.StartsWith(packageRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A copy of <paramref name="app"/> carrying the package family names Windows
    /// registered for it.
    /// </summary>
    /// <remarks>
    /// <see cref="AppIdentity"/> exposes init-only properties, so an enrichment has
    /// to be an explicit copy. Every property must be listed here; a field added to
    /// the model and forgotten here would silently be dropped on reconciliation.
    /// </remarks>
    private static AppIdentity WithPackageFamilyNames(AppIdentity app, IReadOnlyList<string> packageFamilyNames) => new()
    {
        Id = app.Id,
        DisplayName = app.DisplayName,
        NormalizedName = app.NormalizedName,
        Publisher = app.Publisher,
        NormalizedPublisher = app.NormalizedPublisher,
        Version = app.Version,
        InstallLocation = app.InstallLocation,
        NormalizedInstallLocation = app.NormalizedInstallLocation,
        DisplayIcon = app.DisplayIcon,
        UninstallString = app.UninstallString,
        RegistrySource = app.RegistrySource,
        ProductCode = app.ProductCode,
        RegistryRoot = app.RegistryRoot,
        DiscoveryKind = app.DiscoveryKind,
        PackageFamilyNames = packageFamilyNames,
    };

    /// <summary>
    /// Collapses duplicate registrations of the same product across hives or
    /// registry views. The first surface wins because the surfaces are ordered
    /// machine-64, machine-32, then per-user.
    /// </summary>
    /// <remarks>
    /// Classic registrations collapse by display name and publisher, which is how
    /// the same product routinely appears in more than one place. Package
    /// registrations collapse by package family name instead, because that is the
    /// identity Windows itself uses: one package family is one package, however
    /// many registration surfaces happen to show it, and folding them by display
    /// name would lose the distinction between two packages that share a name.
    /// </remarks>
    private static List<AppIdentity> Deduplicate(List<AppIdentity> identities)
    {
        var byKey = new Dictionary<string, AppIdentity>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            var key = identity.DiscoveryKind == DiscoveryKind.MsixPackage
                ? "package|" + TextNormalizer.Fold(
                    identity.PackageFamilyNames.Count > 0 ? identity.PackageFamilyNames[0] : null)
                : $"{TextNormalizer.Fold(identity.NormalizedName)}|{TextNormalizer.Fold(identity.NormalizedPublisher)}";

            if (key is "|" or "package|")
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

    private static int? ReadInt(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
            {
                int i => i,
                long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
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
