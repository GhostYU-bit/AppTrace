using AppTrace.Core.Attribution;
using AppTrace.Core.Discovery;
using AppTrace.Core.Model;

namespace AppTrace.Core.Tests;

/// <summary>
/// Task 10: Windows package identity (MSIX/AppX) as a first-class, explainable
/// provenance source inside Attribution Engine V2.
/// </summary>
/// <remarks>
/// <para><b>The claim these tests protect.</b> Windows registered a package, and
/// Windows gave that package exactly one per-user data namespace:
/// <c>%LOCALAPPDATA%\Packages\&lt;package family name&gt;</c>. That structured
/// registration — not a resemblance between the directory name and a product
/// name — is what may name an owner there.</para>
/// <para><b>What must never happen.</b> The claim may not reach the
/// <c>Packages</c> container, a neighbouring package, or any directory outside the
/// package data root, and it must not be produced for an application Windows did
/// not register a package for.</para>
/// <para>No test asserts anything about the machine it runs on: the package data
/// root is the synthetic constant <see cref="Fixtures.PackageDataRoot"/>.</para>
/// </remarks>
public class PackageEvidenceTests
{
    private const string AcrobatFamilyName = "AdobeAcrobatDCCoreApp_n3f54xz2h0aj4";

    private static string PackageDirectory(string packageFamilyName)
        => Path.Combine(Fixtures.PackageDataRoot, packageFamilyName);

    private static bool HasPackageEvidence(CandidateOwner? candidate)
        => candidate is not null
            && candidate.Supporting.Any(e => e.Type == EvidenceType.PackageDataRoot);

    // ---------------------------------------------------------------------
    // Windows identity names the package's own data namespace
    // ---------------------------------------------------------------------

    [Fact]
    public void APackageDataRootIsOwnedByTheApplicationWindowsRegisteredUnderIt()
    {
        var acrobat = Fixtures.App(
            "Adobe Acrobat DC",
            "Adobe Inc.",
            packageFamilyName: AcrobatFamilyName);

        var attribution = Fixtures.Evaluate(
            PackageDirectory(AcrobatFamilyName),
            [acrobat],
            LocationCategory.LocalAppData);

        var candidate = Fixtures.Candidate(attribution, "Adobe Acrobat DC");
        Assert.True(HasPackageEvidence(candidate), "The package registration should name the owner.");

        // The directory name is the package family name, so no name compared against
        // the display name could ever have proposed this candidate. It is the Windows
        // registration that establishes the owner, and that is a decisive statement
        // about this exact directory - the same standing as a declared install root.
        Assert.Equal(Classification.Confirmed, attribution.Classification);
        Assert.True(attribution.OwnershipEstablished);
        Assert.Equal(acrobat.Id, Assert.Single(attribution.AcceptedOwners).AppId);

        var evidence = Assert.Single(candidate!.Supporting, e => e.Type == EvidenceType.PackageDataRoot);
        Assert.Equal(EvidenceSource.PackageIdentity, evidence.Source);
        Assert.Contains(AcrobatFamilyName, evidence.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void APackageClaimCannotReachThePackagesContainer()
    {
        var acrobat = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.", packageFamilyName: AcrobatFamilyName);

        // The parent of every package's data namespace is shared infrastructure, not
        // any one package's storage. An ancestor rule would hand it to whichever
        // package was looked up first.
        var attribution = Fixtures.Evaluate(Fixtures.PackageDataRoot, [acrobat], LocationCategory.LocalAppData);

        Assert.Empty(attribution.AcceptedOwners);
        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.All(attribution.Candidates, c => Assert.False(HasPackageEvidence(c)));
    }

    [Fact]
    public void APackageClaimCannotReachANeighbouringPackage()
    {
        var acrobat = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.", packageFamilyName: AcrobatFamilyName);

        // A prefix rule over package family names is a real hazard: these two names
        // differ only by a suffix, and Windows partitioned them as two namespaces.
        var attribution = Fixtures.Evaluate(
            PackageDirectory(AcrobatFamilyName + "_reader"),
            [acrobat],
            LocationCategory.LocalAppData);

        Assert.Empty(attribution.AcceptedOwners);
        Assert.Equal(Classification.Unknown, attribution.Classification);
    }

    [Fact]
    public void APackageClaimCannotReachOutsideThePackageDataRoot()
    {
        var acrobat = Fixtures.App(
            "Adobe Acrobat DC",
            "Adobe Inc.",
            @"C:\Program Files\Adobe\Acrobat DC",
            packageFamilyName: AcrobatFamilyName);

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Adobe\Acrobat DC", [acrobat]);

        // The same application, in its traditional install root, is still attributed
        // by its declared install location and only by that: a package registration
        // is a statement about the package data namespace, not a global licence to
        // claim every path the application touches.
        var candidate = Fixtures.Candidate(attribution, "Adobe Acrobat DC")!;
        Assert.False(HasPackageEvidence(candidate));
        Assert.Contains(candidate.Supporting, e => e.Type == EvidenceType.DeclaredInstallLocation);
        Assert.Equal(Classification.Confirmed, attribution.Classification);
    }

    [Fact]
    public void PackageEvidenceNamesTheNamespaceItselfAndNothingInsideIt()
    {
        var acrobat = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.", packageFamilyName: AcrobatFamilyName);
        var root = PackageDirectory(AcrobatFamilyName);

        // Content below the namespace is attributed by the ownership assertion the
        // scanner carries down from the confirmed namespace, not by a second package
        // record. The registration names one directory, and re-deriving it per
        // descendant would be a different - and unreachable - claim.
        var attribution = Fixtures.EvaluateDescendant(
            Path.Combine(root, "LocalCache"),
            [acrobat],
            ownedAncestorPath: root,
            ownedAncestorAppId: acrobat.Id);

        var candidate = Fixtures.Candidate(attribution, "Adobe Acrobat DC");
        Assert.NotNull(candidate);
        Assert.False(HasPackageEvidence(candidate));
        Assert.Contains(candidate!.Supporting, e => e.Type == EvidenceType.InheritedFromOwner);
    }

    // ---------------------------------------------------------------------
    // Absence of a package registration is not a package claim
    // ---------------------------------------------------------------------

    [Fact]
    public void AnApplicationWithoutPackageRegistrationReceivesNoPackageEvidence()
    {
        var acrobat = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.");

        // The directory looks like a package namespace, but nothing registered this
        // application as a package, so Windows has said nothing about it. The
        // registration must come from Windows, never from the directory's shape.
        var attribution = Fixtures.Evaluate(
            PackageDirectory(AcrobatFamilyName),
            [acrobat],
            LocationCategory.LocalAppData);

        Assert.Empty(attribution.AcceptedOwners);
        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.All(attribution.Candidates, c => Assert.False(HasPackageEvidence(c)));
    }

    [Fact]
    public void APackageDirectoryWithNoRegisteredApplicationStaysUnknown()
    {
        // A framework or dependency package is not discovered as an application at
        // all, so its data namespace has no identity to be attributed to. Refusing an
        // owner here is the honest outcome, not a gap to be closed by a name rule.
        var acrobat = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.", packageFamilyName: AcrobatFamilyName);

        var attribution = Fixtures.Evaluate(
            PackageDirectory("Microsoft.VCLibs.140.00_8wekyb3d8bbwe"),
            [acrobat],
            LocationCategory.LocalAppData);

        Assert.Empty(attribution.AcceptedOwners);
        Assert.Equal(Classification.Unknown, attribution.Classification);
    }

    [Fact]
    public void PackageEvidenceIsProvenanceAndRanksWithADeclaredInstallLocation()
    {
        Assert.Equal(EvidenceKind.Provenance, EvidenceClassification.KindOf(EvidenceType.PackageDataRoot));
        Assert.Equal(
            EvidenceWeights.WeightOf(EvidenceType.DeclaredInstallLocation),
            EvidenceWeights.WeightOf(EvidenceType.PackageDataRoot));
    }

    [Fact]
    public void ThePackageDataRootIsInertWhenNoRootIsConfigured()
    {
        var acrobat = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.", packageFamilyName: AcrobatFamilyName);

        // An empty root means "this machine has no per-user package namespace"; the
        // application keeps its package family name but nothing is attributed.
        var attribution = Fixtures.Evaluate(
            PackageDirectory(AcrobatFamilyName),
            [acrobat],
            LocationCategory.LocalAppData,
            packageDataRoot: "");

        Assert.Empty(attribution.AcceptedOwners);
        Assert.All(attribution.Candidates, c => Assert.False(HasPackageEvidence(c)));
    }

    // ---------------------------------------------------------------------
    // Reconciliation with classic Win32 registrations
    // ---------------------------------------------------------------------

    [Fact]
    public void APackageIsMergedIntoTheClassicRecordForTheSameInstall()
    {
        var classic = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.", @"C:\Program Files\Adobe\Acrobat DC");
        var package = PackageIdentity(
            "Adobe Acrobat DC",
            @"C:\Program Files\Adobe\Acrobat DC\Acrobat",
            AcrobatFamilyName);

        var reconciled = UninstallRegistry.ReconcilePackages([classic, package]);

        // One user-facing application, one identity. The classic record survives
        // because it carries the install root and the uninstall entry a user sees, and
        // it gains the package family name so the package's data namespace is still
        // attributed to it.
        var merged = Assert.Single(reconciled);
        Assert.Equal(classic.Id, merged.Id);
        Assert.Equal(classic.InstallLocation, merged.InstallLocation);
        Assert.Equal(AcrobatFamilyName, Assert.Single(merged.PackageFamilyNames));
    }

    [Fact]
    public void ASameNamedPackageInADifferentRootIsKeptSeparate()
    {
        var classic = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.", @"C:\Program Files\Adobe\Acrobat DC");
        var package = PackageIdentity(
            "Adobe Acrobat DC",
            @"C:\Program Files\Adobe\Acrobat Reader DC",
            "AdobeAcrobatReaderDC_x");

        var reconciled = UninstallRegistry.ReconcilePackages([classic, package]);

        // Sharing a display name is not evidence of being the same installation. An
        // unresolved duplicate is visible and honest; a false merge would silently
        // hide one application inside another.
        Assert.Equal(2, reconciled.Count);
        Assert.Empty(Assert.Single(reconciled, r => r.Id == classic.Id).PackageFamilyNames);
    }

    [Fact]
    public void AContainerRootedPackageInTheClassicRootButWithAnotherNameIsKeptSeparate()
    {
        var classic = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.", @"C:\Program Files\Adobe\Acrobat DC");
        var package = PackageIdentity(
            "Adobe Creative Cloud",
            @"C:\Program Files\Adobe\Acrobat DC",
            "AdobeCreativeCloud_x");

        var reconciled = UninstallRegistry.ReconcilePackages([classic, package]);

        Assert.Equal(2, reconciled.Count);
    }

    [Fact]
    public void APackageWithoutAKnownInstallRootIsNeverMerged()
    {
        var classic = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.", @"C:\Program Files\Adobe\Acrobat DC");
        var package = PackageIdentity("Adobe Acrobat DC", packageRoot: null, AcrobatFamilyName);

        var reconciled = UninstallRegistry.ReconcilePackages([classic, package]);

        // Windows declared no filesystem location, so there is nothing to agree with
        // the classic record about. Refusing to merge is the conservative outcome.
        Assert.Equal(2, reconciled.Count);
        Assert.Empty(Assert.Single(reconciled, r => r.Id == classic.Id).PackageFamilyNames);
    }

    [Fact]
    public void SeveralPackagesOfOneApplicationAreAllCarriedByTheClassicRecord()
    {
        var classic = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.", @"C:\Program Files\Adobe\Acrobat DC");
        var core = PackageIdentity("Adobe Acrobat DC", @"C:\Program Files\Adobe\Acrobat DC", AcrobatFamilyName);
        var extension = PackageIdentity(
            "Adobe Acrobat DC",
            @"C:\Program Files\Adobe\Acrobat DC\Extensions",
            "AdobeAcrobatDCExtensions_n3f54xz2h0aj4");

        var reconciled = UninstallRegistry.ReconcilePackages([classic, core, extension]);

        var merged = Assert.Single(reconciled);
        Assert.Equal(
            [AcrobatFamilyName, "AdobeAcrobatDCExtensions_n3f54xz2h0aj4"],
            merged.PackageFamilyNames.OrderBy(n => n, StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------------
    // Package identity derivation
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("Microsoft.WindowsCalculator_11.2210.0.0_x64__8wekyb3d8bbwe", "Microsoft.WindowsCalculator_8wekyb3d8bbwe")]
    [InlineData("AdobeAcrobatDCCoreApp_1.0.0.0_neutral_~_n3f54xz2h0aj4", AcrobatFamilyName)]
    [InlineData("NotAPackageName", null)]
    [InlineData("_8wekyb3d8bbwe", null)]
    [InlineData("Name_", null)]
    public void ThePackageFamilyNameIsDerivedFromTheRegistrationKey(string packageFullName, string? expected)
    {
        // The repository surface stores no package family name, so it is derived. The
        // resource id in the middle is dropped, because a package family groups a
        // package with its resource and bundle variants - which is exactly the
        // granularity %LOCALAPPDATA%\Packages uses.
        Assert.Equal(expected, UninstallRegistry.PackageFamilyNameOf(packageFullName));
    }

    [Fact]
    public void APackageIsMergedWhenTheUninstallEntryNamesAPayloadInsideThePackageRoot()
    {
        // Windows registers a packaged product twice, and the two registrations do not
        // have to agree on the display name. The uninstall entry names a file inside the
        // package's own versioned install root, which is a structural statement about the
        // same installed payload - not a name resemblance.
        const string root = @"C:\Program Files\Microsoft OneDrive\26.168.0830.0006";
        var classic = Fixtures.App(
            "Microsoft OneDrive",
            "Microsoft Corporation",
            displayIcon: root + @"\OneDriveSetup.exe,-101");
        var package = PackageIdentity("OneDrive", root, "Microsoft.OneDriveSync_8wekyb3d8bbwe");

        var reconciled = UninstallRegistry.ReconcilePackages([classic, package]);

        var merged = Assert.Single(reconciled);
        Assert.Equal(classic.Id, merged.Id);
        Assert.Equal(
            "Microsoft.OneDriveSync_8wekyb3d8bbwe",
            Assert.Single(merged.PackageFamilyNames));
    }

    [Fact]
    public void APackageIsNotMergedWhenTheUninstallEntryNamesAFileOutsideThePackageRoot()
    {
        // The launcher sits beside the versioned root rather than inside it, so the
        // registration says nothing about which package payload it belongs to. Two
        // identities are reported rather than one guessed merge.
        var classic = Fixtures.App(
            "Microsoft OneDrive",
            "Microsoft Corporation",
            displayIcon: @"C:\Program Files\Microsoft OneDrive\OneDrive.exe");
        var package = PackageIdentity(
            "OneDrive",
            @"C:\Program Files\Microsoft OneDrive\26.168.0830.0006",
            "Microsoft.OneDriveSync_8wekyb3d8bbwe");

        var reconciled = UninstallRegistry.ReconcilePackages([classic, package]);

        Assert.Equal(2, reconciled.Count);
    }

    [Fact]
    public void APackageIsNotMergedWhenSeveralClassicRecordsNamePayloadInsideIt()
    {
        // Two uninstall entries pointing into one package root are ambiguous about which
        // is the packaged application, so the package is left as its own identity.
        const string root = @"C:\Gamma\26.0.0.0";
        var first = Fixtures.App("Alpha Suite", displayIcon: root + @"\alpha.exe");
        var second = Fixtures.App("Beta Suite", uninstallString: "\"" + root + @"\beta.exe"" /uninstall");
        var package = PackageIdentity("Gamma", root, "GammaPublisher_abcd1234efgh5");

        var reconciled = UninstallRegistry.ReconcilePackages([first, second, package]);

        Assert.Equal(3, reconciled.Count);
    }

    /// <summary>A package identity shaped like the one AppX discovery produces.</summary>
    private static AppIdentity PackageIdentity(string displayName, string? packageRoot, string packageFamilyName)
        => new()
        {
            Id = "app-" + TextNormalizer.Fold(packageFamilyName),
            DisplayName = displayName,
            NormalizedName = TextNormalizer.NormalizeDisplayName(displayName),
            Version = "1.0.0.0",
            InstallLocation = packageRoot,
            NormalizedInstallLocation = packageRoot is null ? null : TextNormalizer.NormalizePath(packageRoot),
            ProductCode = $"{packageFamilyName}_1.0.0.0_x64__0",
            RegistrySource = @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\" + packageFamilyName,
            RegistryRoot = RegistryRootKind.CurrentUser,
            DiscoveryKind = DiscoveryKind.MsixPackage,
            PackageFamilyNames = [packageFamilyName],
        };
}
