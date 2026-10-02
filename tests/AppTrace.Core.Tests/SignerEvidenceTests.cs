using AppTrace.Core.Attribution;
using AppTrace.Core.Model;

namespace AppTrace.Core.Tests;

/// <summary>
/// Task 09: an executable's embedded Authenticode signer as a bounded,
/// publisher-level corroboration source.
/// </summary>
/// <remarks>
/// <para><b>Publisher agreement is not product identity and is not directory
/// ownership.</b> These tests protect both halves of that statement, plus the
/// dangerous cases the task names: a publisher that signs several installed
/// products, a signed binary inside a generic container, and the Honest
/// "no embedded certificate observed" outcome that must never become a
/// contradiction.</para>
/// <para>No test asserts anything about the machine it runs on: the signer value
/// is supplied through the engine's binary-probe override, exactly as the
/// deterministic corpus disables the real probe.</para>
/// </remarks>
public class SignerEvidenceTests
{
    private static Func<string, ExecutableProbe> Signer(string? publisher)
        => _ => ExecutableProbe.ForTesting([], publisher);

    private static bool HasSigner(CandidateOwner? candidate)
        => candidate is not null
            && candidate.Supporting.Any(e => e.Type == EvidenceType.SignerPublisherMatch);

    // ---------------------------------------------------------------------
    // Useful corroboration
    // ---------------------------------------------------------------------

    [Fact]
    public void ASignatureFromTheCandidatesPublisherCorroboratesIt()
    {
        var acrobat = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.");

        var attribution = Fixtures.EvaluateWithProbe(
            @"C:\Program Files\Adobe\Acrobat DC",
            [acrobat],
            Signer("Adobe Inc."));

        var candidate = Fixtures.Candidate(attribution, "Adobe Acrobat DC");
        Assert.True(HasSigner(candidate), "Publisher agreement should corroborate the candidate.");

        // Corroboration only: a publisher is not a product identity, so the claim
        // stays at the honest ceiling for identity-without-provenance.
        Assert.Equal(Classification.Medium, candidate!.Classification);
    }

    [Fact]
    public void ASignatureStrengthensAnAlreadyProposedCandidate()
    {
        var acrobat = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.");
        var fields = new[] { new KeyValuePair<string, string>("ProductName", "Adobe Acrobat DC") };

        var withoutSigner = Fixtures.EvaluateWithProbe(
            @"C:\Program Files\Adobe\Acrobat DC",
            [acrobat],
            _ => ExecutableProbe.ForTesting(fields, signerPublisher: null));

        var withSigner = Fixtures.EvaluateWithProbe(
            @"C:\Program Files\Adobe\Acrobat DC",
            [acrobat],
            _ => ExecutableProbe.ForTesting(fields, "Adobe Inc."));

        var before = Fixtures.Candidate(withoutSigner, "Adobe Acrobat DC")!;
        var after = Fixtures.Candidate(withSigner, "Adobe Acrobat DC")!;

        Assert.False(HasSigner(before));
        Assert.True(HasSigner(after));
        Assert.Equal(
            EvidenceWeights.WeightOf(EvidenceType.SignerPublisherMatch),
            after.Score - before.Score);

        // Publisher agreement corroborates; it never makes the claim itself
        // confident, so the classification stays at the identity ceiling.
        Assert.Equal(Classification.Medium, after.Classification);
    }

    // ---------------------------------------------------------------------
    // Dangerous cases
    // ---------------------------------------------------------------------

    [Fact]
    public void APublisherThatSignsTwoProductsCannotChooseBetweenThem()
    {
        // Two products from one publisher are both proposed for this directory:
        // "Acme Tools" by name, "Acme Suite" by the install location it registered.
        // Each has substantive evidence of its own, so publisher agreement would
        // otherwise apply to both — and therefore must apply to neither.
        var root = @"C:\Program Files\Acme Tools";
        var tools = Fixtures.App("Acme Tools", "Acme Corp");
        var suite = Fixtures.App("Acme Suite", "Acme Corp", root);

        var attribution = Fixtures.EvaluateWithProbe(root, [tools, suite], Signer("Acme Corp"));

        var toolsCandidate = Fixtures.Candidate(attribution, "Acme Tools");
        var suiteCandidate = Fixtures.Candidate(attribution, "Acme Suite");
        Assert.NotNull(toolsCandidate);
        Assert.NotNull(suiteCandidate);

        Assert.False(HasSigner(toolsCandidate), "A shared signer cannot pick one of two products.");
        Assert.False(HasSigner(suiteCandidate), "A shared signer cannot pick one of two products.");
    }

    [Fact]
    public void ASignatureCannotCreateACandidate()
    {
        var chrome = Fixtures.App("Google Chrome", "Google LLC");

        // The directory name does not identify the application and no registration
        // anchors it, so nothing proposes a candidate. A signature must not become a
        // back door through candidate generation.
        var attribution = Fixtures.EvaluateWithProbe(
            @"C:\Program Files\SomeTool",
            [chrome],
            Signer("Google LLC"));

        Assert.Empty(attribution.AcceptedOwners);
        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.All(attribution.Candidates, c => Assert.False(HasSigner(c)));
    }

    [Fact]
    public void ASignatureCannotPromoteProvenanceToAConfidentClaim()
    {
        var acme = Fixtures.App("Acme Suite", "Acme Corp");
        var ancestor = @"C:\Users\user\AppData\Local\Acme";

        // Inside an owned ancestor, with a name that carries no identity, the only
        // evidence is inherited ownership (provenance). Publisher agreement must
        // corroborate without turning "this publisher likely reaches here" into
        // "this application owns this directory".
        var attribution = Fixtures.EvaluateWithProbe(
            ancestor + @"\Cache",
            [acme],
            Signer("Acme Corp"),
            LocationCategory.LocalAppData,
            ownedAncestors:
            [
                new OwnedAncestor(
                    TextNormalizer.NormalizePath(ancestor),
                    acme.Id,
                    Classification.Confirmed),
            ]);

        var candidate = Fixtures.Candidate(attribution, "Acme Suite");
        Assert.True(HasSigner(candidate));
        Assert.Equal(Classification.Medium, attribution.Classification);
        Assert.NotEqual(Classification.High, attribution.Classification);
    }

    [Fact]
    public void ASignedBinaryInsideAGenericContainerDoesNotClaimTheContainer()
    {
        // The real-world case the task calls out: %LOCALAPPDATA%\Programs holds many
        // products' installs under one uninformative name. A signed descendant must
        // not hand the container to its publisher's product.
        var code = Fixtures.App(
            "Visual Studio Code",
            "Microsoft Corporation",
            @"C:\Users\user\AppData\Local\Programs\Microsoft VS Code");

        var attribution = Fixtures.EvaluateWithProbe(
            @"C:\Users\user\AppData\Local\Programs",
            [code],
            Signer("Microsoft Corporation"),
            LocationCategory.LocalAppData);

        Assert.Empty(attribution.AcceptedOwners);
        Assert.Equal(Classification.Unknown, attribution.Classification);
        Assert.False(attribution.OwnershipEstablished);
        Assert.All(attribution.Candidates, c => Assert.False(HasSigner(c)));
    }

    [Fact]
    public void AComponentSignatureDoesNotAttributeAnotherVendorsDirectory()
    {
        // The Task 02 regression, re-checked with signer evidence present:
        // electron.exe reports "GitHub, Inc.", which is neither the installed Git
        // application nor proof that DaVinci Resolve's component directory is Git's.
        var git = Fixtures.App("Git", "The Git Development Community", @"C:\Program Files\Git\");
        var daVinci = Fixtures.App("DaVinci Resolve", "Blackmagic Design");

        var attribution = Fixtures.EvaluateWithProbe(
            @"C:\Program Files\Blackmagic Design\DaVinci Resolve\Electron",
            [git, daVinci],
            Signer("GitHub, Inc."));

        Assert.False(
            Fixtures.Candidate(attribution, "Git") is { Accepted: true },
            "Git must not be accepted as an owner of a DaVinci Resolve component directory.");
        Assert.False(HasSigner(Fixtures.Candidate(attribution, "Git")));
    }

    // ---------------------------------------------------------------------
    // Absence is not opposition
    // ---------------------------------------------------------------------

    [Fact]
    public void AnUnobservedEmbeddedCertificateProducesNoEvidenceAtAll()
    {
        var acrobat = Fixtures.App("Adobe Acrobat DC", "Adobe Inc.");

        var attribution = Fixtures.EvaluateWithProbe(
            @"C:\Program Files\Adobe\Acrobat DC",
            [acrobat],
            Signer(null));

        var candidate = Fixtures.Candidate(attribution, "Adobe Acrobat DC");
        Assert.False(HasSigner(candidate));
        Assert.All(attribution.Candidates, c => Assert.Empty(c.Contradicting));

        // Windows signs through catalogs too, so "not observed" must not change the
        // verdict the other evidence already justified.
        Assert.Equal(Classification.Medium, candidate!.Classification);
    }

    [Fact]
    public void ReadingSignerFromAFileWithoutAnEmbeddedCertificateReturnsNull()
    {
        // A nonexistent file and a plain text file both have no embedded
        // certificate. Neither may throw, and neither may be reported as a
        // publisher.
        Assert.Null(AuthenticodeSigner.ReadPublisher(@"C:\does\not\exist\nothing.exe"));

        var temporary = Path.Combine(AppContext.BaseDirectory, $"apptrace-signer-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllText(temporary, "not a signed portable executable");
            Assert.Null(AuthenticodeSigner.ReadPublisher(temporary));
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}