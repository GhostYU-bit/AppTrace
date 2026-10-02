using AppTrace.Core.Attribution;
using AppTrace.Core.Model;

namespace AppTrace.Core.Tests;

/// <summary>
/// Covers the evidence-to-classification rules that Phase 0 depends on.
/// </summary>
/// <remarks>
/// Every case here is synthetic: it fixes an application list and a path, so the
/// expectations never depend on what happens to be installed on the machine
/// running the tests.
/// </remarks>
public class AttributionRulesTests
{
    // ---- Exact and normalized name matching -------------------------------

    [Fact]
    public void ExactAppNameDirectory_KeepsTheCorrectOwnerButIsNoLongerHigh()
    {
        // Realistic per-user data case: the folder name matches the application and
        // the location is the expected per-user data area. The owner is right, and
        // must stay right.
        var discord = Fixtures.App("Discord", "Discord Inc.");

        var attribution = Fixtures.Evaluate(
            @"C:\Users\User\AppData\Roaming\discord",
            [discord],
            LocationCategory.RoamingAppData);

        var owner = Fixtures.Candidate(attribution, "Discord");
        Assert.NotNull(owner);
        Assert.True(owner.Accepted);
        Assert.Contains(owner.Evidence, e => e.Type == EvidenceType.ExactDirectoryNameMatch);

        // Task 04: HIGH now requires provenance, and nothing registers this
        // directory. The name match plus "this is an application-data area" is not
        // ownership evidence, so MEDIUM is the honest ceiling. This is the whole
        // point of the change — correctness of the owner is preserved, the
        // confidence claim is not inflated.
        Assert.Equal(Classification.Medium, attribution.Classification);
    }

    [Fact]
    public void LoneNameMatchWithoutCorroboration_StaysMedium()
    {
        // A name match on its own is plausible, not strong: any application could
        // have created a directory with this name.
        var app = Fixtures.App("Frobnicator", "Unrelated Holdings Ltd.");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Frobnicator", [app]);

        var owner = Fixtures.Candidate(attribution, "Frobnicator");
        Assert.NotNull(owner);
        Assert.True(owner.Accepted);
        Assert.Equal(Classification.Medium, owner.Classification);
    }

    [Fact]
    public void DirectoryNameDifferingOnlyByPunctuation_IsExactMatch()
    {
        // "Notepad++" folds to "notepad" exactly like the folder does.
        var app = Fixtures.App("Notepad++");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Notepad", [app]);

        var owner = Fixtures.Candidate(attribution, "Notepad++");
        Assert.NotNull(owner);
        Assert.Contains(owner.Evidence, e => e.Type == EvidenceType.ExactDirectoryNameMatch);
    }

    [Fact]
    public void MultiWordAppName_MatchesItsWholeWordDirectory_ButNotAsExact()
    {
        var app = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Adobe\Acrobat DC", [app]);

        var owner = Fixtures.Candidate(attribution, "Adobe Acrobat DC");
        Assert.NotNull(owner);
        Assert.Contains(owner.Evidence, e => e.Type == EvidenceType.NormalizedNameMatch);
        Assert.DoesNotContain(owner.Evidence, e => e.Type == EvidenceType.ExactDirectoryNameMatch);
    }

    [Fact]
    public void VendorDirectory_IsNotClaimedByAProductWhoseNameMerelyContainsIt()
    {
        // Regression guard: folded substring matching would let "googlechrome"
        // claim the "Google" vendor directory.
        var chrome = Fixtures.App("Google Chrome", "Google LLC");
        var drive = Fixtures.App("Google Drive", "Google LLC");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Google", [chrome, drive]);

        Assert.DoesNotContain(
            attribution.Candidates.SelectMany(c => c.Evidence),
            e => e.Type is EvidenceType.ExactDirectoryNameMatch or EvidenceType.NormalizedNameMatch);
        Assert.False(attribution.OwnershipEstablished);
    }

    // ---- Publisher namespaces --------------------------------------------

    [Fact]
    public void PublisherDirectoryWithOneProduct_IsExplainedAsANamespace()
    {
        var app = Fixtures.App("Contoso Studio", "Contoso");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Contoso", [app]);

        var candidate = Fixtures.Candidate(attribution, "Contoso Studio");
        Assert.NotNull(candidate);
        Assert.Contains(candidate.Evidence, e => e.Type == EvidenceType.KnownPublisherNamespace);
        Assert.DoesNotContain(candidate.Evidence, e => e.Type == EvidenceType.SharedPublisherDirectory);
    }

    [Fact]
    public void PublisherDirectoryWithSeveralProducts_IsReportedSharedAndNeverOwnedByOne()
    {
        var photoshop = Fixtures.App("Adobe Photoshop", "Adobe Inc.", @"C:\Adobe");
        var premiere = Fixtures.App("Adobe Premiere Pro", "Adobe Inc.", @"C:\Adobe");
        var afterEffects = Fixtures.App("Adobe After Effects", "Adobe Inc.", @"C:\Adobe");

        // The directory is the vendor namespace these products declared, so it is
        // reported as shared — but never confirmed, and never closed for one of
        // them, because the products' data lives in the subdirectories.
        var attribution = Fixtures.Evaluate(@"C:\Adobe", [photoshop, premiere, afterEffects]);

        Assert.Equal(Classification.Shared, attribution.Classification);
        Assert.False(attribution.OwnershipEstablished);
        Assert.Equal(3, attribution.AcceptedOwners.Count);
        Assert.DoesNotContain(
            attribution.Candidates.SelectMany(c => c.Evidence),
            e => e.Type == EvidenceType.DeclaredInstallLocation);
    }

    [Fact]
    public void VendorNamespaceWithoutAnInstallLocationClaim_StaysOpen()
    {
        // Three Adobe products exist, so "Adobe" is a vendor namespace — but no
        // product declared this particular directory, and a vendor name is not a
        // product name. Nothing may be accepted here.
        var photoshop = Fixtures.App("Adobe Photoshop", "Adobe Inc.");
        var premiere = Fixtures.App("Adobe Premiere Pro", "Adobe Inc.");
        var afterEffects = Fixtures.App("Adobe After Effects", "Adobe Inc.");

        var attribution = Fixtures.Evaluate(
            @"C:\Users\User\AppData\Local\Adobe",
            [photoshop, premiere, afterEffects],
            LocationCategory.LocalAppData);

        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.False(attribution.OwnershipEstablished);
        Assert.Empty(attribution.AcceptedOwners);

        // The location is still explained for a human reader: every candidate
        // carries the shared-vendor-directory record.
        Assert.All(
            attribution.Candidates.Where(c => c.Supporting.Any()),
            candidate => Assert.Contains(
                candidate.Evidence,
                e => e.Type == EvidenceType.SharedPublisherDirectory));
    }

    [Fact]
    public void ProductDirectoryInsideSharedVendorNamespace_IsAttributedToTheProduct()
    {
        var photoshop = Fixtures.App("Adobe Photoshop", "Adobe Inc.", @"C:\Program Files\Adobe\Adobe Photoshop");
        var premiere = Fixtures.App("Adobe Premiere Pro", "Adobe Inc.", @"C:\Program Files\Adobe\Adobe Premiere Pro");

        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Adobe\Adobe Photoshop",
            [photoshop, premiere]);

        var owner = Fixtures.Candidate(attribution, "Adobe Photoshop");
        Assert.NotNull(owner);
        Assert.True(owner.Accepted);
        Assert.Equal(Classification.Confirmed, attribution.Classification);
        Assert.True(attribution.OwnershipEstablished);
    }

    // ---- Generic directory names ------------------------------------------

    [Theory]
    [InlineData("Common")]
    [InlineData("Cache")]
    [InlineData("Shared")]
    [InlineData("Packages")]
    [InlineData("app-1.2.3")]
    [InlineData("1.0.0")]
    public void GenericDirectoryName_ProducesNoNameEvidence(string directoryName)
    {
        var app = Fixtures.App(directoryName == "Cache" ? "Cache" : "Contoso Suite", "Contoso");

        var attribution = Fixtures.Evaluate($@"C:\Program Files\Contoso\{directoryName}", [app]);

        Assert.DoesNotContain(
            attribution.Candidates.SelectMany(c => c.Evidence),
            e => e.Type is EvidenceType.ExactDirectoryNameMatch or EvidenceType.NormalizedNameMatch);
    }

    [Fact]
    public void ProductNamedCommon_IsNotClaimedByItsOwnName()
    {
        // The generic-name rule wins over an exact match: many products ship a
        // "Common" folder, so an application literally called Common would
        // otherwise claim every one of them.
        var app = Fixtures.App("Common", "Some Publisher");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\VendorName\Common", [app]);

        Assert.DoesNotContain(
            attribution.Candidates.SelectMany(c => c.Evidence),
            e => e.Type is EvidenceType.ExactDirectoryNameMatch or EvidenceType.NormalizedNameMatch);
    }

    // ---- Multiple candidates ----------------------------------------------

    [Fact]
    public void AmbiguousProductFolder_IsNotHandedToEitherSibling()
    {
        // Two products from one vendor, neither name anchoring to the folder, and
        // no unique evidence. AppTrace declines to choose, which is the outcome the
        // "prefer false negatives" rule asks for; the descent continues instead.
        var first = Fixtures.App("Widget One", "Widgetco");
        var second = Fixtures.App("Widget Two", "Widgetco");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Widgetco\Widget", [first, second]);

        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.Empty(attribution.AcceptedOwners);
        Assert.False(attribution.OwnershipEstablished);
    }

    [Fact]
    public void SeveralAppsDeclaringOneVendorNamespace_AreReportedAsSharingIt()
    {
        // When the folder name is the vendor namespace itself and several products
        // register it, the location is genuinely shared: AppTrace says so instead
        // of picking the first product the registry happened to list.
        var chrome = Fixtures.App("Google Chrome", "Google", @"C:\Program Files\Google");
        var drive = Fixtures.App("Google Drive", "Google", @"C:\Program Files\Google");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Google", [chrome, drive]);

        Assert.Equal(Classification.Shared, attribution.Classification);
        Assert.Equal(2, attribution.AcceptedOwners.Count);
        Assert.False(attribution.OwnershipEstablished);
    }

    [Fact]
    public void OneAppNamedAfterDirectoryIsPreferredOverOtherVendorProducts()
    {
        var target = Fixtures.App("Contoso Reporter", "Contoso", @"C:\Program Files\Contoso\Reporter");
        var sibling = Fixtures.App("Contoso Warehouse", "Contoso", @"C:\Program Files\Contoso\Warehouse");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Contoso\Reporter", [target, sibling]);

        Assert.True(attribution.OwnershipEstablished);
        Assert.Single(attribution.AcceptedOwners);
        Assert.Equal("app-contosoreporter", attribution.AcceptedOwners[0].AppId);
    }

    // ---- Missing registry data -------------------------------------------

    [Fact]
    public void AppWithoutPublisherOrInstallLocation_StillMatchesByExactName()
    {
        var minimal = Fixtures.App("Minimal Tool");

        Assert.Null(minimal.Publisher);
        Assert.Null(minimal.InstallLocation);

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Minimal Tool", [minimal]);

        var owner = Fixtures.Candidate(attribution, "Minimal Tool");
        Assert.NotNull(owner);
        Assert.True(owner.Accepted);
        Assert.Contains(owner.Evidence, e => e.Type == EvidenceType.ExactDirectoryNameMatch);
    }

    [Fact]
    public void EmptyApplicationList_ProducesUnknownWithNoCandidates()
    {
        var attribution = Fixtures.Evaluate(@"C:\Program Files\Whatever", []);

        Assert.Empty(attribution.Candidates);
        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.False(attribution.OwnershipEstablished);
    }

    [Fact]
    public void DirectoryNameShorterThanThreeCharacters_IsNotMatchedByName()
    {
        // Guards against two-letter directory names claiming applications.
        var app = Fixtures.App("AB");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\AB", [app]);

        Assert.DoesNotContain(
            attribution.Candidates.SelectMany(c => c.Evidence),
            e => e.Type is EvidenceType.ExactDirectoryNameMatch or EvidenceType.NormalizedNameMatch);
    }
}
