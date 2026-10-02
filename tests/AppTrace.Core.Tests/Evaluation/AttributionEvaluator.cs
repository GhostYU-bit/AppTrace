namespace AppTrace.Core.Tests.Evaluation;

using AppTrace.Core.Attribution;
using AppTrace.Core.Model;

/// <summary>What the engine concluded for one path, independent of any expectation.</summary>
internal sealed class EngineVerdict
{
    /// <summary>Display name of the single accepted owner, or null when there is none or several.</summary>
    public string? Owner { get; init; }

    /// <summary>Every accepted owner; more than one means a structural outcome.</summary>
    public IReadOnlyList<string> AcceptedOwners { get; init; } = [];

    public required Classification Classification { get; init; }

    public IReadOnlyList<EvidenceType> SupportingEvidence { get; init; } = [];

    public IReadOnlyList<EvidenceType> ContradictingEvidence { get; init; } = [];

    public string Summary => AcceptedOwners.Count == 0
        ? $"(none) / {Classification}"
        : $"{string.Join(" + ", AcceptedOwners)} / {Classification}";
}

/// <summary>One case, its expectation, the engine's verdict, and the comparison.</summary>
internal sealed class EvaluationOutcome
{
    public required string Id { get; init; }

    public required string Description { get; init; }

    public required string Path { get; init; }

    public required string Reason { get; init; }

    public required EngineVerdict Verdict { get; init; }

    /// <summary>
    /// The engine's full attribution result for this path.
    /// </summary>
    /// <remarks>
    /// Exposed so a test can assert on the whole candidate set rather than only on
    /// the accepted owner. Task 05's guarantees are mostly about candidates that must
    /// <em>not</em> be generated at all, which an accepted-owner summary cannot
    /// express.
    /// </remarks>
    public LocationAttribution? Raw { get; init; }

    public string? ExpectedOwner { get; init; }

    public required string ExpectedBound { get; init; }

    /// <summary>Null when the case has no recorded Phase 0 baseline.</summary>
    public IReadOnlyList<string>? BaselineDrift { get; init; }

    /// <summary>Null when the case carries no relationship expectation.</summary>
    public string? ExpectedRelation { get; init; }

    /// <summary>
    /// Display name of the application the location's content should be related to.
    /// </summary>
    /// <remarks>
    /// Null when the case expects no relationship. Checked separately from
    /// <see cref="OwnerMet"/> because relatedness must never satisfy, or be confused
    /// with, an ownership expectation.
    /// </remarks>
    public string? ExpectedRelatedApplication { get; init; }

    /// <summary>
    /// The applications the engine reported the content to be about.
    /// </summary>
    public IReadOnlyList<string> RelatedApplications { get; init; } = [];

    /// <summary>
    /// True when the expected relationship was reported. A case with no expectation
    /// is satisfied when no relationship was invented either.
    /// </summary>
    public bool RelationshipMet { get; init; } = true;

    /// <summary>Human-readable summary of what was expected, for failure messages.</summary>
    public string ExpectedRelationSummary
        => ExpectedRelatedApplication is null
            ? "no relationship"
            : $"{ExpectedRelatedApplication} RELATED_TO";

    /// <summary>"high" or "medium": how sure the human label is.</summary>
    public string Confidence { get; init; } = "high";

    /// <summary>False when the label itself needs human review and must not be scored.</summary>
    public bool IsReviewed { get; init; } = true;

    public required bool OwnerMet { get; init; }

    public required bool ClassificationMet { get; init; }

    public required bool RelationMet { get; init; }

    /// <summary>
    /// True when the case expected no owner and the engine correctly accepted none.
    /// </summary>
    /// <remarks>
    /// Tracked separately from <see cref="OwnerMet"/>. Both are "the engine agreed
    /// with the label", but they are different events: one is a successful
    /// attribution and the other is a successful refusal. Collapsing them makes
    /// owner-precision numbers meaningless.
    /// </remarks>
    public bool CorrectlyRefused { get; init; }

    /// <summary>
    /// True when the engine agreed with the label, whether by attributing the
    /// expected owner or by correctly refusing to name one.
    /// </summary>
    /// <remarks>
    /// A correct refusal is a successful outcome, not an unmet expectation. When a
    /// case is labelled "no owner", <see cref="OwnerMet"/> is false by construction,
    /// so requiring it here would report every correct refusal as unresolved — which
    /// hides exactly the progress that removing an unsupported ownership claim
    /// represents. The two events stay separately tracked in
    /// <see cref="OwnerMet"/> and <see cref="CorrectlyRefused"/> so owner precision
    /// is still measured only over attributions.
    /// </remarks>
    public bool Met => (OwnerMet || CorrectlyRefused) && ClassificationMet && RelationMet;

    /// <summary>True when the engine named an owner that the label says does not own the path.</summary>
    public required bool WrongOwnerClaim { get; init; }

    public string ExpectedSummary => ExpectedOwner is null
        ? $"(none) / {ExpectedBound}"
        : $"{ExpectedOwner} / {ExpectedBound}";
}

/// <summary>
/// Runs a case through the real attribution engine and compares the result with an
/// expectation.
/// </summary>
/// <remarks>
/// One code path for both fixtures, so a rule change can never be measured
/// differently in the corpus than in the real-machine evaluation. Executable
/// probing is disabled: a verdict must never depend on what happens to be on the
/// machine running the tests.
/// </remarks>
internal static class AttributionEvaluator
{
    /// <summary>
    /// Turns a case's accepted-ancestor paths into ownership assertions.
    /// </summary>
    /// <remarks>
    /// The corpus names ancestors by path because that is what the scanner knows at
    /// run time. To reconstruct what the scanner would have carried down, each
    /// ancestor is matched to the installed application whose own registration
    /// anchors it. An ancestor no application accounts for contributes no ownership,
    /// which keeps a fixture from asserting ownership merely by listing a path.
    /// </remarks>
    private static List<OwnedAncestor> OwnedAncestors(
        IReadOnlyList<string> ancestors,
        IReadOnlyList<CorpusApp> catalogue)
    {
        var owned = new List<OwnedAncestor>(ancestors.Count);
        foreach (var ancestor in ancestors)
        {
            var normalized = TextNormalizer.NormalizePath(ancestor);
            var owner = catalogue.FirstOrDefault(a =>
                a.InstallLocation is { Length: > 0 } install
                && TextNormalizer.NormalizePath(install).StartsWith(
                    normalized + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal));

            if (owner is not null)
            {
                owned.Add(new OwnedAncestor(normalized, owner.Id, Classification.Confirmed));
            }
        }

        return owned;
    }

    /// <summary>
    /// Runs the engine for one path and returns both the summary verdict and the
    /// raw attribution, so a test can assert on candidates that must not exist.
    /// </summary>
    public static (EngineVerdict Verdict, LocationAttribution Raw) Evaluate(
        string path,
        IReadOnlyList<CorpusApp> catalogue,
        IReadOnlyList<string> appIds,
        string category,
        IReadOnlyList<string> ancestors)
    {
        var apps = appIds
            .Select(id => catalogue.First(a => a.Id == id))
            .Select(a => Fixtures.App(a.DisplayName, a.Publisher, a.InstallLocation, a.Version, a.Id))
            .ToArray();

        var locationCategory = Enum.TryParse<LocationCategory>(category, ignoreCase: true, out var parsed)
            ? parsed
            : LocationCategory.Unknown;

        var delta = Fixtures.Evaluate(
            path,
            apps,
            locationCategory,
            ancestors,
            OwnedAncestors(ancestors, catalogue));

        var accepted = delta.AcceptedOwners
            .Select(o => apps.First(a => a.Id == o.AppId).DisplayName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var verdict = new EngineVerdict
        {
            Owner = accepted.Length == 1 ? accepted[0] : null,
            AcceptedOwners = accepted!,
            Classification = delta.Classification,
            SupportingEvidence = delta.AcceptedOwners
                .SelectMany(o => o.Supporting)
                .Select(e => e.Type)
                .Distinct()
                .OrderBy(t => t.ToString(), StringComparer.Ordinal)
                .ToArray(),
            ContradictingEvidence = delta.AcceptedOwners
                .SelectMany(o => o.Contradicting)
                .Select(e => e.Type)
                .Distinct()
                .OrderBy(t => t.ToString(), StringComparer.Ordinal)
                .ToArray(),
        };

        return (verdict, delta);
    }

    public static EvaluationOutcome Run(CorpusCase testCase, CorpusDocument document)
    {
        var (verdict, raw) = Evaluate(testCase.Path, document.Apps, testCase.Apps, testCase.Category, testCase.AcceptedAncestors);
        var minimum = Parse(testCase.Desired.MinClassification);
        var allowed = ParseAll(testCase.Desired.AllowedClassifications);

        var related = RelatedApplicationNames(raw, document.Apps);

        return new EvaluationOutcome
        {
            Id = testCase.Id,
            Description = testCase.Description,
            Path = testCase.Path,
            Reason = testCase.Reason,
            Verdict = verdict,
            Raw = raw,
            ExpectedOwner = testCase.Desired.Owner,
            ExpectedBound = ClassificationComparison.Describe(minimum, allowed),
            ExpectedRelation = testCase.Desired.Relation,
            ExpectedRelatedApplication = ExpectedRelatedName(testCase, document),
            RelatedApplications = related,
            RelationshipMet = RelationshipSatisfied(testCase, document, related),
            OwnerMet = testCase.Desired.Owner is not null
                && string.Equals(verdict.Owner, testCase.Desired.Owner, StringComparison.Ordinal),
            CorrectlyRefused = testCase.Desired.Owner is null && verdict.AcceptedOwners.Count == 0,
            ClassificationMet = ClassificationComparison.Satisfies(verdict.Classification, minimum, allowed),
            RelationMet = RelationshipSatisfied(testCase, document, related),
            WrongOwnerClaim = testCase.Desired.Owner is null
                ? verdict.AcceptedOwners.Count > 0
                : !string.Equals(verdict.Owner, testCase.Desired.Owner, StringComparison.Ordinal),
            BaselineDrift = CompareToBaseline(testCase, verdict),
        };
    }

    /// <summary>
    /// The application a case expects the content to be related to.
    /// </summary>
    /// <remarks>
    /// A case that declares a <c>relation</c> expects the <em>other</em> application
    /// to be reported: <c>owner</c> owns the location, <c>relatedApplication</c> is
    /// what the content is about. When only <c>relation</c> is given, the related
    /// application is the one named by the case's own expectation note, which the
    /// corpus records explicitly.
    /// </remarks>
    private static string? ExpectedRelatedName(CorpusCase testCase, CorpusDocument document)
    {
        if (testCase.Desired.RelatedApplication is { Length: > 0 } explicitName)
        {
            return document.Apps.FirstOrDefault(a => a.Id == explicitName)?.DisplayName ?? explicitName;
        }

        return null;
    }

    private static bool RelationshipSatisfied(
        CorpusCase testCase,
        CorpusDocument document,
        string[] related)
    {
        var expected = ExpectedRelatedName(testCase, document);
        return expected is null
            ? related.Length == 0
            : related.Contains(expected, StringComparer.Ordinal);
    }

    private static string[] RelatedApplicationNames(
        LocationAttribution? raw,
        IReadOnlyList<CorpusApp> catalogue)
        => raw is null
            ? []
            : raw.RelatedApplications
                .Select(r => catalogue.FirstOrDefault(a => a.Id == r.AppId)?.DisplayName ?? r.AppId)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

    public static EvaluationOutcome Run(EvaluationCase testCase, EvaluationDocument document)
    {
        var (verdict, raw) = Evaluate(testCase.Path, document.Apps, testCase.Apps, testCase.Category, testCase.AcceptedAncestors);
        var minimum = Parse(testCase.MinClassification);
        var allowed = ParseAll(testCase.AllowedClassifications);

        return new EvaluationOutcome
        {
            Id = testCase.Id,
            Description = testCase.Description,
            Path = testCase.Path,
            Reason = testCase.Reasoning,
            Verdict = verdict,
            Raw = raw,
            ExpectedOwner = testCase.Owner,
            ExpectedBound = ClassificationComparison.Describe(minimum, allowed),
            ExpectedRelation = null,
            Confidence = testCase.Confidence,
            IsReviewed = string.Equals(testCase.Label, "reviewed", StringComparison.OrdinalIgnoreCase),
            OwnerMet = testCase.Owner is not null
                && string.Equals(verdict.Owner, testCase.Owner, StringComparison.Ordinal),
            CorrectlyRefused = testCase.Owner is null && verdict.AcceptedOwners.Count == 0,
            ClassificationMet = ClassificationComparison.Satisfies(verdict.Classification, minimum, allowed),
            RelationMet = true,
            WrongOwnerClaim = testCase.Owner is null
                ? verdict.AcceptedOwners.Count > 0
                : !string.Equals(verdict.Owner, testCase.Owner, StringComparison.Ordinal),
            BaselineDrift = null,
        };
    }

    private static Classification? Parse(string? value)
        => value is not null && Enum.TryParse<Classification>(value, ignoreCase: true, out var parsed) ? parsed : null;

    private static Classification[] ParseAll(IReadOnlyList<string> values)
        => values.Select(v => Parse(v) ?? Classification.Unknown).ToArray();

    /// <summary>
    /// Compares the engine against a recorded Phase 0 baseline. A non-empty result
    /// means behaviour changed, which is a deliberate act that must be recorded.
    /// </summary>
    private static List<string>? CompareToBaseline(CorpusCase testCase, EngineVerdict verdict)
    {
        if (testCase.Current is not { } baseline)
        {
            return null;
        }

        var drift = new List<string>();
        if (!string.Equals(verdict.Owner, baseline.Owner, StringComparison.Ordinal))
        {
            drift.Add($"owner: baseline '{baseline.Owner ?? "(none)"}', now '{verdict.Owner ?? "(none)"}'");
        }

        if (Parse(baseline.Classification) is { } expected && expected != verdict.Classification)
        {
            drift.Add($"classification: baseline {expected}, now {verdict.Classification}");
        }

        foreach (var missing in ParseEvidence(baseline.SupportingEvidence).Where(e => !verdict.SupportingEvidence.Contains(e)))
        {
            drift.Add($"supporting evidence '{missing}' is no longer present");
        }

        return drift;
    }

    private static EvidenceType[] ParseEvidence(IReadOnlyList<string> values)
        => values
            .Select(v => Enum.TryParse<EvidenceType>(v, ignoreCase: true, out var parsed) ? parsed : (EvidenceType?)null)
            .Where(e => e is not null)
            .Select(e => e!.Value)
            .ToArray();
}
