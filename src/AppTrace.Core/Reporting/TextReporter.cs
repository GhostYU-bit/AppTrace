namespace AppTrace.Core.Reporting;

using System.Text;
using AppTrace.Core.Model;
using AppTrace.Core.Scanning;

/// <summary>
/// Renders a scan result as human-readable text.
/// </summary>
/// <remarks>
/// Every "WHY" block is generated from the structured
/// <see cref="AppTrace.Core.Model.Evidence"/> records on the item. No explanation
/// text is stored pre-written, which is what lets the CLI, the JSON output and a
/// future UI all tell the same story.
/// </remarks>
public static class TextReporter
{
    /// <summary>Renders the full report.</summary>
    public static string Render(FootprintReport report, ReportOptions? options = null)
    {
        var opts = options ?? new ReportOptions();
        var sb = new StringBuilder();

        RenderHeader(sb, report, opts);
        RenderApplications(sb, report, opts);
        RenderAmbiguous(sb, report, opts);
        RenderUnattributed(sb, report, opts);
        RenderErrors(sb, report, opts);

        return sb.ToString();
    }

    private static void RenderHeader(StringBuilder sb, FootprintReport report, ReportOptions options)
    {
        var scan = report.Scan;
        sb.AppendLine("AppTrace " + AppTraceInfo.Version + " - read-only application storage attribution");
        sb.AppendLine(new string('=', 72));
        sb.AppendLine($"Scanned at      : {scan.StartedAt:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Scan duration   : {scan.Duration.TotalSeconds:0.##} s");
        sb.AppendLine($"Applications    : {scan.Applications.Count} discovered");
        sb.AppendLine($"Locations       : {scan.Items.Count} accounted");
        sb.AppendLine($"Measured total  : {TextNormalizer.FormatBytes(scan.TotalMeasuredBytes)} in {scan.TotalFileCount:N0} files");
        sb.AppendLine();
        sb.AppendLine("Footprint by certainty:");
        sb.AppendLine($"  Confirmed / high : {TextNormalizer.FormatBytes(report.Totals.ConfidentBytes),12}");
        sb.AppendLine($"  Possible         : {TextNormalizer.FormatBytes(report.Totals.PossibleBytes),12}   (MEDIUM / LOW)");
        sb.AppendLine($"  Shared / ambiguous: {TextNormalizer.FormatBytes(report.Totals.SharedBytes),11}   (no single owner)");
        sb.AppendLine($"  Unattributed     : {TextNormalizer.FormatBytes(report.Totals.UnattributedBytes),12}   (no evidence found)");
        sb.AppendLine();

        if (options.RootLabels is { Count: > 0 })
        {
            sb.AppendLine("In scope:");
            foreach (var label in options.RootLabels)
            {
                sb.AppendLine($"  {label}");
            }

            sb.AppendLine();
        }
    }

    private static void RenderApplications(StringBuilder sb, FootprintReport report, ReportOptions options)
    {
        var shown = 0;
        foreach (var app in report.Applications)
        {
            if (app.ConfidentBytes + app.PossibleBytes == 0 && !options.IncludeZeroFootprintApps)
            {
                continue;
            }

            if (shown >= options.MaxApplications)
            {
                sb.AppendLine($"... {report.Applications.Count - shown} more applications with attributed storage. " +
                              "Use --top N to see more.");
                sb.AppendLine();
                break;
            }

            shown++;
            RenderApplication(sb, app, options);
        }

        if (shown == 0)
        {
            sb.AppendLine("No application-attributed locations were found. Run with --all to list every discovered application.");
            sb.AppendLine();
        }
        else if (options.IncludeZeroFootprintApps)
        {
            var idle = report.ApplicationsWithoutFootprint;
            if (idle.Count > 0)
            {
                sb.AppendLine($"{idle.Count} discovered applications have no attributed filesystem location:");
                foreach (var app in idle.Take(options.MaxApplications))
                {
                    sb.AppendLine($"  - {app.DisplayName}{(app.Version is null ? string.Empty : " " + app.Version)}");
                }

                sb.AppendLine();
            }
        }
    }

    private static void RenderApplication(StringBuilder sb, AppFootprint app, ReportOptions options)
    {
        var identity = app.App;
        sb.AppendLine(identity.DisplayName ?? identity.Id);
        if (identity.Version is { Length: > 0 })
        {
            sb.AppendLine($"  version {identity.Version}" + (identity.Publisher is { Length: > 0 } p ? $" | {p}" : string.Empty));
        }
        else if (identity.Publisher is { Length: > 0 })
        {
            sb.AppendLine($"  {identity.Publisher}");
        }

        foreach (var location in app.Locations)
        {
            var item = location.Item;
            sb.AppendLine();
            sb.AppendLine($"  {item.Category.Label()}");
            sb.AppendLine($"    {item.Path}");
            sb.AppendLine($"    {TextNormalizer.FormatBytes(item.SizeBytes),10}  {item.Classification.Symbol()}");

            if (options.Verbose || options.ShowWhy)
            {
                RenderWhy(sb, item, location.Owners, options);
            }
        }

        sb.AppendLine();
        sb.AppendLine($"  Confirmed / high footprint: {TextNormalizer.FormatBytes(app.ConfidentBytes)}");
        if (app.PossibleBytes > 0)
        {
            sb.AppendLine($"  Possible additional       : {TextNormalizer.FormatBytes(app.PossibleBytes)}");
        }

        if (app.SharedBytes > 0)
        {
            sb.AppendLine($"  Shared with other apps    : {TextNormalizer.FormatBytes(app.SharedBytes)}");
        }

        if (app.AmbiguousBytes > 0)
        {
            sb.AppendLine($"  Ambiguous                 : {TextNormalizer.FormatBytes(app.AmbiguousBytes)}");
        }

        sb.AppendLine();
    }

    private static void RenderWhy(
        StringBuilder sb,
        FootprintItem item,
        IReadOnlyList<CandidateOwner> owners,
        ReportOptions options)
    {
        sb.AppendLine("    WHY");

        if (item.AcceptedOwners.Count > 1)
        {
            sb.AppendLine("      This location is claimed by more than one application:");
            foreach (var owner in item.AcceptedOwners)
            {
                sb.AppendLine($"        - {options.NameOf(owner.AppId)} ({owner.Classification.Symbol()})");
            }
        }

        foreach (var owner in owners.Take(options.MaxOwnersToExplain))
        {
            sb.AppendLine($"      For {options.NameOf(owner.AppId)}: score {owner.Score}, {owner.Classification.Symbol()}");
            foreach (var evidence in owner.Evidence.Take(options.MaxEvidencePerOwner))
            {
                var marker = evidence.SupportsAttribution ? "+" : "-";
                sb.AppendLine($"        {marker} [{evidence.Type}] {evidence.Description}");
            }

            var hidden = owner.Evidence.Count - Math.Min(owner.Evidence.Count, options.MaxEvidencePerOwner);
            if (hidden > 0)
            {
                sb.AppendLine($"        ... {hidden} further evidence record(s)");
            }
        }

        var related = item.RelatedApplications;
        if (related.Count > 0)
        {
            // Stated separately from ownership, and never with a size, because a
            // related application does not receive any of these bytes.
            sb.AppendLine("      Related application(s) - the content is about these, they do not own it:");
            foreach (var candidate in related.Take(options.MaxOwnersToExplain))
            {
                sb.AppendLine($"        ~ {options.NameOf(candidate.AppId)} (RELATED_TO, no bytes)");
                foreach (var evidence in candidate.RelationshipEvidence.Take(options.MaxEvidencePerOwner))
                {
                    sb.AppendLine($"            [{evidence.Type}] {evidence.Description}");
                }
            }
        }

        var otherCandidates = item.CandidateOwners
            .Where(c => !c.Accepted && c.Owns && c.Supporting.Any())
            .Take(options.MaxOwnersToExplain)
            .ToArray();
        if (otherCandidates.Length > 0)
        {
            sb.AppendLine("      Considered but not accepted:");
            foreach (var candidate in otherCandidates)
            {
                sb.AppendLine($"        - {options.NameOf(candidate.AppId)} (score {candidate.Score})");
            }
        }

        if (item.StopReason is { Length: > 0 } stopReason)
        {
            sb.AppendLine($"      Stopped descending: {stopReason}");
        }
    }

    private static void RenderAmbiguous(StringBuilder sb, FootprintReport report, ReportOptions options)
    {
        var ambiguous = report.AmbiguousLocations;
        if (ambiguous.Count == 0)
        {
            return;
        }

        sb.AppendLine("Shared / ambiguous locations");
        sb.AppendLine(new string('-', 72));
        sb.AppendLine("AppTrace does not assign these to a single application.");
        sb.AppendLine();

        foreach (var item in ambiguous.Take(options.MaxAmbiguousLocations))
        {
            sb.AppendLine($"{item.Path}");
            sb.AppendLine($"  {TextNormalizer.FormatBytes(item.SizeBytes),10}  {item.Classification.Symbol()}  ({item.Category.Label()})");
            foreach (var owner in item.AcceptedOwners)
            {
                sb.AppendLine($"    possible owner: {options.NameOf(owner.AppId)} ({owner.Classification.Symbol()}, score {owner.Score})");
            }

            if (options.Verbose)
            {
                RenderWhy(sb, item, item.AcceptedOwners, options);
            }

            sb.AppendLine();
        }
    }

    private static void RenderUnattributed(StringBuilder sb, FootprintReport report, ReportOptions options)
    {
        if (report.Unattributed.Count == 0)
        {
            return;
        }

        var total = report.Totals.UnattributedBytes;
        sb.AppendLine("Unattributed storage");
        sb.AppendLine(new string('-', 72));
        sb.AppendLine($"{TextNormalizer.FormatBytes(total)} in {report.Unattributed.Count} location(s) could not be tied to an installed application.");
        sb.AppendLine();

        foreach (var location in report.Unattributed.Take(options.MaxUnattributedLocations))
        {
            sb.AppendLine($"  {TextNormalizer.FormatBytes(location.SizeBytes),10}  {location.Path}");
            if (options.Verbose && location.Reason is { Length: > 0 })
            {
                sb.AppendLine($"              {location.Reason}");
            }
        }

        if (report.Unattributed.Count > options.MaxUnattributedLocations)
        {
            sb.AppendLine($"  ... {report.Unattributed.Count - options.MaxUnattributedLocations} more");
        }

        sb.AppendLine();
    }

    private static void RenderErrors(StringBuilder sb, FootprintReport report, ReportOptions options)
    {
        var errors = report.Scan.Errors;
        if (errors.Count == 0)
        {
            return;
        }

        var byStage = errors
            .GroupBy(e => e.Stage)
            .OrderByDescending(g => g.Count())
            .ToArray();

        sb.AppendLine("Scan notes (the scan could not read everything)");
        sb.AppendLine(new string('-', 72));
        foreach (var group in byStage)
        {
            sb.AppendLine($"  {group.Key}: {group.Count()} occurrence(s)");
        }

        sb.AppendLine();
        var interesting = errors
            .Where(e => e.Severity != ScanErrorSeverity.Info)
            .Take(options.MaxErrors)
            .ToArray();
        foreach (var error in interesting)
        {
            sb.AppendLine($"  [{error.Severity}] {error.Path}");
            sb.AppendLine($"      {error.Message}");
        }

        var informational = errors.Count(e => e.Severity == ScanErrorSeverity.Info);
        if (informational > 0)
        {
            sb.AppendLine($"  ({informational} informational note(s) suppressed; use --json for the full list.)");
        }

        sb.AppendLine();
    }
}

/// <summary>Tunables for <see cref="TextReporter"/>.</summary>
public sealed class ReportOptions
{
    public int MaxApplications { get; init; } = 25;

    public int MaxAmbiguousLocations { get; init; } = 15;

    public int MaxUnattributedLocations { get; init; } = 15;

    public int MaxErrors { get; init; } = 10;

    public int MaxEvidencePerOwner { get; init; } = 8;

    public int MaxOwnersToExplain { get; init; } = 5;

    public bool Verbose { get; init; }

    public bool ShowWhy { get; init; } = true;

    public bool IncludeZeroFootprintApps { get; init; }

    public IReadOnlyList<string> RootLabels { get; init; } = [];

    /// <summary>Resolves an application id to its display name for explanations.</summary>
    public Func<string, string> NameOf { get; init; } = id => id;

    public static ReportOptions Default { get; } = new();

    public static ReportOptions FromScan(ScanResult scan) => new()
    {
        NameOf = id => scan.Applications.FirstOrDefault(a => a.Id == id)?.DisplayName ?? id,
    };
}

/// <summary>Static build information.</summary>
public static class AppTraceInfo
{
    public const string Version = "0.1.0";
}
