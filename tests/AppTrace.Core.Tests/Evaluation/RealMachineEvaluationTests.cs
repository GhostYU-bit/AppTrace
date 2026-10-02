namespace AppTrace.Core.Tests.Evaluation;

/// <summary>
/// Evaluates the engine against hand-labelled ground truth from a real Windows
/// machine.
/// </summary>
/// <remarks>
/// <para><b>The ground truth is human-reviewed, not engine-derived.</b> Each label
/// in <c>real-machine-labels.json</c> records what a person concluded from the
/// machine's own evidence (uninstall registry, directory contents, executable
/// metadata, the installer path) and states why. The engine's output was used to
/// choose <em>which</em> locations are interesting, never to decide the answer.</para>
/// <para><b>Cases that could not be justified are excluded from the metrics</b> and
/// reported as needing review. A guessed label would corrupt the one number this
/// harness exists to protect, so those cases are listed explicitly rather than
/// counted.</para>
/// <para>The suite stays green: this is a measurement, not an assertion about what
/// the engine should already do. What it asserts is that the measurement itself
/// stays honest — every case labelled, none silently dropped.</para>
/// </remarks>
public class RealMachineEvaluationTests
{
    private readonly ITestOutputHelper _output;

    public RealMachineEvaluationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EvaluationSetIsWellFormed()
    {
        // LoadEvaluation() validates structure, undefined app references, unknown
        // enum values, missing reasoning and mismatched owner names.
        var document = AttributionCorpus.LoadEvaluation();

        Assert.True(document.Cases.Count >= 30, $"Only {document.Cases.Count} evaluation cases; the target is 40-60.");
        Assert.All(document.Cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Reasoning)));
    }

    [Fact]
    public void EveryCaseStatesWhyItsLabelIsCorrect()
    {
        var document = AttributionCorpus.LoadEvaluation();

        // A label without a stated justification is not ground truth, it is an
        // opinion. This is the guard that keeps the set reviewable.
        var unexplained = document.Cases
            .Where(c => c.Reasoning.Length < 40 || !c.Reasoning.Contains(' ', StringComparison.Ordinal))
            .Select(c => c.Id)
            .ToArray();

        Assert.Empty(unexplained);
    }

    [Fact]
    public void RecordsOwnerPrecisionAgainstHandLabelledTruth()
    {
        var document = AttributionCorpus.LoadEvaluation();

        var reviewed = document.Cases
            .Where(c => string.Equals(c.Label, "reviewed", StringComparison.OrdinalIgnoreCase))
            .Select(c => AttributionEvaluator.Run(c, document))
            .ToArray();

        var uncertain = document.Cases
            .Where(c => !string.Equals(c.Label, "reviewed", StringComparison.OrdinalIgnoreCase))
            .Select(c => AttributionEvaluator.Run(c, document))
            .ToArray();

        var report = EvaluationReport.Render(
            "Real-machine evaluation - engine vs hand-labelled ground truth",
            AttributionCorpus.EvaluationPath,
            reviewed);

        var uncertainReport = EvaluationReport.Render(
            "Real-machine evaluation - cases needing human review (excluded from metrics)",
            AttributionCorpus.EvaluationPath,
            uncertain);

        var path = AttributionCorpus.WriteArtefact("real-machine-report.txt", report + Environment.NewLine + uncertainReport);

        foreach (var line in AttributionCorpusTests.Headline(report))
        {
            _output.WriteLine(line);
        }

        _output.WriteLine($"cases excluded pending human review: {uncertain.Length}");
        _output.WriteLine($"full report: {path}");

        var metrics = EvaluationMetrics.From(reviewed);

        // The measurement must be complete and honest: every reviewed case is
        // accounted for by exactly one outcome. The two halves of the set are
        // checked separately so a false positive cannot be quietly counted as a
        // wrong owner, and a successful refusal cannot be counted as a successful
        // attribution.
        var labelledWithOwner = reviewed.Count(o => o.ExpectedOwner is not null);
        var labelledWithoutOwner = reviewed.Count(o => o.ExpectedOwner is null);

        var withOwner = metrics.OwnerCorrect + metrics.FalseNegatives + metrics.WrongOwner;
        var withoutOwner = metrics.CorrectUnknown + metrics.FalsePositives;

        Assert.True(
            labelledWithOwner == withOwner,
            $"owner-labelled cases: {labelledWithOwner} but outcomes sum to {withOwner} " +
            $"(correct {metrics.OwnerCorrect} + false-negative {metrics.FalseNegatives} + wrong {metrics.WrongOwner})");

        Assert.True(
            labelledWithoutOwner == withoutOwner,
            $"no-owner-labelled cases: {labelledWithoutOwner} but outcomes sum to {withoutOwner} " +
            $"(unknown {metrics.CorrectUnknown} + false-positive {metrics.FalsePositives})");

        Assert.True(
            document.Cases.Count == reviewed.Length + uncertain.Length,
            $"case accounting: {document.Cases.Count} total, {reviewed.Length} reviewed, {uncertain.Length} uncertain");
        Assert.NotEmpty(reviewed);
    }

    [Fact]
    public void ReportsTheConfidentlyWrongCountProminently()
    {
        var document = AttributionCorpus.LoadEvaluation();
        var reviewed = document.Cases
            .Where(c => string.Equals(c.Label, "reviewed", StringComparison.OrdinalIgnoreCase))
            .Select(c => AttributionEvaluator.Run(c, document))
            .ToArray();

        var metrics = EvaluationMetrics.From(reviewed);

        // This is the number the acceptance criterion asks for: did a change make
        // the engine more confidently wrong, less, or merely different?
        _output.WriteLine($"WRONG-OWNER HIGH/CONFIRMED CLAIMS: {metrics.WrongOwnerAtHighOrConfirmed} of {metrics.HighOrConfirmedClaims} confident claims");

        Assert.True(metrics.HighOrConfirmedClaims > 0, "The evaluation set no longer exercises any confident claim.");
        Assert.Equal(reviewed.Length, metrics.TotalCases);
    }

    [Fact]
    public void EveryUncertainCaseNamesItsOpenQuestion()
    {
        var document = AttributionCorpus.LoadEvaluation();
        var uncertain = document.Cases
            .Where(c => !string.Equals(c.Label, "reviewed", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Reported, not failed: these are the cases Task 03 could not honestly
        // resolve, and they are listed so a human can settle them.
        foreach (var @case in uncertain)
        {
            _output.WriteLine($"NEEDS REVIEW: {@case.Id} - {@case.Path}");
        }

        Assert.All(uncertain, c => Assert.Contains("review", c.Reasoning, StringComparison.OrdinalIgnoreCase));
    }
}
