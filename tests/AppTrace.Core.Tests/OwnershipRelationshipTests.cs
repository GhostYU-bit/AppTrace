using AppTrace.Core.Attribution;
using AppTrace.Core.Model;

namespace AppTrace.Core.Tests;

/// <summary>
/// Task 06: ownership propagation and the Owns / RelatedTo model.
/// </summary>
/// <remarks>
/// <para>The principle under test:</para>
/// <blockquote>A file can belong to one application while being about another.</blockquote>
/// <para>Two questions are kept strictly apart. Task 05 asked <em>should this child
/// name create a new owner candidate?</em> Task 06 asks <em>does the existing
/// ancestor owner continue through this structure?</em> A structure that suppresses
/// a child's name from becoming an owner says nothing about whether the bytes still
/// belong to the parent application.</para>
/// </remarks>
public class OwnershipRelationshipTests
{
    private const string LocalAppData = @"C:\Users\user\AppData\Local";
    private const string OwnerRoot = LocalAppData + @"\OwnerApp";

    private static AppIdentity OwnerApp(string? installLocation = OwnerRoot)
        => Fixtures.App("Owner App", "Owner Labs", installLocation);

    private static AppIdentity OtherProduct(string? installLocation = null)
        => Fixtures.App("Other Product", "Other Ltd", installLocation);

    private static LocationAttribution Descendant(string relativePath, params AppIdentity[] apps)
        => Fixtures.EvaluateDescendant(
            OwnerRoot + @"\" + relativePath,
            apps,
            OwnerRoot,
            "app-ownerapp");

    // ---------------------------------------------------------------------
    // Ancestor ownership propagation
    // ---------------------------------------------------------------------

    [Fact]
    public void EstablishedOwnershipPropagatesThroughOrdinaryDescendants()
    {
        // Vivaldi\User Data\Default\Cache is still Vivaldi's footprint. None of those
        // names says "Vivaldi", so without propagation the tree would be lost.
        var owner = OwnerApp();

        foreach (var relative in new[] { "User Data", @"User Data\Default", @"User Data\Default\Cache" })
        {
            var attribution = Descendant(relative, owner);

            var accepted = attribution.AcceptedOwners.SingleOrDefault();
            Assert.NotNull(accepted);
            Assert.Equal("app-ownerapp", accepted.AppId);
            Assert.Contains(accepted.Evidence, e => e.Type == EvidenceType.InheritedFromOwner);
            Assert.True(attribution.OwnershipEstablished);
        }
    }

    [Fact]
    public void StructuralDescendantDoesNotStealOwnershipFromItsParent()
    {
        // ProductRoot\logs\helper: "helper" must not become ASUS Update Helper, and
        // the logs structure must not detach the bytes from the product.
        var owner = OwnerApp();
        var asusHelper = Fixtures.App("ASUS Update Helper", "ASUSTeK Computer Inc.");

        var attribution = Descendant(@"logs\helper", owner, asusHelper);

        var accepted = attribution.AcceptedOwners.SingleOrDefault();
        Assert.NotNull(accepted);
        Assert.Equal("app-ownerapp", accepted.AppId);
        Assert.DoesNotContain(
            attribution.Candidates,
            c => c.AppId == "app-asusupdatehelper" && c.Accepted);
    }

    [Fact]
    public void DependencyTreeDescendantDoesNotBecomeNodeJs()
    {
        // ProductRoot\node_modules\@types\node. The product may keep the bytes;
        // Node.js must not become owner or related merely from the leaf "node".
        var owner = OwnerApp();
        var node = Fixtures.App("Node.js Krypton via nvm-windows", "OpenJS Foundation");

        var attribution = Descendant(@"node_modules\@types\node", owner, node);

        Assert.Equal("app-ownerapp", attribution.AcceptedOwners.Single().AppId);
        Assert.DoesNotContain(attribution.Candidates, c => c.AppId == "app-nodejs");
    }

    [Fact]
    public void InheritedOwnershipIsWeakerThanTheDirectorysOwnEvidence()
    {
        // Inheritance proposes; it does not override. The record must be weak enough
        // that anything the directory says on its own behalf outranks it.
        var owner = OwnerApp();
        var attribution = Descendant("User Data", owner);

        var inherited = attribution.AcceptedOwners.Single()
            .Evidence.Single(e => e.Type == EvidenceType.InheritedFromOwner);

        Assert.Equal(EvidenceKind.Provenance, attribution.AcceptedOwners.Single()
            .Evidence.Single(e => e.Type == EvidenceType.InheritedFromOwner).Kind);
        Assert.True(
            inherited.Weight < EvidenceWeights.WeightOf(EvidenceType.InstallLocationMatch),
            "Inherited ownership must be weaker than direct provenance.");
    }

    // ---------------------------------------------------------------------
    // Boundaries
    // ---------------------------------------------------------------------

    [Fact]
    public void ADescendantWithItsOwnRegistrationIsABoundary()
    {
        // Independent decisive provenance at the descendant wins outright.
        var owner = OwnerApp();
        var other = OtherProduct(OwnerRoot + @"\Other Product");

        var attribution = Descendant("Other Product", owner, other);

        var accepted = attribution.AcceptedOwners.Single();
        Assert.Equal("app-otherproduct", accepted.AppId);
        Assert.Equal(Classification.Confirmed, accepted.Classification);
        Assert.DoesNotContain(
            attribution.Candidates,
            c => c.AppId == "app-ownerapp" && c.Accepted);
    }

    [Fact]
    public void AVendorNamespaceIsABoundaryToInheritance()
    {
        // Ownership of a product root must not extend into a vendor namespace it
        // happens to contain: the products below it are separately owned.
        var owner = OwnerApp();
        var sibling = Fixtures.App("Vendor Tool One", "SomeVendor", @"C:\Program Files\SomeVendor\Tool One");
        var sister = Fixtures.App("Vendor Tool Two", "SomeVendor", @"C:\Program Files\SomeVendor\Tool Two");

        var attribution = Descendant("SomeVendor", owner, sibling, sister);

        Assert.DoesNotContain(
            attribution.Candidates,
            c => c.AppId == "app-ownerapp" && c.Accepted);
    }

    [Fact]
    public void AStructuralStoreIsNotABoundaryToInheritance()
    {
        // The counterpart of the previous two: recognition of a cache or a runtime
        // is not a reason to drop the parent's ownership. Task 05's semantics decide
        // whether a child may claim identity; they do not decide whether the parent
        // still owns the bytes.
        var owner = OwnerApp();

        foreach (var relative in new[] { "GPUCache", "runtime", @"User Data\Default\Cache", "logs", "Temp" })
        {
            var attribution = Descendant(relative, owner);
            Assert.Equal("app-ownerapp", attribution.AcceptedOwners.Single().AppId);
        }
    }

    // ---------------------------------------------------------------------
    // RelatedTo
    // ---------------------------------------------------------------------

    [Fact]
    public void SubjectDataNamesAProductWithoutGivingItOwnership()
    {
        // The NVIDIA shape, generically: an owned tree contains a store whose
        // entries refer to other products.
        var owner = OwnerApp();
        var other = OtherProduct();

        var attribution = Descendant(@"Recommendations\Other_Product", owner, other);

        var accepted = attribution.AcceptedOwners.Single();
        Assert.Equal("app-ownerapp", accepted.AppId);

        var related = attribution.RelatedApplications.Single();
        Assert.Equal("app-otherproduct", related.AppId);
        Assert.Equal(CandidateRelation.RelatedTo, related.Relation);
        Assert.False(related.Accepted);
    }

    [Fact]
    public void ARelatedApplicationReceivesNoBytesAndNoClassification()
    {
        var owner = OwnerApp();
        var other = OtherProduct();

        var attribution = Descendant(@"Recommendations\Other_Product", owner, other);
        var related = attribution.RelatedApplications.Single();

        Assert.False(related.Accepted);
        Assert.Equal(Classification.Unknown, related.Classification);
        Assert.Empty(related.Evidence);
        Assert.Equal(0, related.Score);

        // And it never appears as an owner anywhere.
        Assert.DoesNotContain(attribution.AcceptedOwners, o => o.AppId == "app-otherproduct");
    }

    [Fact]
    public void RelationshipEvidenceIsRelationshipEvidence()
    {
        var attribution = Descendant(@"Recommendations\Other_Product", OwnerApp(), OtherProduct());
        var related = attribution.RelatedApplications.Single();

        var evidence = related.RelationshipEvidence.Single();
        Assert.Equal(EvidenceType.SubjectNameMatch, evidence.Type);
        Assert.Equal(EvidenceKind.Relationship, evidence.Kind);
        Assert.Equal(0, evidence.Weight);
        Assert.False(evidence.SupportsAttribution);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("sdk")]
    [InlineData("helper")]
    [InlineData("tool")]
    [InlineData("zip")]
    public void GenericVocabularyDoesNotBecomeARelationship(string leaf)
    {
        // The scope guard. These words appear in installed product names, but a name
        // coincidence in an owned tree is not a relationship. Either the word is not
        // specific enough for the product, or its specificity is too low.
        var owner = OwnerApp();
        var node = Fixtures.App("Node.js Krypton via nvm-windows", "OpenJS Foundation");
        var sdk = Fixtures.App("ASUS Aura SDK", "ASUSTeK COMPUTER INC.");
        var helper = Fixtures.App("ASUS Update Helper", "ASUSTeK Computer Inc.");
        var epson = Fixtures.App("Epson Printer Driver Security Support Tool", "Seiko Epson Corporation");
        var zip = Fixtures.App("360 Zip", "360 Security Center", @"C:\Program Files (x86)\360\360zip");

        var attribution = Descendant(@"Recommendations\" + leaf, owner, node, sdk, helper, epson, zip);

        Assert.Equal("app-ownerapp", attribution.AcceptedOwners.Single().AppId);
        Assert.Empty(attribution.RelatedApplications);
    }

    [Fact]
    public void ASubjectStoreIsFoundThroughAnAnonymousIntermediate()
    {
        // Stores routinely put a content hash between the store and its entries:
        // Recommendations\<hash>\cities_skylines. That is the real NVIDIA shape, and
        // stopping at the immediate parent would miss exactly the case this exists
        // for.
        const string hash = "f00b33d0e90b878f9724f9217d712c4dcebee196ae129f6ac2a631aef0787cae";

        var attribution = Descendant(
            @"Recommendations\" + hash + @"\Other_Product",
            OwnerApp(),
            OtherProduct());

        Assert.Equal("app-ownerapp", attribution.AcceptedOwners.Single().AppId);
        Assert.Equal("app-otherproduct", attribution.RelatedApplications.Single().AppId);
    }

    [Fact]
    public void AnAnonymousIntermediaryIsNotItselfASubjectStore()
    {
        // The walking must not become unbounded: a hash directly inside an ordinary
        // owned directory is not a subject store.
        const string hash = "f00b33d0e90b878f9724f9217d712c4dcebee196ae129f6ac2a631aef0787cae";

        var attribution = Descendant(@"Backups\" + hash + @"\Other_Product", OwnerApp(), OtherProduct());

        Assert.Equal("app-ownerapp", attribution.AcceptedOwners.Single().AppId);
        Assert.Empty(attribution.RelatedApplications);
    }

    [Fact]
    public void RelationshipRequiresASubjectDataParent()
    {
        // A specific product name inside an ordinary owned directory is not a
        // relationship. Without this restriction every mention of a product anywhere
        // in a tree would become one.
        var owner = OwnerApp();
        var other = OtherProduct();

        var attribution = Descendant(@"Backups\Other_Product", owner, other);

        Assert.Equal("app-ownerapp", attribution.AcceptedOwners.Single().AppId);
        Assert.Empty(attribution.RelatedApplications);
    }

    [Fact]
    public void ASpecificNameWithoutASubjectParentStillOwnsNothing()
    {
        // The inverse safety property: refusing to make it a relationship must not
        // quietly make it an owner either.
        var owner = OwnerApp();
        var other = OtherProduct();

        var attribution = Descendant(@"Backups\Other_Product", owner, other);

        Assert.DoesNotContain(attribution.AcceptedOwners, o => o.AppId == "app-otherproduct");
        Assert.DoesNotContain(attribution.RelatedApplications, r => r.AppId == "app-otherproduct");
    }

    [Fact]
    public void TheOwnerOfADirectoryIsNeverRelatedToIt()
    {
        var owner = OwnerApp();

        var attribution = Descendant(@"Recommendations\Owner_App", owner);

        Assert.Equal("app-ownerapp", attribution.AcceptedOwners.Single().AppId);
        Assert.DoesNotContain(attribution.RelatedApplications, r => r.AppId == "app-ownerapp");
    }

    [Fact]
    public void ASequelInsideSubjectDataIsNotARelationship()
    {
        // The near-miss rule holds for relationships too.
        var owner = OwnerApp();
        var cities = Fixtures.App("Cities: Skylines", "Colossal Order");

        var attribution = Descendant(@"Recommendations\Cities Skylines II", owner, cities);

        Assert.Equal("app-ownerapp", attribution.AcceptedOwners.Single().AppId);
        Assert.Empty(attribution.RelatedApplications);
    }

    // ---------------------------------------------------------------------
    // No relationship without ownership
    // ---------------------------------------------------------------------

    [Fact]
    public void AnUnownedPathDoesNotBecomeARelationship()
    {
        // If AppTrace cannot say who owns the path, it must not manufacture a
        // relationship as a substitute. UNKNOWN stays valid.
        var other = OtherProduct();

        var attribution = Fixtures.Evaluate(
            LocalAppData + @"\NobodyOwnsThis\Recommendations\Other_Product",
            [other],
            LocationCategory.LocalAppData,
            ownedAncestors: []);

        Assert.Empty(attribution.AcceptedOwners);
        Assert.Empty(attribution.RelatedApplications);
        Assert.Equal(Classification.Unknown, attribution.Classification);
    }

    // ---------------------------------------------------------------------
    // Shared and ambiguous keep their meaning
    // ---------------------------------------------------------------------

    [Fact]
    public void SharedOwnershipIsNotConvertedIntoRelatedness()
    {
        // A shared vendor namespace is co-ownership, not subject association.
        var chrome = Fixtures.App("Google Chrome", "Google LLC", @"C:\Program Files\Google");
        var drive = Fixtures.App("Google Drive", "Google LLC", @"C:\Program Files\Google");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Google", [chrome, drive]);

        Assert.Equal(2, attribution.AcceptedOwners.Count);
        Assert.Empty(attribution.RelatedApplications);
        Assert.Equal(Classification.Shared, attribution.Classification);
    }

    [Fact]
    public void RelatednessIsNotAppliedToAnUnownedVendorRoot()
    {
        // The vendor root has candidates but no accepted owner, so there is nothing
        // for a relationship to be relative to.
        var chrome = Fixtures.App("Google Chrome", "Google LLC");
        var drive = Fixtures.App("Google Drive", "Google LLC");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Google", [chrome, drive]);

        Assert.Empty(attribution.RelatedApplications);
    }

    // ---------------------------------------------------------------------
    // The model itself
    // ---------------------------------------------------------------------

    [Fact]
    public void OnlyTwoRelationsExist()
    {
        // The scope guard, executable. GeneratedFor, CacheOf, DependencyOf, UsedBy,
        // InstalledBy, ProducedBy and Contains are deliberately absent: the evidence
        // cannot yet tell them apart, and a relation the evidence cannot support is a
        // claim AppTrace must not make.
        Assert.Equal(["Owns", "RelatedTo"], Enum.GetNames<CandidateRelation>());
    }

    [Fact]
    public void OwnershipIsTheDefaultRelation()
    {
        var candidate = new CandidateOwner { AppId = "x" };

        Assert.Equal(CandidateRelation.Owns, candidate.Relation);
        Assert.True(candidate.Owns);
    }
}
