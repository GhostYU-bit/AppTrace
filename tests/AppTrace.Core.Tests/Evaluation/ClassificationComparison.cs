namespace AppTrace.Core.Tests.Evaluation;

using AppTrace.Core.Model;

/// <summary>
/// Compares classifications without ranking the two structural outcomes against
/// the four strength outcomes.
/// </summary>
/// <remarks>
/// <para><see cref="Classification.Shared"/> and <see cref="Classification.Ambiguous"/>
/// are statements about a <em>location</em> with several owners. They are not
/// points on the CONFIDENT..UNKNOWN line, so ordering them would be meaningless.
/// This mirrors <c>ClassificationExtensions.IsConfident</c> and <c>IsUncertain</c>.</para>
/// <para>The corpus therefore expresses expectations as a <em>bound</em>
/// (a minimum, or an explicit set) rather than always demanding one exact label.
/// That is what lets "correct owner, slightly lower confidence" be recorded as
/// acceptable rather than as a regression — the correction Task 03 was asked to
/// make to the original regression philosophy.</para>
/// </remarks>
internal static class ClassificationComparison
{
    /// <summary>Strength order for the four scalar classifications.</summary>
    private static readonly Dictionary<Classification, int> StrengthOrder = new()
    {
        [Classification.Unknown] = 0,
        [Classification.Low] = 1,
        [Classification.Medium] = 2,
        [Classification.High] = 3,
        [Classification.Confirmed] = 4,
    };

    public static bool IsScalar(Classification classification) => StrengthOrder.ContainsKey(classification);

    public static int Strength(Classification classification)
        => StrengthOrder.TryGetValue(classification, out var value) ? value : 0;

    /// <summary>
    /// True when <paramref name="actual"/> satisfies the bound described by
    /// <paramref name="minimum"/> and <paramref name="allowed"/>.
    /// </summary>
    public static bool Satisfies(Classification actual, Classification? minimum, IReadOnlyList<Classification> allowed)
    {
        if (allowed.Count > 0)
        {
            return allowed.Contains(actual);
        }

        if (minimum is null)
        {
            return true;
        }

        return IsScalar(actual)
            && IsScalar(minimum.Value)
            && Strength(actual) >= Strength(minimum.Value);
    }

    public static string Describe(Classification? minimum, IReadOnlyList<Classification> allowed)
    {
        if (allowed.Count > 0)
        {
            return "one of " + string.Join(" / ", allowed);
        }

        return minimum is null ? "any" : "at least " + minimum.Value.ToString().ToUpperInvariant();
    }
}
