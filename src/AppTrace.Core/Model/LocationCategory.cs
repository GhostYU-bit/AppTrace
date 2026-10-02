namespace AppTrace.Core.Model;

/// <summary>
/// Which well-known Windows location a scanned path belongs to. The category is
/// used by reporting and by the path heuristics, never as the sole owner signal.
/// </summary>
public enum LocationCategory
{
    Unknown = 0,

    /// <summary>Application binaries declared as the install location.</summary>
    InstallLocation,

    /// <summary>Program Files / Program Files (x86) descendants.</summary>
    ProgramFiles,

    /// <summary>Machine-wide application data (ProgramData).</summary>
    ProgramData,

    /// <summary>Per-user local application data (LocalAppData).</summary>
    LocalAppData,

    /// <summary>Per-user roaming application data (AppData\Roaming).</summary>
    RoamingAppData,

    /// <summary>Per-user low-integrity application data (AppData\LocalLow).</summary>
    LocalLowAppData,
}

public static class LocationCategoryExtensions
{
    public static string Label(this LocationCategory category) => category switch
    {
        LocationCategory.InstallLocation => "Install location",
        LocationCategory.ProgramFiles => "Program Files",
        LocationCategory.ProgramData => "ProgramData",
        LocationCategory.LocalAppData => "AppData\\Local",
        LocationCategory.RoamingAppData => "AppData\\Roaming",
        LocationCategory.LocalLowAppData => "AppData\\LocalLow",
        _ => "Unknown location",
    };

    /// <summary>
    /// True for locations that hold per-application data rather than the
    /// application's own binaries.
    /// </summary>
    public static bool IsApplicationData(this LocationCategory category)
        => category is LocationCategory.LocalAppData
            or LocationCategory.RoamingAppData
            or LocationCategory.LocalLowAppData
            or LocationCategory.ProgramData;
}

/// <summary>Severity of a non-fatal problem encountered while scanning.</summary>
public enum ScanErrorSeverity
{
    /// <summary>Informational: AppTrace intentionally did not descend further.</summary>
    Info,

    /// <summary>A path could not be read; its size is incomplete.</summary>
    Warning,

    /// <summary>A path could not be read at all; its size is unknown.</summary>
    Error,
}

/// <summary>
/// A path AppTrace could not fully inspect. Reported instead of being silently
/// skipped, so an incomplete scan is never mistaken for a complete one.
/// </summary>
public sealed class ScanError
{
    public required string Path { get; init; }

    public required ScanErrorSeverity Severity { get; init; }

    public required string Message { get; init; }

    /// <summary>Where the failure happened, e.g. "enumerate", "file-size", "reparse-point".</summary>
    public required string Stage { get; init; }
}
