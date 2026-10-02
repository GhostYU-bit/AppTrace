namespace AppTrace.Core.Attribution;

using AppTrace.Core.Model;

/// <summary>
/// The score each evidence type contributes.
/// </summary>
/// <remarks>
/// <para>
/// The weights are explicit, documented, and deliberately coarse. They exist so
/// the final <see cref="Classification"/> is a mechanical function of the
/// evidence rather than a number invented afterwards. Phase 0 has no
/// probabilistic model, so the score is never shown as a percentage.
/// </para>
/// <para>
/// The design follows Bulk Crap Uninstaller's proven approach of a signed score
/// where evidence can also <em>reduce</em> confidence
/// (<c>UninstallTools/Junk/Confidence/ConfidenceRecords.cs</c>, Apache-2.0). The
/// scales differ because AppTrace answers "who owns this?" rather than "is this
/// safe to delete?", and because AppTrace must be able to express several
/// simultaneous owners.
/// </para>
/// </remarks>
public static class EvidenceWeights
{
    /// <summary>
    /// The score each evidence type contributes, used to rank and threshold
    /// candidates.
    /// </summary>
    /// <remarks>
    /// <para><b>Task 04: weights no longer decide the top of the ladder.</b> A
    /// weight orders candidates and sets score floors; whether a claim may be
    /// CONFIRMED or HIGH is decided by <see cref="EvidenceKind"/> in
    /// <c>AttributionEngine.ClassifyCandidate</c>. That is why
    /// <see cref="EvidenceType.KnownApplicationPath"/> is now worth 0 rather than
    /// +8: it was never evidence about an owner, and adding it to a name match was
    /// enough to reach HIGH.</para>
    /// <para><see cref="EvidenceKind.Structure"/> records keep their weights only
    /// so that shared-owner detection and candidate ranking still work; they may
    /// never contribute to a confident ownership claim.</para>
    /// </remarks>
    public static int WeightOf(EvidenceType type) => type switch
    {
        // Provenance: the application's own registration names this path.
        EvidenceType.DeclaredInstallLocation => 95,
        EvidenceType.DiscoveryLocationMatch => 85,
        EvidenceType.InstallLocationMatch => 70,
        EvidenceType.ProductCodeMatch => 55,
        EvidenceType.RegistryReference => 45,

        // Inherited ownership is deliberately weak. It is real evidence about the
        // directory's scope, but it is the only provenance record that does not come
        // from the directory itself, so it must be able to be outranked by anything
        // the directory says on its own behalf.
        EvidenceType.InheritedFromOwner => 18,

        // A subject-name match is never scored. It is listed for completeness so the
        // zero is explicit rather than accidental.
        EvidenceType.SubjectNameMatch => 0,

        // Provenance from Windows itself (Task 07). Weaker than a declared install
        // location, because a service or a shortcut proves the application reaches a
        // file rather than that it owns the directory containing it, and stronger
        // than inherited ownership, because it is an independent registration rather
        // than an inference from scope.
        EvidenceType.DisplayIconMatch => 60,
        EvidenceType.ProvenanceAnchorMatch => 50,

        // Identity: strong name identity.
        EvidenceType.ExactDirectoryNameMatch => 40,
        EvidenceType.ExecutableMetadataMatch => 40,
        EvidenceType.NormalizedNameMatch => 30,

        // Structure: describes the location, never the owner.
        // KnownApplicationPath is deliberately worth nothing. Being under
        // AppData\Local, AppData\Roaming, LocalLow or Program Files says that
        // applications store data here, which is true of nearly every path
        // AppTrace inspects; it says nothing about which application owns it.
        EvidenceType.KnownApplicationPath => 0,

        // The product-boundary record from Task 07.6 is worth nothing for the same
        // reason: being at the right depth below a data root is where a candidate may
        // be proposed, not proof that the candidate owns anything. It exists so the
        // candidate-generation decision is explicit and auditable, and it must never
        // be able to move a candidate up the ladder on its own.
        EvidenceType.DataRootProductBoundary => 0,
        EvidenceType.ParentDirectoryMatch => 12,
        EvidenceType.ChildDirectoryMatch => 10,
        EvidenceType.PublisherMatch => 15,
        EvidenceType.KnownPublisherNamespace => 10,

        // Contradicting / bounding signals. A decisive contradiction is enforced as
        // a gate in classification; its weight is belt-and-braces for ranking.
        EvidenceType.SharedPublisherDirectory => -18,
        EvidenceType.MultipleCandidateOwners => -15,
        EvidenceType.GenericDirectoryName => -20,
        EvidenceType.ConflictingApplicationMatch => -25,
        EvidenceType.PublisherMismatch => -22,
        EvidenceType.UnknownApplication => -30,
        EvidenceType.SystemManagedPath => -100,

        _ => 0,
    };

    /// <summary>Maps a declared strength onto the score, keeping the two in step.</summary>
    public static int WeightOf(EvidenceType type, EvidenceStrength _) => WeightOf(type);
}
