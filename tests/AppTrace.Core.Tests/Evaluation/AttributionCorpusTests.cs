namespace AppTrace.Core.Tests.Evaluation;

/// <summary>
/// Runs the attribution regression corpus against the current engine.
/// </summary>
/// <remarks>
/// <para>The suite is deliberately green. It asserts what must not drift and
/// reports everything else:</para>
/// <list type="number">
/// <item><b>Phase 0 behaviour is unchanged.</b> Each case's recorded
/// <c>current</c> block is asserted. If an attribution change alters a known case,
/// this fails — which is the point: the change must be recorded deliberately.</item>
/// <item><b>The known-defect set is frozen.</b> The set of cases whose V2
/// expectation is unmet is compared with <c>corpus-golden.json</c>, so a case
/// cannot silently become unresolved (a regression) or silently become resolved
/// (an unrecorded improvement).</item>
/// </list>
/// <para>The gap between current and desired behaviour is written to
/// <c>bin/&lt;config&gt;/evaluation/corpus-report.txt</c>, and the headline numbers
/// are emitted on every run.</para>
/// </remarks>
public class AttributionCorpusTests
{
    private readonly ITestOutputHelper _output;

    public AttributionCorpusTests(ITestOutputHelper output) => _output = output;

    private static EvaluationOutcome[] RunAll(CorpusDocument document)
        => document.Cases.Select(c => AttributionEvaluator.Run(c, document)).ToArray();

    [Fact]
    public void CorpusIsWellFormed()
    {
        // Load() validates structure, undefined app references, unknown enum values
        // and mismatched owner names, and throws with a full problem list.
        var document = AttributionCorpus.Load();

        Assert.NotEmpty(document.Cases);
        Assert.All(document.Cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Reason)));
    }

    [Fact]
    public void EveryCaseReproducesItsRecordedPhase0Behaviour()
    {
        var document = AttributionCorpus.Load();
        var drift = RunAll(document)
            .Where(o => o.BaselineDrift is { Count: > 0 })
            .Select(o => $"{o.Id}: {string.Join("; ", o.BaselineDrift!)}")
            .ToArray();

        Assert.True(
            drift.Length == 0,
            "The engine no longer produces the recorded Phase 0 behaviour for these cases. " +
            "If the change is intended, update the 'current' block in attribution-corpus.json " +
            "and record it in the task report:" + Environment.NewLine + string.Join(Environment.NewLine, drift));
    }

    [Fact]
    public void KnownDefectSetMatchesTheFrozenBaseline()
    {
        var document = AttributionCorpus.Load();
        var golden = CorpusGolden.Load();
        var outcomes = RunAll(document);

        var unresolved = outcomes.Where(o => !o.Met).Select(o => o.Id).OrderBy(i => i, StringComparer.Ordinal).ToArray();
        var expected = golden.UnresolvedCases.OrderBy(i => i, StringComparer.Ordinal).ToArray();

        var newlyUnresolved = unresolved.Except(expected, StringComparer.Ordinal).ToArray();
        var newlyResolved = expected.Except(unresolved, StringComparer.Ordinal).ToArray();

        Assert.True(
            newlyUnresolved.Length == 0,
            "Attribution regressed on these previously-satisfied cases: " + string.Join(", ", newlyUnresolved));

        Assert.True(
            newlyResolved.Length == 0,
            "These cases now satisfy their V2 expectation. That is progress, not a fault - " +
            "remove them from corpus-golden.json so the improvement is recorded: " + string.Join(", ", newlyResolved));

        Assert.Equal(golden.TotalCases, outcomes.Length);
    }

    [Fact]
    public void ConfidentlyWrongOwnershipClaimsDoNotIncrease()
    {
        // The single most important measurement in the project. An honest UNKNOWN
        // is acceptable; a confident wrong owner is not. This may only go down.
        var document = AttributionCorpus.Load();
        var golden = CorpusGolden.Load();
        var metrics = EvaluationMetrics.From(RunAll(document));

        _output.WriteLine($"WRONG-OWNER HIGH/CONFIRMED CLAIMS: {metrics.WrongOwnerAtHighOrConfirmed} of {metrics.HighOrConfirmedClaims} confident claims");
        _output.WriteLine($"frozen baseline: {golden.WrongOwnerAtHighOrConfirmed} of {golden.HighOrConfirmedClaims}");

        Assert.True(
            metrics.WrongOwnerAtHighOrConfirmed <= golden.WrongOwnerAtHighOrConfirmed,
            $"Confidently wrong ownership claims rose from {golden.WrongOwnerAtHighOrConfirmed} to " +
            $"{metrics.WrongOwnerAtHighOrConfirmed}. This is the one regression AppTrace must never accept.");

        if (metrics.WrongOwnerAtHighOrConfirmed < golden.WrongOwnerAtHighOrConfirmed)
        {
            _output.WriteLine("IMPROVEMENT: lower 'wrongOwnerAtHighOrConfirmed' in corpus-golden.json to record it.");
        }
    }

    [Fact]
    public void WritesTheCorpusReport()
    {
        var document = AttributionCorpus.Load();
        var outcomes = RunAll(document);
        var report = EvaluationReport.Render(
            "Attribution regression corpus - current engine vs desired V2",
            AttributionCorpus.CorpusPath,
            outcomes);

        var path = AttributionCorpus.WriteArtefact("corpus-report.txt", report);

        foreach (var line in Headline(report))
        {
            _output.WriteLine(line);
        }

        _output.WriteLine($"full report: {path}");

        Assert.Contains("WRONG-OWNER HIGH/CONFIRMED CLAIMS", report, StringComparison.Ordinal);
    }

    /// <summary>Extracts the summary lines so the numbers are visible in the test log.</summary>
    internal static IEnumerable<string> Headline(string report)
        => report.Split(Environment.NewLine)
            .Where(l => l.StartsWith("***", StringComparison.Ordinal)
                        || (l.StartsWith("  ", StringComparison.Ordinal) && l.Contains(':'))
                        || l.StartsWith("total cases", StringComparison.Ordinal))
            .Select(l => l.Trim());
}
