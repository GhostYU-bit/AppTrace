using AppTrace.Core.Attribution;
using AppTrace.Core.Model;
using AppTrace.Core.Tests.Evaluation;

namespace AppTrace.Core.Tests;

/// <summary>
/// Task 05: path semantics and candidate generation.
/// </summary>
/// <remarks>
/// <para>The principle under test:</para>
/// <blockquote>A word in a path is not an application identity until the structure
/// around it gives that word the right to mean one.</blockquote>
/// <para><b>Similarity validates candidates; it does not create them.</b> The
/// negatives below are the point of the task: each one is a path that must not
/// produce an owner at all, not merely one that must not be confident about it.</para>
/// <para>Deliberately absent from every rule exercised here is a vocabulary
/// blacklist. <c>sdk</c>, <c>helper</c>, <c>node</c>, <c>tool</c>,
/// <c>universal</c> and <c>zip</c> are not banned words anywhere in the engine;
/// those paths are rejected because the structure they sit in, and the specificity
/// of the match, do not license an identity claim.</para>
/// </remarks>
public class PathSemanticsTests
{
    private const string LocalAppData = @"C:\Users\user\AppData\Local";

    private static readonly CorpusDocument Corpus = AttributionCorpus.Load();

    private static LocationAttribution Run(string path, params string[] appIds)
        => AttributionEvaluator
            .Evaluate(path, Corpus.Apps, appIds, nameof(LocationCategory.LocalAppData), [])
            .Raw;

    private static AppIdentity App(string displayName, string? publisher = null, string? installLocation = null)
        => Fixtures.App(displayName, publisher, installLocation);

    // ---------------------------------------------------------------------
    // Negative cases: no owner may be generated at all
    // ---------------------------------------------------------------------

    [Fact]
    public void DependencyTreeLeafDoesNotOwnTheDirectory()
    {
        // node_modules\@types\node. "node" is one word of a five-word product name,
        // and it sits inside a dependency tree. Neither the specificity nor the
        // structure licenses an identity claim.
        var node = Corpus.Apps.First(a => a.Id == "app-nodejs");

        var attribution = Run(
            @"C:\Users\user\AppData\Local\npm-cache\_npx\0f94ee\build\node_modules\@types\node",
            "app-nodejs");

        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.Empty(attribution.AcceptedOwners);
        Assert.DoesNotContain(attribution.Candidates, c => c.AppId == node.Id && c.Accepted);
    }

    [Fact]
    public void PackageManagerCacheDoesNotManufactureAnOwnerFromAPackageName()
    {
        // npm-cache\...\@anthropic-ai\sdk must not become ASUS Aura SDK.
        var attribution = Run(
            @"C:\Users\user\AppData\Local\npm-cache\_npx\1e7f6d9597241db0\node_modules\@anthropic-ai\sdk",
            "app-asusaurasdk");

        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.Empty(attribution.AcceptedOwners);
    }

    [Fact]
    public void RuntimeComponentLeafDoesNotOwnTheEnclosingProductTree()
    {
        // Doubao\User Data\sandbox_runtime\...\node must not become Node.js.
        var attribution = Run(
            @"C:\Users\user\AppData\Local\Doubao\User Data\sandbox_runtime\bases\c98c5042338ed152\node",
            "app-nodejs", "app-doubao");

        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.Empty(attribution.AcceptedOwners);
    }

    [Fact]
    public void HelperComponentInsideALogsTreeDoesNotOwnIt()
    {
        // Amazon Web Services\Amazon WorkSpaces\logs\helper must not become
        // ASUS Update Helper.
        var attribution = Run(
            @"C:\Users\user\AppData\Local\Amazon Web Services\Amazon WorkSpaces\logs\helper",
            "app-asusupdatehelper", "app-amazonworkspaces");

        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.Empty(attribution.AcceptedOwners);
    }

    [Fact]
    public void UiToolkitComponentDoesNotOwnIt()
    {
        // JianyingPro\...\QtQuick\Controls\Universal must not become the installed
        // Universal Holtek product.
        var attribution = Run(
            @"C:\Users\user\AppData\Local\JianyingPro\Apps\6.6.0.12145\QtQuick\Controls\Universal",
            "app-universalholtek");

        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.Empty(attribution.AcceptedOwners);
    }

    [Fact]
    public void ArchiveLibraryNameDoesNotOwnAGeneratedTree()
    {
        // node_modules\adm-zip must not become 360 Zip.
        var attribution = Run(
            @"C:\Users\user\AppData\Local\Programs\Contoso Desktop\resources\app\node_modules\adm-zip",
            "app-360zip", "app-dshdesktop");

        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.Empty(attribution.AcceptedOwners);
    }

    [Fact]
    public void RecommendationDataDoesNotIndependentlyEstablishTheSubjectsOwnership()
    {
        // NVIDIA app\NvBackend\Recommendations\cities_skylines. The leaf names the
        // SUBJECT of a stored recommendation, not the writer. Expressing "owned by
        // NVIDIA App, related to Cities: Skylines" is Task 06's model, so the correct
        // Task 05 behaviour is to make no ownership claim here.
        var attribution = Run(
            @"C:\Users\user\AppData\Local\NVIDIA Corporation\NVIDIA app\NvBackend\Recommendations\cities_skylines",
            "app-citiesskylines", "app-nvidiaapp");

        Assert.DoesNotContain(attribution.Candidates, c => c.AppId == "app-citiesskylines" && c.Accepted);
        Assert.Equal(Classification.Unknown, attribution.Classification);
    }

    [Fact]
    public void ASequelDoesNotBecomeItsPredecessor()
    {
        // "Cities Skylines II" must not become "Cities: Skylines" merely because the
        // correct product is absent. A missing candidate means UNKNOWN, not the
        // nearest installed application.
        var attribution = Run(
            @"C:\Users\user\AppData\LocalLow\Colossal Order\Cities Skylines II",
            "app-citiesskylines");

        var cities = attribution.Candidates.FirstOrDefault(c => c.AppId == "app-citiesskylines");
        Assert.False(cities is { Accepted: true }, "A sequel must not be attributed to the earlier game.");
        Assert.Equal(Classification.Unknown, attribution.Classification);
    }

    // ---------------------------------------------------------------------
    // Positive cases: legitimate product paths must still work
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Users\user\AppData\Local\Vivaldi", "app-vivaldi", "app-vivaldi")]
    [InlineData(@"C:\Users\user\AppData\Local\Google\Chrome", "app-googlechrome", "app-googlechrome")]
    [InlineData(@"C:\Users\user\AppData\Local\Microsoft\Edge", "app-msedge", "app-msedge")]
    public void ProductLevelPathsStillGenerateTheCorrectOwner(string path, string appId, string expectedAppId)
    {
        var attribution = Run(path, appId);

        var owner = attribution.AcceptedOwners.SingleOrDefault();
        Assert.NotNull(owner);
        Assert.Equal(expectedAppId, owner.AppId);
    }

    [Fact]
    public void AProductLegitimatelyNamedCacheSurvivesStructuralVocabulary()
    {
        // The inverse guarantee. "Cache" is structural vocabulary in the middle of
        // another product's tree, but it is a perfectly good product name at product
        // level. Structure must not become an absolute forbidden-name list.
        var attribution = AttributionEvaluator
            .Evaluate(@"C:\Program Files\Cache", Corpus.Apps, ["app-cacheapp"], nameof(LocationCategory.ProgramFiles), [])
            .Raw;

        var owner = attribution.AcceptedOwners.SingleOrDefault();
        Assert.NotNull(owner);
        Assert.Equal("app-cacheapp", owner.AppId);
    }

    [Fact]
    public void StructuralVocabularyNamesAncestorsNotTheLeaf()
    {
        // The two readings of the same word, side by side. "Cache" as a product
        // directory is identity; "Cache" beneath another product's tree is structure.
        var product = PathSemantics.Analyse(@"C:\Program Files\Cache", @"c:\program files", 1);
        Assert.False(PathSemantics.SuppressesIdentityForLeaf(product));

        var nested = PathSemantics.Analyse(
            @"C:\Users\user\AppData\Local\Vendor\Product\Cache\data", @"c:\users\user\appdata\local", 4);
        Assert.True(PathSemantics.SuppressesIdentityForLeaf(nested));
    }

    // ---------------------------------------------------------------------
    // Structure kinds
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("node_modules", StructureKind.PackageDependencyTree)]
    [InlineData("npm-cache", StructureKind.PackageManagerCache)]
    [InlineData("sandbox_runtime", StructureKind.ApplicationRuntime)]
    [InlineData("QtQuick", StructureKind.ComponentFramework)]
    [InlineData("User Data", StructureKind.ApplicationProfile)]
    [InlineData("GPUCache", StructureKind.ApplicationCache)]
    [InlineData("logs", StructureKind.Logs)]
    [InlineData("Temp", StructureKind.Temporary)]
    public void KnownStorageStructuresAreRecognised(string segment, StructureKind expected)
    {
        var semantics = PathSemantics.Analyse(
            @"C:\Users\user\AppData\Local\Vendor\Product\" + segment + @"\leaf",
            @"c:\users\user\appdata\local",
            4);

        // Segments are stored exactly as they appear in the normalised path.
        Assert.Contains(semantics, s => s.Kind == expected);
        Assert.Contains(semantics, s => s.Kind == expected && s.SuppressesIdentity);
    }

    [Theory]
    [InlineData("sdk")]
    [InlineData("helper")]
    [InlineData("tool")]
    [InlineData("universal")]
    [InlineData("client")]
    [InlineData("service")]
    [InlineData("manager")]
    public void AmbiguousVocabularyIsNotDeclaredStructural(string segment)
    {
        // The scope guard, as a test. These words are ambiguous rather than
        // structural: their meaning comes from context and independent evidence. If
        // someone "fixes" a future false positive by adding one of them here, this
        // test fails and the reviewer has to justify it.
        var semantics = PathSemantics.Analyse(
            @"C:\Users\user\AppData\Local\Vendor\Product\" + segment,
            @"c:\users\user\appdata\local",
            3);

        // Only the structural claim matters here. Whether identity is suppressed at
        // this depth is a separate, positional decision.
        Assert.Equal(StructureKind.Unknown, PathSemantics.StructureForLeaf(semantics));
    }

    [Fact]
    public void NoGenericTokenBlacklistExists()
    {
        // Task 05's hard scope rule, made executable. A "known generic directory
        // names" list may hold only names that carry no identity information
        // whatsoever; it must not have grown into a blacklist of the words that
        // appeared in the false positives.
        var forbidden = new[] { "sdk", "helper", "node", "tool", "universal", "zip", "adm-zip", "cities_skylines", "github" };

        foreach (var word in forbidden)
        {
            Assert.False(GenericDirectoryNames.IsGeneric(word), $"'{word}' must not be treated as a generic directory name.");
            Assert.Equal(
                StructureKind.Unknown,
                PathSemantics.StructureForLeaf(PathSemantics.Analyse(
                    @"C:\Users\user\AppData\Local\Vendor\Product\" + word,
                    @"c:\users\user\appdata\local",
                    3)));
        }
    }

    // ---------------------------------------------------------------------
    // Candidate generation
    // ---------------------------------------------------------------------

    [Fact]
    public void CandidateGenerationIsIndexedRatherThanExhaustive()
    {
        // A name that relates to nothing in the catalogue produces no candidates at
        // all, rather than one candidate per installed application.
        var attribution = Run(@"C:\Users\user\AppData\Local\Zzqquuxx", "app-vivaldi", "app-googlechrome", "app-msedge");

        Assert.Empty(attribution.Candidates);
    }

    [Fact]
    public void CandidateGenerationStaysSmallForADeepStructuralLeaf()
    {
        // The candidate set is bounded by the identity indexes, not by the size of
        // the installed-application catalogue.
        var attribution = Run(
            @"C:\Users\user\AppData\Local\npm-cache\_npx\1e7f6d95\node_modules\@anthropic-ai\sdk",
            "app-asusaurasdk", "app-nodejs", "app-vivaldi", "app-googlechrome", "app-msedge", "app-360zip");

        Assert.True(
            attribution.Candidates.Count <= 2,
            $"Expected an indexed candidate set, got {attribution.Candidates.Count} candidates.");
    }

    [Fact]
    public void ABriefButPreciseNameStillGeneratesItsCandidate()
    {
        // Specificity must not be a blanket "short names are rejected" rule. A
        // one-word directory that accounts for the whole of a one-word product name
        // is exact identity.
        var attribution = Run(@"C:\Users\user\AppData\Local\Vivaldi", "app-vivaldi");

        var owner = attribution.AcceptedOwners.SingleOrDefault();
        Assert.NotNull(owner);
        Assert.Contains(owner.Evidence, e => e.Type == EvidenceType.ExactDirectoryNameMatch);
    }

    // ---------------------------------------------------------------------
    // Provenance still overrides structure
    // ---------------------------------------------------------------------

    [Fact]
    public void AnExplicitRegistrationBeatsStructuralVocabary()
    {
        // Structure may observe that a directory "looks like a cache", but it must
        // never override the application's own registration of the exact directory.
        var app = App("Registered Runtime", "Vendor Ltd.", @"C:\Program Files\Vendor\Registered Runtime");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Vendor\Registered Runtime", [app]);

        Assert.Equal(Classification.Confirmed, attribution.Classification);
    }

    [Fact]
    public void StructureDoesNotSuppressADeclaredInstallLocation()
    {
        // A product whose registered install location sits under structural
        // vocabulary keeps its claim: the registration is independent evidence, and
        // suppression only ever withholds *name* evidence.
        var app = App("Sandbox Tool", "Vendor Ltd.", @"C:\Program Files\Vendor\sandbox_runtime");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Vendor\sandbox_runtime", [app]);

        Assert.Equal(Classification.Confirmed, attribution.Classification);
        Assert.Single(attribution.AcceptedOwners);
    }

    // ---------------------------------------------------------------------
    // Vendor namespaces
    // ---------------------------------------------------------------------

    [Fact]
    public void ASharedVendorNamespaceStaysShared()
    {
        var chrome = App("Google Chrome", "Google LLC", @"C:\Program Files\Google");
        var drive = App("Google Drive", "Google LLC", @"C:\Program Files\Google");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Google", [chrome, drive]);

        Assert.Equal(2, attribution.AcceptedOwners.Count);
        Assert.Empty(Fixtures.Candidate(attribution, "Google Chrome")!.Contradicting);
    }

    [Fact]
    public void AVendorRootIsNotAProductRoot()
    {
        var chrome = App("Google Chrome", "Google LLC");
        var drive = App("Google Drive", "Google LLC");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Google", [chrome, drive]);

        Assert.DoesNotContain(
            attribution.Candidates,
            c => c.Accepted && c.Classification is Classification.Confirmed or Classification.High);
    }
}
