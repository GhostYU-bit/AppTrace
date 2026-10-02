using AppTrace.Core.Attribution;
using AppTrace.Core.Model;

namespace AppTrace.Core.Tests;

/// <summary>
/// Regression coverage for the DaVinci Resolve / Electron failure found on a real
/// machine during Task 02.
/// </summary>
/// <remarks>
/// <para><c>C:\Program Files\Blackmagic Design\DaVinci Resolve\Electron\electron.exe</c>
/// reports <c>CompanyName = "GitHub, Inc."</c>. Phase 0 matched the installed
/// application <c>Git</c> against it because <c>git</c> is a substring of the
/// folded <c>githubinc</c>, and reported a confident ownership claim on a component
/// of a different vendor's product.</para>
/// <para>The lesson the tests protect is not "GitHub is special": it is that
/// <b>a component binary's metadata does not make an unrelated installed
/// application the owner of the containing directory</b>. There is deliberately no
/// Git/GitHub blacklist anywhere in the engine.</para>
/// </remarks>
public class ExecutableMetadataOwnershipTests
{
    private static AppIdentity Git() => Fixtures.App("Git", "The Git Development Community", @"C:\Program Files\Git\");

    private static AppIdentity DaVinci() => Fixtures.App("DaVinci Resolve", "Blackmagic Design");

    /// <summary>
    /// The exact metadata the real binary reports, asserted against the matcher the
    /// engine uses, so the rule is checked even on a machine that has no such file.
    /// </summary>
    [Theory]
    [InlineData("GitHub, Inc.")]   // CompanyName of the real electron.exe
    [InlineData("Electron")]       // ProductName and FileDescription of the same binary
    [InlineData("electron.exe")]   // InternalName
    public void AComponentBinaryMetadataDoesNotIdentifyAnUnrelatedApplication(string metadataValue)
    {
        var gitSegments = TextNormalizer.Tokens(TextNormalizer.NormalizeDisplayName("Git")).ToArray();

        Assert.False(
            AttributionEngine.IdentifiesWholeProduct(metadataValue, gitSegments),
            $"'{metadataValue}' must not be treated as naming the application \"Git\".");
    }

    [Fact]
    public void TheElectronDirectoryIsNotAttributedToGit()
    {
        // End-to-end through the real engine with the real application set. The
        // evidence a scan actually collects here is the identity metadata of the
        // binary; because that metadata names GitHub rather than Git, no identity
        // evidence for Git is produced and nothing may be accepted on its behalf.
        var attribution = Fixtures.Evaluate(
            @"C:\Program Files\Blackmagic Design\DaVinci Resolve\Electron",
            [Git(), DaVinci()]);

        Assert.NotEqual(Classification.High, attribution.Classification);
        Assert.NotEqual(Classification.Confirmed, attribution.Classification);

        var git = Fixtures.Candidate(attribution, "Git");
        Assert.False(
            git is { Accepted: true },
            "Git must not be accepted as an owner of a DaVinci Resolve component directory.");
    }

    [Fact]
    public void GenuineProductMetadataStillCorroboratesIdentity()
    {
        // The tightening must not make metadata matching useless: a binary that
        // genuinely names the product still corroborates it.
        var chromeSegments = TextNormalizer.Tokens(TextNormalizer.NormalizeDisplayName("Google Chrome")).ToArray();
        Assert.True(AttributionEngine.IdentifiesWholeProduct("Google Chrome", chromeSegments));
        Assert.True(AttributionEngine.IdentifiesWholeProduct("Chrome", chromeSegments));
    }
}
