namespace AppTrace.Core.Model;

/// <summary>
/// A normalized, comparable representation of an installed Windows application.
/// </summary>
/// <remarks>
/// AppTrace treats registry uninstall entries as <em>claims</em>, not as truth:
/// fields are frequently missing, stale, or shared between related products.
/// Every field other than <see cref="Id"/> is therefore optional.
/// </remarks>
public sealed class AppIdentity
{
    /// <summary>Stable identifier for this identity within a single scan.</summary>
    public required string Id { get; init; }

    /// <summary>Raw <c>DisplayName</c> as recorded in the registry.</summary>
    public string? DisplayName { get; init; }

    /// <summary><see cref="DisplayName"/> reduced to a comparable token set.</summary>
    public string NormalizedName { get; init; } = string.Empty;

    public string? Publisher { get; init; }

    /// <summary><see cref="Publisher"/> reduced to a comparable token set.</summary>
    public string NormalizedPublisher { get; init; } = string.Empty;

    public string? Version { get; init; }

    /// <summary>Declared install directory, when the uninstall entry provides one.</summary>
    public string? InstallLocation { get; init; }

    /// <summary>Normalized (fully qualified, lower-cased) form of <see cref="InstallLocation"/>.</summary>
    public string? NormalizedInstallLocation { get; init; }

    public string? DisplayIcon { get; init; }

    public string? UninstallString { get; init; }

    /// <summary>
    /// Human-readable description of where this identity came from, for example
    /// <c>HKLM\Software\...\Uninstall\{GUID}</c>.
    /// </summary>
    public string? RegistrySource { get; init; }

    /// <summary>Product code GUID or other registry key identity, when applicable.</summary>
    public string? ProductCode { get; init; }

    /// <summary>Which registry hive/root produced the entry.</summary>
    public RegistryRootKind RegistryRoot { get; init; } = RegistryRootKind.Unknown;

    /// <summary>How the entry was discovered.</summary>
    public DiscoveryKind DiscoveryKind { get; init; } = DiscoveryKind.UninstallRegistry;

    /// <summary>
    /// Directory name candidates derived from the identity, used as scan hints.
    /// AppTrace uses these only to prioritise inspection; attribution is always
    /// performed by <c>AttributionEngine</c> from evidence.
    /// </summary>
    public IReadOnlyList<string> NameTokens => _nameTokens ??= TextNormalizer.Tokens(NormalizedName);

    private IReadOnlyList<string>? _nameTokens;

    public override string ToString() => DisplayName ?? Id;
}

public enum RegistryRootKind
{
    Unknown = 0,
    LocalMachine64,
    LocalMachine32,
    CurrentUser,
    CurrentUser64,
}

public enum DiscoveryKind
{
    UninstallRegistry = 0,

    /// <summary>MSIX / AppX packaged application (Microsoft Store style).</summary>
    MsixPackage,

    /// <summary>Derived from a filesystem location with no registry entry.</summary>
    FilesystemOnly,
}
