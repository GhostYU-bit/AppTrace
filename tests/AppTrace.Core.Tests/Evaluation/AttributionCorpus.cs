namespace AppTrace.Core.Tests.Evaluation;

using System.Text.Json;
using System.Text.Json.Serialization;
using AppTrace.Core.Model;

/// <summary>One synthetic installed application available to corpus cases.</summary>
internal sealed class CorpusApp
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string? Publisher { get; init; }

    public string? InstallLocation { get; init; }

    public string? Version { get; init; }
}

/// <summary>
/// What a corpus case should look like once the attribution engine is good enough.
/// </summary>
/// <remarks>
/// Expressed as constraints rather than one exact label, so "correct owner, a
/// confidence step lower" is not treated as a regression. See
/// <see cref="ClassificationComparison"/>.
/// </remarks>
internal sealed class CorpusDesired
{
    /// <summary>Expected owner's display name, or null when nothing should be accepted.</summary>
    public string? Owner { get; init; }

    /// <summary>Minimum acceptable classification, when the scalar ladder applies.</summary>
    public string? MinClassification { get; init; }

    /// <summary>Explicit allowed classifications, for the structural outcomes.</summary>
    public IReadOnlyList<string> AllowedClassifications { get; init; } = [];

    /// <summary>Reserved for the V2 relationship model (Task 06). Not yet reachable.</summary>
    public string? Relation { get; init; }

    public string? RelationNote { get; init; }
}

/// <summary>
/// What the Phase 0 engine actually does today, recorded so the normal test suite
/// can stay green while the gap to <see cref="Desired"/> is reported.
/// </summary>
/// <remarks>
/// These are baselines, not aspirations. When a V2 task changes one, the failure
/// is the signal: update the baseline deliberately and record the change in the
/// task's report.
/// </remarks>
internal sealed class CorpusCurrent
{
    public string? Owner { get; init; }

    public string? Classification { get; init; }

    /// <summary>Evidence types expected to carry the claim today, for a stronger baseline.</summary>
    public IReadOnlyList<string> SupportingEvidence { get; init; } = [];
}

/// <summary>One attribution case: a path, the applications in scope, and both expectations.</summary>
internal sealed class CorpusCase
{
    public required string Id { get; init; }

    public required string Description { get; init; }

    public required string Path { get; init; }

    /// <summary>Location category the scanner would assign to this path.</summary>
    public string Category { get; init; } = "ProgramFiles";

    /// <summary>
    /// Ancestor paths the scanner would have accepted as owned before reaching
    /// this path. Models the traversal context a real scan provides.
    /// </summary>
    public IReadOnlyList<string> AcceptedAncestors { get; init; } = [];

    public IReadOnlyList<string> Apps { get; init; } = [];

    public required string Reason { get; init; }

    public required CorpusDesired Desired { get; init; }

    /// <summary>Null when Phase 0's outcome has not been recorded yet.</summary>
    public CorpusCurrent? Current { get; init; }
}

/// <summary>The corpus document.</summary>
internal sealed class CorpusDocument
{
    public string? Comment { get; init; }

    public IReadOnlyList<CorpusApp> Apps { get; init; } = [];

    public IReadOnlyList<CorpusCase> Cases { get; init; } = [];
}

/// <summary>
/// An evaluation case: a real machine path, the applications the engine is given,
/// and hand-reviewed ground truth.
/// </summary>
/// <remarks>
/// Deliberately a separate type from <see cref="CorpusCase"/>. The corpus encodes
/// synthetic Path/App pairs for rule-level testing, including fictional
/// applications; the evaluation set encodes real paths with hand-labelled truth
/// and must never be generated from the engine's own output.
/// </remarks>
internal sealed class EvaluationCase
{
    public required string Id { get; init; }

    public required string Description { get; init; }

    public required string Path { get; init; }

    public string Category { get; init; } = "ProgramFiles";

    public IReadOnlyList<string> AcceptedAncestors { get; init; } = [];

    /// <summary>Applications the engine is given, matching what the real scan discovered.</summary>
    public IReadOnlyList<string> Apps { get; init; } = [];

    /// <summary>
    /// Hand-reviewed owner, or null when no installed application owns this path.
    /// Null is a real answer, not "unknown to the labeller" — use
    /// <see cref="Confidence"/> to express how sure the label is.
    /// </summary>
    public string? Owner { get; init; }

    public string? MinClassification { get; init; }

    public IReadOnlyList<string> AllowedClassifications { get; init; } = [];

    /// <summary>How sure the human label is: "high" or "medium".</summary>
    public string Confidence { get; init; } = "high";

    /// <summary>
    /// "reviewed" for a label that can be evaluated, "uncertain" for one that
    /// cannot honestly be justified yet.
    /// </summary>
    /// <remarks>
    /// Uncertain cases are reported with their open question but excluded from the
    /// metrics. Counting a guess as ground truth would corrupt the very number the
    /// harness exists to protect.
    /// </remarks>
    public string Label { get; init; } = "reviewed";

    /// <summary>Why this label is correct, and what it tests.</summary>
    public required string Reasoning { get; init; }
}

/// <summary>The hand-labelled evaluation document.</summary>
internal sealed class EvaluationDocument
{
    public string? Comment { get; init; }

    public IReadOnlyList<CorpusApp> Apps { get; init; } = [];

    public IReadOnlyList<EvaluationCase> Cases { get; init; } = [];
}

/// <summary>Loads the JSON fixtures that ship beside the tests.</summary>
internal static class AttributionCorpus
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CorpusDocument? _cached;

    /// <summary>Absolute path of the corpus file, resolved from the test output directory.</summary>
    public static string CorpusPath => Locate("attribution-corpus.json");

    /// <summary>Absolute path of the frozen unresolved-case baseline.</summary>
    public static string GoldenPath => Locate("corpus-golden.json");

    /// <summary>Absolute path of the hand-labelled real-machine evaluation set.</summary>
    public static string EvaluationPath => Locate("real-machine-labels.json");

    public static CorpusDocument Load() => _cached ??= LoadFile(CorpusPath);

    public static CorpusGolden LoadGolden()
        => JsonSerializer.Deserialize<CorpusGolden>(File.ReadAllText(GoldenPath), Options)
           ?? throw new InvalidOperationException($"Golden file '{GoldenPath}' deserialised to null.");

    public static EvaluationDocument LoadEvaluation()
    {
        var path = EvaluationPath;
        var document = JsonSerializer.Deserialize<EvaluationDocument>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException($"Evaluation file '{path}' deserialised to null.");

        ValidateEvaluation(document, path);
        return document;
    }

    /// <summary>Writes a text artefact next to the test binaries for inspection.</summary>
    public static string WriteArtefact(string fileName, string content)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "evaluation");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Fails fast on a malformed evaluation set. A typo must never be silently
    /// skipped, because a skipped case looks exactly like a passing one.
    /// </summary>
    public static void ValidateEvaluation(EvaluationDocument document, string origin)
    {
        var problems = new List<string>();
        var appIds = document.Apps.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var group in document.Cases.GroupBy(c => c.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"duplicate case id '{group.Key}'");
        }

        foreach (var @case in document.Cases)
        {
            if (string.IsNullOrWhiteSpace(@case.Id)) { problems.Add("an evaluation case has no id"); }
            if (string.IsNullOrWhiteSpace(@case.Path)) { problems.Add($"case '{@case.Id}' has no path"); }
            if (string.IsNullOrWhiteSpace(@case.Reasoning)) { problems.Add($"case '{@case.Id}' has no reasoning"); }
            if (@case.Apps.Count == 0) { problems.Add($"case '{@case.Id}' lists no applications"); }
            if (@case.Confidence is not ("high" or "medium")) { problems.Add($"case '{@case.Id}' has invalid confidence '{@case.Confidence}'"); }
            if (@case.Label is not ("reviewed" or "uncertain")) { problems.Add($"case '{@case.Id}' has invalid label '{@case.Label}'"); }

            foreach (var appId in @case.Apps.Where(a => !appIds.Contains(a)))
            {
                problems.Add($"case '{@case.Id}' references undefined app '{appId}'");
            }

            if (@case.Owner is not null
                && !@case.Apps.Any(a => string.Equals(document.Apps.First(x => x.Id == a).DisplayName, @case.Owner, StringComparison.Ordinal)))
            {
                problems.Add($"case '{@case.Id}' labels owner '{@case.Owner}', which is not among the applications it supplies");
            }

            if (@case.Owner is null && @case.MinClassification is not null
                && !string.Equals(@case.MinClassification, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"case '{@case.Id}' has no owner but requires classification '{@case.MinClassification}'");
            }

            foreach (var value in new[] { @case.MinClassification }.Where(v => v is not null).Concat(@case.AllowedClassifications))
            {
                if (!Enum.TryParse<Classification>(value, ignoreCase: true, out _))
                {
                    problems.Add($"case '{@case.Id}' has unknown classification '{value}'");
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"Evaluation set '{origin}' is invalid:{Environment.NewLine}  - " +
                string.Join($"{Environment.NewLine}  - ", problems));
        }
    }

    public static CorpusDocument LoadFile(string path)
    {
        var document = JsonSerializer.Deserialize<CorpusDocument>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException($"Corpus file '{path}' deserialised to null.");

        Validate(document, path);
        return document;
    }

    /// <summary>
    /// Fails fast on a malformed corpus. A typo in a case must never be silently
    /// skipped, because a skipped case looks exactly like a passing one.
    /// </summary>
    public static void Validate(CorpusDocument document, string origin)
    {
        var problems = new List<string>();
        var appIds = document.Apps.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var group in document.Cases.GroupBy(c => c.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"duplicate case id '{group.Key}'");
        }

        foreach (var @case in document.Cases)
        {
            if (string.IsNullOrWhiteSpace(@case.Id)) { problems.Add("a case has no id"); }
            if (string.IsNullOrWhiteSpace(@case.Path)) { problems.Add($"case '{@case.Id}' has no path"); }
            if (string.IsNullOrWhiteSpace(@case.Reason)) { problems.Add($"case '{@case.Id}' has no reason"); }
            if (@case.Apps.Count == 0) { problems.Add($"case '{@case.Id}' lists no applications"); }

            foreach (var appId in @case.Apps.Where(a => !appIds.Contains(a)))
            {
                problems.Add($"case '{@case.Id}' references undefined app '{appId}'");
            }

            if (!Enum.TryParse<LocationCategory>(@case.Category, ignoreCase: true, out _))
            {
                problems.Add($"case '{@case.Id}' has unknown category '{@case.Category}'");
            }

            foreach (var value in new[] { @case.Desired.MinClassification, @case.Current?.Classification }
                         .Where(v => v is not null))
            {
                if (!Enum.TryParse<Classification>(value, ignoreCase: true, out _))
                {
                    problems.Add($"case '{@case.Id}' has unknown classification '{value}'");
                }
            }

            foreach (var value in @case.Desired.AllowedClassifications)
            {
                if (!Enum.TryParse<Classification>(value, ignoreCase: true, out _))
                {
                    problems.Add($"case '{@case.Id}' allows unknown classification '{value}'");
                }
            }

            if (@case.Desired.Owner is not null && !@case.Apps.Any(a =>
                    string.Equals(document.Apps.First(x => x.Id == a).DisplayName, @case.Desired.Owner, StringComparison.Ordinal)))
            {
                problems.Add($"case '{@case.Id}' expects owner '{@case.Desired.Owner}', which is not among its applications");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"Attribution corpus '{origin}' is invalid:{Environment.NewLine}  - " +
                string.Join($"{Environment.NewLine}  - ", problems));
        }
    }

    /// <summary>
    /// Resolves a data file by walking up from the test binaries to the repository
    /// root, so the fixtures live beside the tests rather than in build output.
    /// </summary>
    private static string Locate(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "tests", "AppTrace.Core.Tests", "Evaluation", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate '{fileName}' under any 'tests/AppTrace.Core.Tests/Evaluation' directory above '{AppContext.BaseDirectory}'.");
    }
}
