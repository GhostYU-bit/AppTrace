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

    public string? ExpectedOwner { get; init; }

    public required string ExpectedBound { get; init; }

    /// <summary>Null when the case has no recorded Phase 0 baseline.</summary>
    public IReadOnlyList<string>? BaselineDrift { get; init; }

    /// <summary>Null when the case carries no relationship expectation.</summary>
    public string? ExpectedRelation { get; init; }

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

    public bool Met => OwnerMet && ClassificationMet && RelationMet;

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
    public static EngineVerdict Verdict(string path, IReadOnlyList<CorpusApp> catalogue, IReadOnlyList<string> appIds, string category, IReadOnlyList<string> ancestors)
    {
        var apps = appIds
            .Select(id => catalogue.First(a => a.Id == id))
            .Select(a => Fixtures.App(a.DisplayName, a.Publisher, a.InstallLocation, a.Version, a.Id))
            .ToArray();

        var locationCategory = Enum.TryParse<LocationCategory>(category, ignoreCase: true, out var parsed)
            ? parsed
            : LocationCategory.Unknown;

        var delta = Fixtures.Evaluate(path, apps, locationCategory, ancestors);

        var accepted = delta.AcceptedOwners
            .Select(o => apps.First(a => a.Id == o.AppId).DisplayName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        return new EngineVerdict
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
    }

    public static EvaluationOutcome Run(CorpusCase testCase, CorpusDocument document)
    {
        var verdict = Verdict(testCase.Path, document.Apps, testCase.Apps, testCase.Category, testCase.AcceptedAncestors);
        var minimum = Parse(testCase.Desired.MinClassification);
        var allowed = ParseAll(testCase.Desired.AllowedClassifications);

        return new EvaluationOutcome
        {
            Id = testCase.Id,
            Description = testCase.Description,
            Path = testCase.Path,
            Reason = testCase.Reason,
            Verdict = verdict,
            ExpectedOwner = testCase.Desired.Owner,
            ExpectedBound = ClassificationComparison.Describe(minimum, allowed),
            ExpectedRelation = testCase.Desired.Relation,
            OwnerMet = testCase.Desired.Owner is not null
                && string.Equals(verdict.Owner, testCase.Desired.Owner, StringComparison.Ordinal),
            CorrectlyRefused = testCase.Desired.Owner is null && verdict.AcceptedOwners.Count == 0,
            ClassificationMet = ClassificationComparison.Satisfies(verdict.Classification, minimum, allowed),
            RelationMet = testCase.Desired.Relation is null,
            WrongOwnerClaim = testCase.Desired.Owner is null
                ? verdict.AcceptedOwners.Count > 0
                : !string.Equals(verdict.Owner, testCase.Desired.Owner, StringComparison.Ordinal),
            BaselineDrift = CompareToBaseline(testCase, verdict),
        };
    }

    public static EvaluationOutcome Run(EvaluationCase testCase, EvaluationDocument document)
    {
        var verdict = Verdict(testCase.Path, document.Apps, testCase.Apps, testCase.Category, testCase.AcceptedAncestors);
        var minimum = Parse(testCase.MinClassification);
        var allowed = ParseAll(testCase.AllowedClassifications);

        return new EvaluationOutcome
        {
            Id = testCase.Id,
            Description = testCase.Description,
            Path = testCase.Path,
            Reason = testCase.Reasoning,
            Verdict = verdict,
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
