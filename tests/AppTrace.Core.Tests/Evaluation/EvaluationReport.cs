namespace AppTrace.Core.Tests.Evaluation;

using System.Text;
using AppTrace.Core.Model;

/// <summary>
/// The headline numbers for one evaluation run.
/// </summary>
/// <remarks>
/// Deliberately not collapsed into a single accuracy figure. A wrong HIGH claim is
/// a different kind of event from an honest UNKNOWN, and the number that matters
/// most is how many confident ownership claims are wrong.
/// </remarks>
internal sealed class EvaluationMetrics
{
    public int TotalCases { get; init; }

    public int Met { get; init; }

    public int OwnerCorrect { get; init; }

    public int WrongOwner { get; init; }

    public int CorrectUnknown { get; init; }

    /// <summary>Labelled as having no owner, but the engine still named one.</summary>
    public int FalsePositives { get; init; }

    /// <summary>Labelled as having an owner, but the engine left it unattributed.</summary>
    public int FalseNegatives { get; init; }

    public int CorrectOwnerWrongConfidence { get; init; }

    /// <summary>Cases that carry a relationship expectation the model cannot express yet.</summary>
    public int RelationExpectations { get; init; }

    public int BaselineDrift { get; init; }

    /// <summary>Cases whose human label was marked high confidence.</summary>
    public int HighConfidenceCases { get; init; }

    /// <summary>THE metric: confident ownership claims that are wrong.</summary>
    public int WrongOwnerAtHighOrConfirmed { get; init; }

    public int HighOrConfirmedClaims { get; init; }

    public IReadOnlyDictionary<Classification, (int Correct, int Total)> PrecisionByClassification { get; init; }
        = new Dictionary<Classification, (int, int)>();

    public static EvaluationMetrics From(IReadOnlyList<EvaluationOutcome> outcomes)
    {
        var byClass = new Dictionary<Classification, (int Correct, int Total)>();
        var wrongConfident = 0;
        var confidentClaims = 0;

        foreach (var outcome in outcomes)
        {
            if (outcome.ExpectedOwner is not null)
            {
                var bucket = byClass.TryGetValue(outcome.Verdict.Classification, out var existing)
                    ? existing
                    : (Correct: 0, Total: 0);
                byClass[outcome.Verdict.Classification] = (bucket.Correct + (outcome.OwnerMet ? 1 : 0), bucket.Total + 1);
            }

            if (outcome.Verdict.Classification is Classification.High or Classification.Confirmed
                && outcome.Verdict.AcceptedOwners.Count > 0)
            {
                confidentClaims++;
                if (!outcome.OwnerMet)
                {
                    wrongConfident++;
                }
            }
        }

        return new EvaluationMetrics
        {
            TotalCases = outcomes.Count,
            Met = outcomes.Count(o => o.Met),
            OwnerCorrect = outcomes.Count(o => o.OwnerMet),
            WrongOwner = outcomes.Count(o => !o.OwnerMet && o.ExpectedOwner is not null),
            CorrectUnknown = outcomes.Count(o => o.CorrectlyRefused),
            FalsePositives = outcomes.Count(o => o.ExpectedOwner is null && o.Verdict.AcceptedOwners.Count > 0),
            FalseNegatives = outcomes.Count(o => o.ExpectedOwner is not null && o.Verdict.AcceptedOwners.Count == 0),
            CorrectOwnerWrongConfidence = outcomes.Count(o => o.OwnerMet && !o.ClassificationMet),
            RelationExpectations = outcomes.Count(o => o.ExpectedRelation is not null),
            BaselineDrift = outcomes.Count(o => o.BaselineDrift is { Count: > 0 }),
            HighConfidenceCases = outcomes.Count(o => o.Confidence == "high"),
            WrongOwnerAtHighOrConfirmed = wrongConfident,
            HighOrConfirmedClaims = confidentClaims,
            PrecisionByClassification = byClass,
        };
    }
}

/// <summary>
/// Renders evaluation output as text for the test log and for a report file.
/// </summary>
internal static class EvaluationReport
{
    public static string Render(string title, string source, IReadOnlyList<EvaluationOutcome> outcomes)
    {
        var metrics = EvaluationMetrics.From(outcomes);
        var sb = new StringBuilder();

        // The wrong-owner count is printed first and set apart on purpose: it is
        // the number that decides whether an attribution change was an improvement.
        sb.AppendLine("========================================================================");
        sb.AppendLine(title);
        sb.AppendLine("========================================================================");
        sb.AppendLine($"source: {source}");
        sb.AppendLine($"total cases: {metrics.TotalCases}");
        sb.AppendLine();
        sb.AppendLine("*** WRONG-OWNER HIGH/CONFIRMED CLAIMS: " +
                      $"{metrics.WrongOwnerAtHighOrConfirmed} of {metrics.HighOrConfirmedClaims} confident claims ***");
        sb.AppendLine();
        sb.AppendLine("Owner correctness");
        sb.AppendLine($"  expected outcome fully met        : {metrics.Met}");
        sb.AppendLine($"  correct owner                     : {metrics.OwnerCorrect}");
        sb.AppendLine($"  wrong owner                       : {metrics.WrongOwner}");
        sb.AppendLine($"  correct UNKNOWN (nothing accepted): {metrics.CorrectUnknown}");
        sb.AppendLine($"  false positive (owner where none) : {metrics.FalsePositives}");
        sb.AppendLine($"  false negative (none where owner) : {metrics.FalseNegatives}");
        sb.AppendLine($"  correct owner, confidence differs : {metrics.CorrectOwnerWrongConfidence}");
        sb.AppendLine($"  human labels marked high confidence: {metrics.HighConfidenceCases} of {metrics.TotalCases}");
        sb.AppendLine();

        if (metrics.PrecisionByClassification.Count > 0)
        {
            sb.AppendLine("Owner precision by classification the engine actually produced");
            foreach (var entry in metrics.PrecisionByClassification.OrderByDescending(e => Order(e.Key)))
            {
                var (correct, total) = entry.Value;
                sb.AppendLine($"  {entry.Key,-10}: {correct}/{total} correct");
            }

            sb.AppendLine();
        }

        if (metrics.RelationExpectations > 0)
        {
            sb.AppendLine($"relation expectations recorded but not evaluable yet: {metrics.RelationExpectations}");
            sb.AppendLine();
        }

        if (metrics.BaselineDrift > 0)
        {
            sb.AppendLine($"*** BASELINE DRIFT on {metrics.BaselineDrift} case(s) ***");
            sb.AppendLine();
        }

        sb.AppendLine("Per-case detail");
        sb.AppendLine("------------------------------------------------------------------------");
        foreach (var outcome in outcomes)
        {
            var mark = outcome.Met ? "ok   " : (outcome.WrongOwnerClaim ? "WRONG" : "open ");
            sb.AppendLine($"[{mark}] {outcome.Id}  (label confidence: {outcome.Confidence})");
            sb.AppendLine($"        {outcome.Description}");
            sb.AppendLine($"        path    : {outcome.Path}");
            sb.AppendLine($"        current : {outcome.Verdict.Summary}");
            sb.AppendLine($"        expected: {outcome.ExpectedSummary}");
            if (outcome.Verdict.SupportingEvidence.Count > 0)
            {
                sb.AppendLine($"        evidence: {string.Join(", ", outcome.Verdict.SupportingEvidence)}");
            }

            if (outcome.ExpectedRelation is { } relation)
            {
                sb.AppendLine($"        relation: expects {relation} (not evaluable until the relationship model exists)");
            }

            sb.AppendLine($"        reason  : {outcome.Reason}");
            foreach (var drift in outcome.BaselineDrift ?? [])
            {
                sb.AppendLine($"        BASELINE DRIFT: {drift}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static int Order(Classification classification) => classification switch
    {
        Classification.Confirmed => 6,
        Classification.High => 5,
        Classification.Shared => 4,
        Classification.Ambiguous => 3,
        Classification.Medium => 2,
        Classification.Low => 1,
        _ => 0,
    };
}
