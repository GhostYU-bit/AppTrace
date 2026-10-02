using AppTrace.Core.Attribution;
using AppTrace.Core.Discovery;
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
    [InlineData("Shared")]
    [InlineData("Resources")]
    [InlineData("app-1.2.3")]
    [InlineData("1.0.0")]
    public void GenericDirectoryName_ProducesNoNameEvidence(string directoryName)
    {
        // "Cache", "Packages", "logs", "temp", "runtime" and "update" were removed
        // from this list in Task 05. They are not meaningless at every depth: as a
        // product-level directory for a product with that name they are legitimate
        // identity, and PathSemantics now decides them by position instead. They are
        // still refused here, because this path is nested inside another product's
        // tree - see PathSemanticsTests for that distinction.
        var app = Fixtures.App("Contoso Suite", "Contoso");

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
    public void UnequalProvenance_DoesNotLetOneProductConsumeACoDeclaredRoot()
    {
        // Both products declare the same vendor root, and one of them has several
        // registration records beneath it. Accumulated provenance must not let it
        // drift outside the tie window and be published as the sole owner of a root
        // that is shared by construction.
        var busy = Fixtures.App("Widget One", "Widgetco", @"C:\Program Files\Widgetco");
        var quiet = Fixtures.App("Widget Two", "Widgetco", @"C:\Program Files\Widgetco");

        var provenance = new ProvenanceIndex(
        [
            Anchor(busy.Id, @"C:\Program Files\Widgetco\Widget One\WidgetOne.exe"),
            Anchor(busy.Id, @"C:\Program Files\Widgetco\Widget One\WidgetOneService.exe"),
            Anchor(busy.Id, @"C:\Program Files\Widgetco\Widget One\WidgetOneHelper.exe"),
        ]);

        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Widgetco",
            [busy, quiet],
            provenance: provenance);

        // Neither product may win the root: the per-product boundary is below it.
        Assert.False(attribution.OwnershipEstablished);
        Assert.Equal(2, attribution.AcceptedOwners.Count);
        Assert.Equal(
            [busy.Id, quiet.Id],
            attribution.AcceptedOwners.Select(o => o.AppId).OrderBy(id => id, StringComparer.Ordinal).ToArray());

        // And the product with the most records must not be published as a
        // confident owner of the shared root.
        var busyOwner = Fixtures.Candidate(attribution, "Widget One");
        Assert.NotNull(busyOwner);
        Assert.True(busyOwner.Accepted);
        Assert.Equal(Classification.Medium, busyOwner.Classification);
    }

    [Fact]
    public void CoDeclaredInstallRootWithoutAVendorName_IsStillShared()
    {
        // The root is not named after any publisher, so the vendor-namespace rule
        // cannot see it. What makes it shared is the registry: two distinct products
        // declare it as their exact install location, so it is a container boundary
        // regardless of what it is called.
        var one = Fixtures.App("Widget One", "Widgetco", @"C:\Program Files\Vault");
        var two = Fixtures.App("Gadget Two", "Gadgetry", @"C:\Program Files\Vault");

        var provenance = new ProvenanceIndex(
        [
            Anchor(one.Id, @"C:\Program Files\Vault\Widget One\WidgetOne.exe"),
        ]);

        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Vault",
            [one, two],
            provenance: provenance);

        Assert.False(attribution.OwnershipEstablished);
        Assert.Equal(2, attribution.AcceptedOwners.Count);

        // The location is AMBIGUOUS, so no product is published as its confident
        // owner even though each one's own registration names it.
        Assert.Equal(Classification.Ambiguous, attribution.Classification);
        Assert.False(attribution.Classification.IsConfident());
    }

    [Fact]
    public void DecisivelyContradictedCandidate_IsNeverPublishedAsAnAcceptedOwner()
    {
        // "Adobe" is a vendor namespace because two Adobe products are installed,
        // and a third-party application has a genuine Windows registration pointing
        // into it. The registration is real provenance, but the publisher
        // contradiction is decisive: no amount of weak support may outvote it, so
        // the candidate must not be published as an accepted owner while its own
        // classification is UNKNOWN. Accepting it was what let a location be
        // reported as unattributed in one view and as owned in another.
        var photoshop = Fixtures.App("Adobe Photoshop", "Adobe Inc.");
        var premiere = Fixtures.App("Adobe Premiere Pro", "Adobe Inc.");
        var zoom = Fixtures.App("Zoom", "Zoom Video Communications");

        var provenance = new ProvenanceIndex(
        [
            new ProvenanceAnchor(
                zoom.Id,
                @"C:\Users\User\AppData\Local\Adobe\Zoom.exe",
                @"c:\users\user\appdata\local\adobe",
                ProvenanceSource.AppPath,
                ExecutableRole.MainApplication,
                @"HKLM\Software\Microsoft\Windows\CurrentVersion\App Paths\Zoom.exe",
                "App Paths registers the executable for this user.")
            {
                IsIndependentlyLinked = true,
            },
        ]);

        var attribution = Fixtures.Evaluate(
            @"C:\Users\User\AppData\Local\Adobe",
            [photoshop, premiere, zoom],
            LocationCategory.LocalAppData,
            provenance: provenance);

        var candidate = Fixtures.Candidate(attribution, "Zoom");
        Assert.NotNull(candidate);
        Assert.Contains(candidate.Evidence, e => e.Type == EvidenceType.PublisherMismatch);
        Assert.False(candidate.Accepted);
        Assert.Equal(Classification.Unknown, candidate.Classification);

        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.Empty(attribution.AcceptedOwners);
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

    // ---- Correlated provenance -------------------------------------------

    [Fact]
    public void RepeatedRegistrationsToTheSameExecutable_ContributeOneBoundedRecord()
    {
        // An App Paths entry and three shortcuts all name the same file. That is one
        // fact observed four times, not four independent reasons to trust it, so it
        // must not become four times the confidence.
        var app = Fixtures.App("Widget One", "Widgetco");

        var provenance = new ProvenanceIndex(
        [
            Anchor(app.Id, @"C:\Program Files\Widgetco\Widget One\WidgetOne.exe"),
            Anchor(app.Id, @"C:\Program Files\Widgetco\Widget One\WidgetOne.exe", ProvenanceSource.Shortcut),
            Anchor(app.Id, @"C:\Program Files\Widgetco\Widget One\WidgetOne.exe", ProvenanceSource.Shortcut),
            Anchor(app.Id, @"C:\Program Files\Widgetco\Widget One\WidgetOne.exe", ProvenanceSource.Shortcut),
        ]);

        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Widgetco\Widget One",
            [app],
            provenance: provenance);

        var owner = Fixtures.Candidate(attribution, "Widget One");
        Assert.NotNull(owner);

        var anchors = owner.Evidence
            .Where(e => e.Type == EvidenceType.ProvenanceAnchorMatch)
            .ToArray();
        var record = Assert.Single(anchors);

        // ...but WHY still names every observed surface.
        Assert.Contains("App Paths", record.Description);
        Assert.Contains("shortcut", record.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x3", record.Description);
    }

    [Fact]
    public void DistinctExecutablesOfOneProduct_RemainSeparateProvenanceFacts()
    {
        // A main executable and a service binary are two different physical targets,
        // so bounding correlated provenance must not collapse them into one.
        var app = Fixtures.App("Widget One", "Widgetco");

        var provenance = new ProvenanceIndex(
        [
            Anchor(app.Id, @"C:\Program Files\Widgetco\Widget One\WidgetOne.exe"),
            Anchor(
                app.Id,
                @"C:\Program Files\Widgetco\Widget One\WidgetOneService.exe",
                ProvenanceSource.Service,
                ExecutableRole.Service),
        ]);

        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Widgetco\Widget One",
            [app],
            provenance: provenance);

        var owner = Fixtures.Candidate(attribution, "Widget One");
        Assert.NotNull(owner);
        Assert.Equal(2, owner.Evidence.Count(e => e.Type == EvidenceType.ProvenanceAnchorMatch));
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

    // ---- Data-root product boundaries (Task 07.6) -------------------------

    [Theory]
    [InlineData(LocationCategory.ProgramData, @"C:\ProgramData\Contoso Reporter")]
    [InlineData(LocationCategory.RoamingAppData, @"C:\Users\User\AppData\Roaming\Contoso Reporter")]
    [InlineData(LocationCategory.LocalLowAppData, @"C:\Users\User\AppData\LocalLow\Contoso Reporter")]
    [InlineData(LocationCategory.LocalAppData, @"C:\Users\User\AppData\Local\Contoso Reporter")]
    public void ProductBoundaryDirectlyUnderADataRoot_ProposesTheInstalledIdentity(
        LocationCategory category,
        string path)
    {
        // The first meaningful directory below a data root is where a product's own
        // data namespace may begin, so a sufficiently specific installed identity may
        // be proposed there.
        var app = Fixtures.App("Contoso Reporter", "Contoso");

        var attribution = Fixtures.Evaluate(path, [app], category);

        var owner = Fixtures.Candidate(attribution, "Contoso Reporter");
        Assert.NotNull(owner);
        Assert.True(owner.Accepted);
        Assert.Contains(owner.Evidence, e => e.Type == EvidenceType.ExactDirectoryNameMatch);

        // The boundary record documents where the proposal is allowed to come from...
        var boundary = Assert.Single(
            owner.Evidence,
            e => e.Type == EvidenceType.DataRootProductBoundary);
        Assert.Equal(EvidenceKind.Structure, boundary.Kind);
        Assert.Equal(0, boundary.Weight);

        // ...and being structure worth nothing, it cannot raise the claim by itself.
        Assert.True(owner.Score < 45 || owner.Evidence.Any(e => e.Kind == EvidenceKind.Identity));
    }

    [Fact]
    public void VendorThenProductBoundary_ProposesTheProductAndNotTheVendorDirectory()
    {
        var app = Fixtures.App("DaVinci Resolve", "Blackmagic Design");

        var product = Fixtures.Evaluate(
            @"C:\Users\User\AppData\Local\Blackmagic Design\DaVinci Resolve",
            [app],
            LocationCategory.LocalAppData);

        var owner = Fixtures.Candidate(product, "DaVinci Resolve");
        Assert.NotNull(owner);
        Assert.True(owner.Accepted);
        Assert.Contains(owner.Evidence, e => e.Type == EvidenceType.DataRootProductBoundary);

        // The product boundary proposes the candidate and lets the existing ladder
        // decide: a name alone stays MEDIUM, exactly as it does anywhere else.
        Assert.Equal(Classification.Medium, product.Classification);

        // The vendor segment above it scopes the search; it must not be handed to the
        // product merely because the product lives below it.
        var vendor = Fixtures.Evaluate(
            @"C:\Users\User\AppData\Local\Blackmagic Design",
            [app],
            LocationCategory.LocalAppData);

        Assert.Empty(vendor.Candidates);
        Assert.False(vendor.OwnershipEstablished);
        Assert.False(vendor.Classification.IsConfident());
    }

    [Fact]
    public void VendorDirectoryUnderADataRoot_IsNotExclusivelyOwnedByASingleProduct()
    {
        // Two products share the same publisher namespace. Neither of them may take
        // the vendor directory itself, however specific their own names are: the
        // per-product boundary lives below it.
        var one = Fixtures.App("Widgetco One", "Widgetco");
        var two = Fixtures.App("Widgetco Two", "Widgetco");

        var attribution = Fixtures.Evaluate(
            @"C:\ProgramData\Widgetco",
            [one, two],
            LocationCategory.ProgramData);

        Assert.False(attribution.OwnershipEstablished);
        Assert.Empty(attribution.AcceptedOwners);
        Assert.Equal(Classification.Unknown, attribution.Classification);
    }

    [Fact]
    public void ProductLikeNameDeeperThanAProductBoundary_IsNotProposed()
    {
        // The boundary is positional. Two levels below a data root is the product
        // boundary; three levels is the product's own content, and a product name
        // there is a coincidence rather than a namespace of its own.
        var app = Fixtures.App("Contoso Reporter", "Contoso");

        var attribution = Fixtures.Evaluate(
            @"C:\Users\User\AppData\Local\Contoso Suite\Shared\Contoso Reporter",
            [app],
            LocationCategory.LocalAppData);

        Assert.DoesNotContain(
            attribution.Candidates.SelectMany(c => c.Evidence),
            e => e.Type == EvidenceType.DataRootProductBoundary);
    }

    [Theory]
    [InlineData(@"C:\Users\User\AppData\Local\Contoso\node_modules\Contoso Reporter")]
    [InlineData(@"C:\Users\User\AppData\Local\npm-cache\Contoso Reporter")]
    [InlineData(@"C:\Users\User\AppData\Local\Contoso\logs\Contoso Reporter")]
    [InlineData(@"C:\Users\User\AppData\Local\Contoso\runtime\Contoso Reporter")]
    [InlineData(@"C:\Users\User\AppData\Local\Contoso\QtQuick\Contoso Reporter")]
    public void ProductLikeNameBeneathAStructuralAnchor_GainsNothingFromTheDataRoot(string path)
    {
        // Task 05's structural protections stay authoritative inside application data.
        // Sitting under AppData or ProgramData must not supply the product-boundary
        // privilege that a genuine product boundary has.
        var app = Fixtures.App("Contoso Reporter", "Contoso");

        var attribution = Fixtures.Evaluate(path, [app], LocationCategory.LocalAppData);

        Assert.DoesNotContain(
            attribution.Candidates.SelectMany(c => c.Evidence),
            e => e.Type is EvidenceType.ExactDirectoryNameMatch
                or EvidenceType.NormalizedNameMatch
                or EvidenceType.DataRootProductBoundary);
    }

    // ---- Single-product vendor namespaces (Task 07.7) ---------------------

    [Fact]
    public void VendorNamespaceWithOneInstalledProduct_IsNotOwnedByThatProduct()
    {
        // The field case this task exists for: only one product from the publisher is
        // installed, and it declares a boundary below the vendor directory. The
        // vendor directory is a namespace, not the product's root, so nothing may be
        // established there and the scan has to descend.
        var app = Fixtures.App("ProductA", "Vendorco", @"C:\Program Files\Vendor\ProductA");

        var provenance = new ProvenanceIndex(
        [
            Anchor(app.Id, @"C:\Program Files\Vendor\ProductA\ProductA.exe"),
        ]);

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Vendor", [app], provenance: provenance);

        Assert.False(attribution.OwnershipEstablished);
        Assert.Contains("single-product vendor namespace", attribution.StopReason, StringComparison.Ordinal);

        var owner = Fixtures.Candidate(attribution, "ProductA");
        Assert.NotNull(owner);
        Assert.True(owner.Accepted);
        Assert.NotEqual(Classification.Confirmed, owner.Classification);
        Assert.DoesNotContain(owner.Evidence, e => e.Type == EvidenceType.DeclaredInstallLocation);
    }

    [Fact]
    public void VendorNamespaceSemantics_DoNotDependOnASiblingProductBeingInstalled()
    {
        // The central acceptance criterion: identical evidence for Product A must
        // produce the same boundary whether or not Product B from the same publisher
        // happens to be installed. Only the number of plausible owners may differ.
        var a = Fixtures.App("ProductA", "Contoso Ltd.", @"C:\Program Files\Vendor\ProductA");
        var b = Fixtures.App("ProductB", "Contoso Ltd.", @"C:\Program Files\Vendor\ProductB");

        var onlyA = Fixtures.Evaluate(
            @"C:\Program Files\Vendor",
            [a],
            provenance: new ProvenanceIndex(
            [
                Anchor(a.Id, @"C:\Program Files\Vendor\ProductA\ProductA.exe"),
            ]));

        var both = Fixtures.Evaluate(
            @"C:\Program Files\Vendor",
            [a, b],
            provenance: new ProvenanceIndex(
            [
                Anchor(a.Id, @"C:\Program Files\Vendor\ProductA\ProductA.exe"),
                Anchor(b.Id, @"C:\Program Files\Vendor\ProductB\ProductB.exe"),
            ]));

        Assert.False(onlyA.OwnershipEstablished);
        Assert.False(both.OwnershipEstablished);
        Assert.Contains("single-product vendor namespace", onlyA.StopReason, StringComparison.Ordinal);
        Assert.Contains("single-product vendor namespace", both.StopReason, StringComparison.Ordinal);
        Assert.False(onlyA.Classification.IsConfident());
        Assert.False(both.Classification.IsConfident());

        // Product A's own evidence is untouched by Product B's existence.
        Assert.Equal(
            Fixtures.Candidate(onlyA, "ProductA")!.Evidence.Count(e => e.SupportsAttribution),
            Fixtures.Candidate(both, "ProductA")!.Evidence.Count(e => e.SupportsAttribution));
    }

    [Fact]
    public void ProductWhoseOwnIdentityIsItsDirectory_KeepsNormalOwnership()
    {
        // A genuine product root protected two ways: the directory names the product
        // itself, and the product registers this exact directory as its install
        // location. Having files in a subdirectory does not turn it into a namespace.
        var app = Fixtures.App("ProductA", "Vendorco", @"C:\Program Files\ProductA");

        var provenance = new ProvenanceIndex(
        [
            Anchor(app.Id, @"C:\Program Files\ProductA\Secondary\ProductA.exe"),
        ]);

        var attribution = Fixtures.Evaluate(@"C:\Program Files\ProductA", [app], provenance: provenance);

        Assert.True(attribution.OwnershipEstablished);
        Assert.Equal(Classification.Confirmed, attribution.Classification);
    }

    [Fact]
    public void ProductNamedByItsProductCode_KeepsItsRootWhenItsProgramIsOneLevelDown()
    {
        // The counterpart to the field case above, and the one a real scan found:
        // an application whose display name is not written in the same alphabet as
        // its directory, whose program folder sits inside its own directory. The
        // layout is structurally identical to a vendor namespace — only identity
        // separates them, and here identity is the product code.
        var app = Fixtures.App("产品", "Vendor Co.", productCode: "Doubao");

        var provenance = new ProvenanceIndex(
        [
            Anchor(app.Id, @"C:\Users\User\AppData\Local\Doubao\Application\icon.ico"),
        ]);

        var attribution = Fixtures.Evaluate(
            @"C:\Users\User\AppData\Local\Doubao",
            [app],
            LocationCategory.LocalAppData,
            provenance: provenance);

        Assert.DoesNotContain("vendor namespace", attribution.StopReason, StringComparison.Ordinal);
        Assert.True(attribution.OwnershipEstablished);
    }

    [Fact]
    public void UpdaterAnchorBelowAVendorRoot_DoesNotProveANamespace()
    {
        // An updater is weak infrastructure: it shows the application reaches the
        // path, not that its product boundary is there. Only the product's own
        // executable may make the directory above it a namespace.
        var app = Fixtures.App("ProductA", "Vendorco");

        var updater = Fixtures.Evaluate(
            @"C:\Program Files\Vendor",
            [app],
            provenance: new ProvenanceIndex(
            [
                Anchor(app.Id, @"C:\Program Files\Vendor\ProductA\ProductAUpdater.exe", role: ExecutableRole.Updater),
            ]));

        Assert.DoesNotContain("vendor namespace", updater.StopReason, StringComparison.Ordinal);

        var main = Fixtures.Evaluate(
            @"C:\Program Files\Vendor",
            [app],
            provenance: new ProvenanceIndex(
            [
                Anchor(app.Id, @"C:\Program Files\Vendor\ProductA\ProductA.exe"),
            ]));

        Assert.Contains("single-product vendor namespace", main.StopReason, StringComparison.Ordinal);
    }

    [Fact]
    public void StagingChildBelowAVendorRoot_DoesNotProveANamespace()
    {
        // "Vendor\Updater" and "Vendor\cache" are infrastructure rather than a
        // product boundary. The existing structural vocabulary refuses them, so no
        // word list has to be extended to say so.
        var app = Fixtures.App("ProductA", "Vendorco");

        foreach (var child in new[] { "Updater", "cache", "1.2.3" })
        {
            var attribution = Fixtures.Evaluate(
                @"C:\Program Files\Vendor",
                [app],
                provenance: new ProvenanceIndex(
                [
                    Anchor(app.Id, $@"C:\Program Files\Vendor\{child}\ProductA.exe"),
                ]));

            Assert.DoesNotContain("vendor namespace", attribution.StopReason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GenericContainerName_IsNotTurnedIntoAVendorNamespace()
    {
        // Task 07.7 deliberately leaves "C:\Program Files\Programs -> one product"
        // unresolved. A generic container word names no publisher, so the rule does
        // not apply to it, and no word list may be extended to make it apply. The
        // remaining overclaim is a documented future problem, not a regression.
        var app = Fixtures.App("Generic Host", "Genericco");

        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Programs",
            [app],
            provenance: new ProvenanceIndex(
            [
                Anchor(app.Id, @"C:\Program Files\Programs\Generic Host\GenericHost.exe"),
            ]));

        Assert.DoesNotContain("vendor namespace", attribution.StopReason, StringComparison.Ordinal);
    }

    /// <summary>An independently linked registration of one executable path.</summary>
    private static ProvenanceAnchor Anchor(
        string appId,
        string path,
        ProvenanceSource source = ProvenanceSource.AppPath,
        ExecutableRole role = ExecutableRole.MainApplication)
        => new(
            appId,
            path,
            TextNormalizer.NormalizePath(Path.GetDirectoryName(path)!),
            source,
            role,
            @"HKLM\Software\Microsoft\Windows\CurrentVersion\App Paths",
            "App Paths registers this executable.")
        {
            IsIndependentlyLinked = true,
        };
}
