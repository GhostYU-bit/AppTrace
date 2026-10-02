using System.Reflection;
using AppTrace.Core.Attribution;
using AppTrace.Core.Model;

namespace AppTrace.Core.Tests;

/// <summary>
/// Task 04: the confidence ladder. Evidence kind decides the ceiling; the score
/// only orders and corroborates within it.
/// </summary>
public class ConfidenceSemanticsTests
{
    // ---- Structure can no longer inflate a claim -------------------------

    [Fact]
    public void NameMatchPlusGenericApplicationPathIsNoLongerHigh()
    {
        // The exact combination the Task 02 spike identified: +30 and +8 reaching
        // 38, which Phase 0 called HIGH. "This is an application-data location" is
        // true of nearly every path AppTrace inspects and says nothing about which
        // application owns a directory.
        //
        // Task 05 goes further than withholding HIGH: the name "node" accounts for
        // one word of a five-word product name, so it is not distinctive enough to
        // propose that product at all. Unknown, not Medium.
        var node = Fixtures.App("Node.js Krypton via nvm-windows", "OpenJS Foundation");

        var attribution = Fixtures.Evaluate(
            @"C:\Users\user\AppData\Local\Doubao\User Data\sandbox_runtime\bases\abc\node",
            [node],
            LocationCategory.LocalAppData);

        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.NotEqual(Classification.High, attribution.Classification);
        Assert.NotEqual(Classification.Medium, attribution.Classification);
        Assert.Empty(attribution.AcceptedOwners);
    }

    [Fact]
    public void AGenericApplicationPathStillCapsAWeakNameMatchAtMedium()
    {
        // The Task 04 rule, isolated from Task 05's candidate generation: a name
        // match that IS distinctive enough to propose an owner, but has nothing
        // except "this is an application data area" behind it, is MEDIUM rather
        // than HIGH.
        var chrome = Fixtures.App("Google Chrome", "Google LLC");

        var attribution = Fixtures.Evaluate(
            @"C:\Users\user\AppData\Local\Chrome",
            [chrome],
            LocationCategory.LocalAppData);

        var owner = Fixtures.Candidate(attribution, "Google Chrome");
        Assert.NotNull(owner);
        Assert.True(owner.Accepted);
        Assert.Equal(Classification.Medium, owner.Classification);
        Assert.False(attribution.OwnershipEstablished);
    }

    [Fact]
    public void KnownApplicationPathCarriesNoWeight()
    {
        Assert.Equal(0, EvidenceWeights.WeightOf(EvidenceType.KnownApplicationPath));
    }

    [Fact]
    public void StructureAloneNeverAcceptsAnOwner()
    {
        // Being under AppData is structure. With no identity and no provenance it
        // must not produce a candidate at all.
        var tidy = Fixtures.App("Tidy Suite", "Tidy Software");

        var attribution = Fixtures.Evaluate(@"C:\Users\user\AppData\Local\SomeVendor", [tidy], LocationCategory.LocalAppData);

        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.Empty(attribution.AcceptedOwners);
    }

    // ---- Identity alone is capped at MEDIUM ------------------------------

    [Fact]
    public void IdentityWithoutProvenanceIsCappedAtMedium()
    {
        var app = Fixtures.App("Standalone Tool", "Standalone Ltd.");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Standalone Tool", [app]);

        var owner = Fixtures.Candidate(attribution, "Standalone Tool");
        Assert.NotNull(owner);
        Assert.True(owner.Accepted);
        Assert.Equal(Classification.Medium, owner.Classification);
    }

    // ---- Provenance plus identity is HIGH --------------------------------

    [Fact]
    public void ProvenancePlusCorroboratingIdentityIsHigh()
    {
        // The application declares a tree, and the directory inside it names the
        // product. This is the shape Task 04 means by HIGH: independently anchored,
        // and the name agrees. It is deliberately NOT the exact declared directory,
        // which is CONFIRMED.
        var app = Fixtures.App("Contoso Editor", "Contoso", @"C:\Program Files\Contoso");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Contoso\Contoso Editor", [app]);

        Assert.Equal(Classification.High, attribution.Classification);
        Assert.True(attribution.OwnershipEstablished);
    }

    [Fact]
    public void ExactDeclaredDirectoryIsConfirmedRatherThanHigh()
    {
        var app = Fixtures.App("Contoso Editor", "Contoso", @"C:\Program Files\Contoso\Contoso Editor");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Contoso\Contoso Editor", [app]);

        Assert.Equal(Classification.Confirmed, attribution.Classification);
    }

    [Fact]
    public void ProvenanceWithoutCorroboratingIdentityIsOnlyMedium()
    {
        // The application is anchored inside this tree, but the directory name does
        // not identify it. Strong, yet not a HIGH claim about this exact directory.
        var app = Fixtures.App("Contoso Editor", "Contoso", @"C:\Program Files\Contoso");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Contoso\Cache", [app]);

        var owner = Fixtures.Candidate(attribution, "Contoso Editor");
        Assert.NotNull(owner);
        Assert.Equal(Classification.Medium, owner.Classification);
    }

    // ---- CONFIRMED is preserved ------------------------------------------

    [Fact]
    public void DeclaredInstallLocationStaysConfirmed()
    {
        var app = Fixtures.App("Confirmed App", "Confirmed Ltd.", @"C:\Program Files\Confirmed App");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Confirmed App", [app]);

        Assert.Equal(Classification.Confirmed, attribution.Classification);
    }

    // ---- A decisive contradiction is a gate, not a weight ---------------

    /// <summary>
    /// Exercises the classification gate directly.
    /// </summary>
    /// <remarks>
    /// Deliberately not routed through the detectors: the point of the test is that
    /// <em>classification itself</em> refuses to be outvoted. Driving it through
    /// live detectors would make the assertion depend on which contradiction a
    /// detector happens to emit, which is a different and weaker claim.
    /// </remarks>
    private static Classification ClassifyDirectly(IReadOnlyList<Evidence> evidence)
    {
        var method = typeof(AttributionEngine).GetMethod(
            "ClassifyCandidate",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AttributionEngine.ClassifyCandidate was not found.");

        var candidate = new CandidateOwner { AppId = "app-test", Evidence = evidence, Score = evidence.Sum(e => e.Weight) };
        return (Classification)method.Invoke(null, [candidate])!;
    }

    private static Evidence Record(EvidenceType type, bool supports, double specificity = 1.0)
        => new()
        {
            Type = type,
            Description = "test record",
            Strength = supports ? EvidenceStrength.Strong : EvidenceStrength.Moderate,
            Source = EvidenceSource.Derived,
            SupportsAttribution = supports,
            Weight = supports ? EvidenceWeights.WeightOf(type) : -Math.Abs(EvidenceWeights.WeightOf(type)),
            Specificity = specificity,
        };

    [Fact]
    public void SeveralWeakSupportingRecordsCannotOutvoteADecisiveContradiction()
    {
        var evidence = new List<Evidence>
        {
            // Five plausible-looking supporting records, which under additive scoring
            // would comfortably beat a single -25.
            Record(EvidenceType.NormalizedNameMatch, true, specificity: 0.5),
            Record(EvidenceType.ExactDirectoryNameMatch, true),
            Record(EvidenceType.ExecutableMetadataMatch, true),
            Record(EvidenceType.KnownPublisherNamespace, true),
            Record(EvidenceType.PublisherMatch, true),
            // One decisive contradiction.
            Record(EvidenceType.PublisherMismatch, false),
        };

        Assert.Equal(Classification.Unknown, ClassifyDirectly(evidence));
    }

    [Fact]
    public void ALimitingContradictionCapsButDoesNotForbid()
    {
        var evidence = new List<Evidence>
        {
            Record(EvidenceType.ExactDirectoryNameMatch, true),
            Record(EvidenceType.GenericDirectoryName, false),
        };

        // GenericDirectoryName is Structure, not a contradiction, so it only
        // subtracts. The claim survives at MEDIUM rather than being killed.
        Assert.Equal(Classification.Medium, ClassifyDirectly(evidence));
    }

    [Fact]
    public void DecisiveContradictionAlsoForbidsAProvenanceClaim()
    {
        var evidence = new List<Evidence>
        {
            Record(EvidenceType.InstallLocationMatch, true),
            Record(EvidenceType.ExactDirectoryNameMatch, true),
            Record(EvidenceType.ConflictingApplicationMatch, false),
        };

        Assert.Equal(Classification.Unknown, ClassifyDirectly(evidence));
    }

    [Fact]
    public void LowSpecificityNameMatchCannotCorroborateProvenanceIntoHigh()
    {
        // Provenance is present and real, but the directory name accounts for only a
        // sixth of the product's name. That is the shape of every measured false
        // positive, so it must not be promoted.
        var evidence = new List<Evidence>
        {
            Record(EvidenceType.InstallLocationMatch, true),
            Record(EvidenceType.NormalizedNameMatch, true, specificity: 1.0 / 6),
        };

        Assert.Equal(Classification.Medium, ClassifyDirectly(evidence));
    }
}
