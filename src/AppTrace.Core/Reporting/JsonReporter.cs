namespace AppTrace.Core.Reporting;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppTrace.Core.Model;
using AppTrace.Core.Scanning;

/// <summary>
/// Serializes a scan into the structured diagnostic format
/// (<c>apptrace scan --json</c>).
/// </summary>
/// <remarks>
/// <para>
/// This is the format meant for inspecting and grading attribution quality by
/// hand, and the format a later UI will consume. It therefore contains the whole
/// reasoning chain: discovered applications, every accounted location, every
/// candidate owner that was considered, the individual evidence records, the
/// classification, the sizes, and the scan errors.</para>
/// <para>Enums are written as strings so the output is readable without a schema.</para>
/// </remarks>
public static class JsonReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Render(FootprintReport report)
    {
        var scan = report.Scan;
        var appNames = scan.Applications.ToDictionary(a => a.Id, a => a.DisplayName ?? a.Id, StringComparer.Ordinal);

        var payload = new
        {
            tool = new
            {
                name = "AppTrace",
                version = AppTraceInfo.Version,
                readOnly = true,
            },
            scan = new
            {
                startedAt = scan.StartedAt,
                durationSeconds = Math.Round(scan.Duration.TotalSeconds, 3),
                truncated = scan.Truncated,
                roots = scan.Roots.Select(r => new
                {
                    label = r.Label,
                    path = r.Path,
                    category = r.Category.ToString(),
                }),
                totals = new
                {
                    measuredBytes = scan.TotalMeasuredBytes,
                    fileCount = scan.TotalFileCount,
                    confidentBytes = report.Totals.ConfidentBytes,
                    possibleBytes = report.Totals.PossibleBytes,
                    sharedOrAmbiguousBytes = report.Totals.SharedBytes,
                    unattributedBytes = report.Totals.UnattributedBytes,
                },
            },
            applications = report.Applications.Select(a => new
            {
                id = a.App.Id,
                displayName = a.App.DisplayName,
                normalizedName = a.App.NormalizedName,
                publisher = a.App.Publisher,
                normalizedPublisher = a.App.NormalizedPublisher,
                version = a.App.Version,
                installLocation = a.App.InstallLocation,
                displayIcon = a.App.DisplayIcon,
                uninstallString = a.App.UninstallString,
                registrySource = a.App.RegistrySource,
                productCode = a.App.ProductCode,
                registryRoot = a.App.RegistryRoot.ToString(),
                discoveryKind = a.App.DiscoveryKind.ToString(),
                footprint = new
                {
                    confidentBytes = a.ConfidentBytes,
                    possibleBytes = a.PossibleBytes,
                    sharedBytes = a.SharedBytes,
                    ambiguousBytes = a.AmbiguousBytes,
                    totalBytes = a.TotalAttributedBytes,
                },
            }),
            locations = scan.Items.Select(item => new
            {
                path = item.Path,
                parentPath = item.ParentPath,
                directoryName = item.DirectoryName,
                category = item.Category.ToString(),
                classification = item.Classification.ToString(),
                sizeBytes = item.SizeBytes,
                exclusiveSizeBytes = item.ExclusiveSizeBytes,
                measuredSizeBytes = item.MeasuredSizeBytes,
                fileCount = item.FileCount,
                depth = item.Depth,
                stopReason = item.StopReason,
                unattributedReason = item.UnattributedReason,
                owners = item.AcceptedOwners.Select(o => new
                {
                    appId = o.AppId,
                    displayName = appNames.TryGetValue(o.AppId, out var n) ? n : o.AppId,
                    relation = o.Relation.ToString(),
                    classification = o.Classification.ToString(),
                    score = o.Score,
                    accepted = true,
                }),

                // Applications the content is about, which receive no bytes. Kept in
                // its own collection so an existing consumer that reads `owners` for
                // accounting cannot accidentally count these.
                relatedApplications = item.RelatedApplications.Select(o => new
                {
                    appId = o.AppId,
                    displayName = appNames.TryGetValue(o.AppId, out var n) ? n : o.AppId,
                    relation = o.Relation.ToString(),
                    evidence = o.RelationshipEvidence.Select(e => new
                    {
                        type = e.Type.ToString(),
                        description = e.Description,
                        source = e.Source.ToString(),
                        specificity = e.Specificity,
                    }),
                }),

                candidateOwners = item.CandidateOwners.Select(o => new
                {
                    appId = o.AppId,
                    displayName = appNames.TryGetValue(o.AppId, out var n) ? n : o.AppId,
                    relation = o.Relation.ToString(),
                    classification = o.Classification.ToString(),
                    score = o.Score,
                    accepted = o.Accepted,
                    evidence = o.Evidence.Select(e => new
                    {
                        type = e.Type.ToString(),
                        description = e.Description,
                        strength = e.Strength.ToString(),
                        source = e.Source.ToString(),
                        supportsAttribution = e.SupportsAttribution,
                        weight = e.Weight,
                        specificity = e.Specificity,
                        kind = e.Kind.ToString(),
                    }),
                    relationshipEvidence = o.RelationshipEvidence.Select(e => new
                    {
                        type = e.Type.ToString(),
                        description = e.Description,
                        source = e.Source.ToString(),
                        specificity = e.Specificity,
                        kind = e.Kind.ToString(),
                    }),
                }),
            }),
            unattributed = report.Unattributed.Select(u => new
            {
                path = u.Path,
                sizeBytes = u.SizeBytes,
                category = u.Category.ToString(),
                reason = u.Reason,
            }),
            applicationsWithoutFootprint = report.ApplicationsWithoutFootprint.Select(a => new
            {
                id = a.Id,
                displayName = a.DisplayName,
            }),
            errors = scan.Errors.Select(e => new
            {
                path = e.Path,
                severity = e.Severity.ToString(),
                stage = e.Stage,
                message = e.Message,
            }),
        };

        return JsonSerializer.Serialize(payload, Options);
    }
}
