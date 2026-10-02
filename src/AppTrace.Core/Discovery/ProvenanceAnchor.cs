using AppTrace.Core.Model;

namespace AppTrace.Core.Discovery;

/// <summary>
/// A static Windows registration that independently connects an installed
/// application to a filesystem path.
/// </summary>
/// <remarks>
/// <para>Each source proves something narrow, and the narrow statement is all
/// AppTrace may use:</para>
/// <list type="bullet">
/// <item><see cref="DisplayIcon"/> — this uninstall entry references this
/// executable or resource.</item>
/// <item><see cref="AppPath"/> — Windows registers this executable name at this
/// path.</item>
/// <item><see cref="Service"/> — this service launches this executable.</item>
/// <item><see cref="ScheduledTask"/> — this task launches or references this
/// executable.</item>
/// <item><see cref="RunKey"/> — Windows starts this executable for this user or
/// machine.</item>
/// <item><see cref="Shortcut"/> — this shortcut targets this executable or
/// path.</item>
/// </list>
/// <para><b>What none of them prove:</b> that the application owns every ancestor
/// directory of the referenced file, or that a publisher owns every path containing
/// its name. A registration is evidence that the application <em>reaches</em> a
/// path, which is exactly the claim the confidence ladder is allowed to treat as
/// provenance.</para>
/// </remarks>
public enum ProvenanceSource
{
    Unknown = 0,

    /// <summary>The uninstall entry's <c>DisplayIcon</c> value.</summary>
    DisplayIcon,

    /// <summary>An <c>App Paths</c> registration.</summary>
    AppPath,

    /// <summary>A Windows service's <c>ImagePath</c>.</summary>
    Service,

    /// <summary>A scheduled task action.</summary>
    ScheduledTask,

    /// <summary>A Run / startup registry entry.</summary>
    RunKey,

    /// <summary>A <c>.lnk</c> shortcut target.</summary>
    Shortcut,
}

/// <summary>
/// The role an anchored executable appears to play, so that an uninstaller or an
/// updater is not treated as equivalent to the primary product executable.
/// </summary>
/// <remarks>
/// Deliberately small. Filename, registration source and executable metadata are
/// enough for this; a general classifier would be a much larger claim than the
/// evidence supports.
/// </remarks>
public enum ExecutableRole
{
    Unknown = 0,

    /// <summary>The product's own executable.</summary>
    MainApplication,

    /// <summary>Starts the product without being it.</summary>
    Launcher,

    /// <summary>Applies updates to the product.</summary>
    Updater,

    /// <summary>Removes the product.</summary>
    Uninstaller,

    /// <summary>Runs as a background service.</summary>
    Service,

    /// <summary>A component that assists the product.</summary>
    Helper,
}

/// <summary>
/// One path an installed application is independently known to reach, together with
/// the registration that shows it and the application it was linked to.
/// </summary>
/// <param name="AppId">The installed application this anchor was linked to.</param>
/// <param name="Path">
/// Absolute path of the anchored file or directory. This is what the registration
/// names; it is not a claim about its ancestors.
/// </param>
/// <param name="Directory">
/// Normalized containing directory of <paramref name="Path"/>, precomputed because
/// every query needs it.
/// </param>
/// <param name="Source">Which registration produced the anchor.</param>
/// <param name="Role">What the anchored file appears to be.</param>
/// <param name="Registration">Human-readable origin, for the WHY output.</param>
/// <param name="Corroboration">
/// How the anchor was linked to the application, stated so a reader can judge it.
/// </param>
public sealed record ProvenanceAnchor(
    string AppId,
    string Path,
    string Directory,
    ProvenanceSource Source,
    ExecutableRole Role,
    string Registration,
    string Corroboration)
{
    /// <summary>Normalized form of <see cref="Path"/>, used for comparisons.</summary>
    public string NormalizedPath { get; init; } = TextNormalizer.NormalizePath(Path);

    /// <summary>
    /// True when the anchor was linked without relying on a name resemblance, so it
    /// can stand on its own as provenance.
    /// </summary>
    public bool IsIndependentlyLinked { get; init; }

    public override string ToString()
        => $"{Source} -> {Path} ({Role}, {Corroboration})";
}
