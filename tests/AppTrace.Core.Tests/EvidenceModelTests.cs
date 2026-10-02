using AppTrace.Core.Attribution;
using AppTrace.Core.Model;

namespace AppTrace.Core.Tests;

/// <summary>
/// Task 04: evidence kind, specificity, and the confidence semantics they enforce.
/// </summary>
/// <remarks>
/// The rule under test is "evidence must earn the strength of the claim it
/// supports". Phase 0 summed heterogeneous records, so a name resemblance plus the
/// generic structure of "this is an application-data location" reached HIGH even
/// though neither record showed the application ever touched the path.
/// </remarks>
public class EvidenceModelTests
{
    // ---- Every evidence type answers a known question ---------------------

    [Theory]
    // Identity: says what the path or binary appears to be.
    [InlineData(EvidenceType.ExactDirectoryNameMatch, EvidenceKind.Identity)]
    [InlineData(EvidenceType.NormalizedNameMatch, EvidenceKind.Identity)]
    [InlineData(EvidenceType.ExecutableMetadataMatch, EvidenceKind.Identity)]
    // Provenance: written by the application's own installation or by another
    // component that registered the path for it. Only these can carry a confident
    // ownership claim.
    [InlineData(EvidenceType.DeclaredInstallLocation, EvidenceKind.Provenance)]
    [InlineData(EvidenceType.InstallLocationMatch, EvidenceKind.Provenance)]
    [InlineData(EvidenceType.DiscoveryLocationMatch, EvidenceKind.Provenance)]
    [InlineData(EvidenceType.ProductCodeMatch, EvidenceKind.Provenance)]
    [InlineData(EvidenceType.RegistryReference, EvidenceKind.Provenance)]
    // Structure: describes the location, never the owner.
    [InlineData(EvidenceType.KnownApplicationPath, EvidenceKind.Structure)]
    [InlineData(EvidenceType.KnownPublisherNamespace, EvidenceKind.Structure)]
    [InlineData(EvidenceType.ParentDirectoryMatch, EvidenceKind.Structure)]
    [InlineData(EvidenceType.ChildDirectoryMatch, EvidenceKind.Structure)]
    [InlineData(EvidenceType.GenericDirectoryName, EvidenceKind.Structure)]
    // Ambiguity is a state of the location, not opposition to a claim: several
    // applications being plausible owners is a reason to report SHARED or keep
    // descending, never a reason to say one of them is wrong.
    [InlineData(EvidenceType.MultipleCandidateOwners, EvidenceKind.Structure)]
    [InlineData(EvidenceType.SharedPublisherDirectory, EvidenceKind.Structure)]
    [InlineData(EvidenceType.PublisherMatch, EvidenceKind.Structure)]
    // Relationship: reserved for the Owns/RelatedTo model.
    [InlineData(EvidenceType.UnknownApplication, EvidenceKind.Relationship)]
    // Contradiction: names a differently identified owner, or the system itself.
    [InlineData(EvidenceType.PublisherMismatch, EvidenceKind.Contradiction)]
    [InlineData(EvidenceType.ConflictingApplicationMatch, EvidenceKind.Contradiction)]
    [InlineData(EvidenceType.SystemManagedPath, EvidenceKind.Contradiction)]
    public void EveryEvidenceTypeAnswersAKnownQuestion(EvidenceType type, EvidenceKind expected)
        => Assert.Equal(expected, EvidenceClassification.KindOf(type));

    [Fact]
    public void EveryEvidenceTypeIsMapped()
    {
        // A new evidence type must be given a deliberate kind. The fallback is
        // Identity, which can never reach HIGH, so a forgotten mapping under-claims
        // rather than over-claims.
        Assert.All(
            Enum.GetValues<EvidenceType>(),
            type => Assert.True(Enum.IsDefined(EvidenceClassification.KindOf(type))));
    }

    [Fact]
    public void OnlyADifferentlyIdentifiedOwnerIsADecisiveContradiction()
    {
        Assert.Equal(ContradictionKind.Decisive, EvidenceClassification.ContradictionKindOf(EvidenceType.PublisherMismatch));
        Assert.Equal(ContradictionKind.Decisive, EvidenceClassification.ContradictionKindOf(EvidenceType.ConflictingApplicationMatch));
        Assert.Equal(ContradictionKind.Decisive, EvidenceClassification.ContradictionKindOf(EvidenceType.SystemManagedPath));

        // Ambiguity is not opposition.
        Assert.False(EvidenceClassification.IsDecisiveContradiction(EvidenceType.MultipleCandidateOwners));
        Assert.False(EvidenceClassification.IsDecisiveContradiction(EvidenceType.SharedPublisherDirectory));
        Assert.False(EvidenceClassification.IsDecisiveContradiction(EvidenceType.GenericDirectoryName));
    }

    [Fact]
    public void AccountingIsNotAnEvidenceKind()
    {
        // Measurement completeness - inaccessible files, skipped reparse points,
        // lower-bound sizes, scan errors - is orthogonal to attribution. It must
        // never become ownership evidence.
        var kinds = Enum.GetNames<EvidenceKind>();
        Assert.DoesNotContain("Accounting", kinds);
        Assert.Equal(5, kinds.Length);
    }

    // ---- Specificity is name coverage ------------------------------------

    [Theory]
    // Values measured from the real engine's normalization. Note that
    // NormalizeDisplayName drops noise words, so "ASUS Update Helper" normalizes to
    // "asus helper" (2 tokens) and "NVIDIA App 11.0.9.251" loses its version AND its
    // "app" token. These figures describe the folded names the engine actually
    // compares, not the display names a human reads.
    [InlineData("Vivaldi", "Vivaldi", 1.0)]                          // 1 of 1
    [InlineData("Chrome", "Google Chrome", 0.5)]                     // 1 of 2
    [InlineData("Edge", "Microsoft Edge", 0.5)]                      // 1 of 2
    [InlineData("OneDrive", "Microsoft OneDrive", 0.5)]              // 1 of 2
    [InlineData("sdk", "ASUS Aura SDK", 1.0 / 3)]                    // 1 of 3
    [InlineData("helper", "ASUS Update Helper", 0.5)]                // 1 of 2, "update" is a noise word
    [InlineData("Universal", "Universal Holtek RGB DRAM", 0.25)]     // 1 of 4
    [InlineData("node", "Node.js Krypton via nvm-windows", 0.2)]     // 1 of 5, "windows" is a noise word
    [InlineData("tool", "Epson Printer Driver Security Support Tool", 1.0 / 6)]
    public void NameCoverageIsTheFractionOfTheProductsNameThatMatches(string directory, string product, double expected)
    {
        // Uses the same tokenization the engine uses. TextNormalizer.Tokens splits a
        // folded name on spaces, so it must be fed a normalized display name rather
        // than an already-folded token.
        var appSegments = TextNormalizer.Tokens(TextNormalizer.NormalizeDisplayName(product)).ToArray();
        var actual = AttributionEngine.CoverageOf(directory, appSegments);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Fact]
    public void ASingleTokenOfALongNameIsLowSpecificity()
    {
        // The measured discriminator: the observed false positives sit at 0.33 and
        // below, the observed correct attributions at 0.50 and above. That boundary
        // is a heuristic fitted to the cases we have - it is used only to withhold
        // strength, never to grant it. Task 05 is what removes those candidates
        // outright.
        var sdkCoverage = AttributionEngine.CoverageOf("sdk", ["asus", "aura", "sdk"]);
        var vivaldiCoverage = AttributionEngine.CoverageOf("Vivaldi", ["vivaldi"]);

        Assert.True(sdkCoverage < 0.5);
        Assert.True(vivaldiCoverage >= 0.5);
    }

    // ---- Executable metadata names a whole product, not a token -----------

    [Theory]
    [InlineData("GitHub, Inc.", "Git", false)]         // the DaVinci/Electron failure
    [InlineData("GitHub, Inc.", "GitHub Desktop", true)]
    [InlineData("Electron", "Git", false)]
    [InlineData("Google Chrome", "Google Chrome", true)]
    [InlineData("NVIDIA App", "NVIDIA App", true)]
    [InlineData("Microsoft® Windows® Operating System", "Microsoft Edge", false)]
    [InlineData("Updater", "CH Aurora", false)]
    [InlineData("electron.exe", "Electron", true)]
    public void MetadataIdentifiesAWholeProductOrNothing(string metadataValue, string product, bool expected)
    {
        // Passes both tokenizations, exactly as the engine does: the normalized name
        // (which drops generic tokens) and the raw display name.
        var normalized = TextNormalizer.Tokens(TextNormalizer.NormalizeDisplayName(product)).ToArray();
        var raw = product
            .Split([' ', '.', ',', '®', '™', '©'], StringSplitOptions.RemoveEmptyEntries)
            .Select(TextNormalizer.Fold)
            .Where(s => s.Length > 0)
            .ToArray();

        Assert.Equal(expected, AttributionEngine.IdentifiesWholeProduct(metadataValue, normalized, raw));
    }
}
