using AppTrace.Core.Attribution;
using AppTrace.Core.Model;

namespace AppTrace.Core.Tests;

/// <summary>
/// Tests for the diagnostic helper itself and for install-location handling,
/// which is the anchor every other attribution judgement is measured against.
/// </summary>
public class InstallLocationAttributionTests
{
    [Fact]
    public void InstallLocationMatch_TakesPrecedenceOverNameAbsence()
    {
        var app = Fixtures.App("Contoso Analytics", "Contoso", @"C:\Contoso\Analytics");

        var attribution = Fixtures.Evaluate(@"C:\Contoso\Analytics\cache", [app]);

        var candidate = Fixtures.Candidate(attribution, "Contoso Analytics");
        Assert.NotNull(candidate);
        Assert.True(candidate.Accepted);
        Assert.Contains(candidate.Evidence, e => e.Type == EvidenceType.InstallLocationMatch);
    }

    [Fact]
    public void ExactInstallLocation_WithCorroboratingName_IsConfirmedAndStops()
    {
        var app = Fixtures.App("Discord", "Discord Inc.", @"C:\Program Files\Discord");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Discord", [app]);

        Assert.True(attribution.OwnershipEstablished);
        Assert.Equal(Classification.Confirmed, attribution.Classification);
        var candidate = Fixtures.Candidate(attribution, "Discord");
        Assert.NotNull(candidate);
        Assert.Contains(candidate.Evidence, e => e.Type == EvidenceType.DeclaredInstallLocation);
    }

    [Fact]
    public void VendorNamespaceDeclaredBySeveralProducts_IsSharedAndNeverConfirmed()
    {
        // Vendors routinely register a shared parent directory as the
        // InstallLocation of every product beneath it. That must never be confirmed
        // for one product, and it must never be silently closed: the location is
        // reported as SHARED and the scanner descends to find the real per-product
        // boundary.
        var chrome = Fixtures.App("Google Chrome", "Google LLC", @"C:\Program Files\Google");
        var drive = Fixtures.App("Google Drive", "Google LLC", @"C:\Program Files\Google");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Google", [chrome, drive]);

        Assert.Equal(Classification.Shared, attribution.Classification);
        Assert.False(attribution.OwnershipEstablished);
        Assert.Equal(2, attribution.AcceptedOwners.Count);
        Assert.DoesNotContain(
            attribution.Candidates.SelectMany(c => c.Evidence),
            e => e.Type == EvidenceType.DeclaredInstallLocation);
    }

    [Fact]
    public void VendorDirectoryReachedWithoutAnInstallLocationClaim_StaysOpen()
    {
        // Reached as a plain parent directory rather than as an exact copy of a
        // declared install location. A vendor name is not a product, so nothing is
        // accepted and the descent continues.
        var chrome = Fixtures.App("Google Chrome", "Google LLC", @"C:\Vendor\Google");
        var drive = Fixtures.App("Google Drive", "Google LLC", @"C:\Vendor\Google");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Google", [chrome, drive]);

        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.False(attribution.OwnershipEstablished);
        Assert.Empty(attribution.AcceptedOwners);
    }

    [Fact]
    public void PathOutsideAnyInstallLocation_GetsNoInstallEvidence()
    {
        var app = Fixtures.App("Contoso Analytics", "Contoso", @"C:\Contoso\Analytics");

        var attribution = Fixtures.Evaluate(@"C:\Program Files\Unrelated\Thing", [app]);

        Assert.DoesNotContain(
            attribution.Candidates.SelectMany(c => c.Evidence),
            e => e.Type == EvidenceType.InstallLocationMatch);
    }
}
