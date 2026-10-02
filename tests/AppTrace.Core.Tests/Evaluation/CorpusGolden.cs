namespace AppTrace.Core.Tests.Evaluation;

/// <summary>
/// The frozen set of corpus cases the engine does not yet satisfy.
/// </summary>
/// <remarks>
/// This is what lets the ordinary test suite stay green while V2 is unbuilt, and
/// still fail the moment an attribution change alters a known case in either
/// direction. A suite that is permanently red teaches nothing; a suite that
/// silently absorbs change teaches less.
/// </remarks>
internal sealed class CorpusGolden
{
    public IReadOnlyList<string> UnresolvedCases { get; init; } = [];

    /// <summary>
    /// The number of confident ownership claims that are wrong. May only go down;
    /// an increase means an attribution change made the engine more confidently
    /// incorrect, which is the one outcome this project refuses.
    /// </summary>
    public int WrongOwnerAtHighOrConfirmed { get; init; }

    public int HighOrConfirmedClaims { get; init; }

    public int TotalCases { get; init; }

    public static CorpusGolden Load() => AttributionCorpus.LoadGolden();
}
