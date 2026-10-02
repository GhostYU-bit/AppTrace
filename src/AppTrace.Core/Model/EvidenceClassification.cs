namespace AppTrace.Core.Model;

/// <summary>
/// Maps every evidence type to the question it answers, and every contradicting
/// type to how strongly it argues.
/// </summary>
/// <remarks>
/// <para>The mapping lives in one place so that "which kind is this record?" is a
/// single reviewable decision rather than a scattered judgement, and so a new
/// evidence type cannot be added without a kind.</para>
/// <para><b>Why the boundaries sit where they do.</b> A record belongs in
/// <see cref="EvidenceKind.Provenance"/> only when it independently links the
/// application to the path — registration data the application itself wrote. A
/// record belongs in <see cref="EvidenceKind.Identity"/> when it tells us what the
/// path appears to be, which is a different and weaker statement. A record belongs
/// in <see cref="EvidenceKind.Structure"/> when it describes the location rather
/// than an owner. Only a record that names a differently identified owner (or the
/// system itself) is a <see cref="EvidenceKind.Contradiction"/>.</para>
/// </remarks>
public static class EvidenceClassification
{
    public static EvidenceKind KindOf(EvidenceType type) => type switch
    {
        // ---- Identity: does this name/content identify the application? --------
        // A name or a binary's metadata says what something *appears* to be. It is
        // evidence about identity, never a registration of ownership.
        EvidenceType.ExactDirectoryNameMatch => EvidenceKind.Identity,
        EvidenceType.NormalizedNameMatch => EvidenceKind.Identity,
        EvidenceType.ExecutableMetadataMatch => EvidenceKind.Identity,

        // ---- Provenance: is the application independently anchored here? -------
        // All four are written by the application's own installation or by another
        // component that registered this path for it. These are the only records
        // that can carry a confident ownership claim.
        EvidenceType.DeclaredInstallLocation => EvidenceKind.Provenance,
        EvidenceType.InstallLocationMatch => EvidenceKind.Provenance,
        EvidenceType.DiscoveryLocationMatch => EvidenceKind.Provenance,
        EvidenceType.ProductCodeMatch => EvidenceKind.Provenance,
        EvidenceType.RegistryReference => EvidenceKind.Provenance,

        // ---- Structure: what does this path tell us about the storage? ---------
        // None of these can identify an owner. KnownApplicationPath in particular
        // says only "applications store data here", which is true of almost every
        // path AppTrace inspects, so it must never raise ownership confidence.
        EvidenceType.KnownApplicationPath => EvidenceKind.Structure,
        EvidenceType.KnownPublisherNamespace => EvidenceKind.Structure,
        EvidenceType.ParentDirectoryMatch => EvidenceKind.Structure,
        EvidenceType.ChildDirectoryMatch => EvidenceKind.Structure,
        EvidenceType.GenericDirectoryName => EvidenceKind.Structure,

        // MultipleCandidateOwners describes the location's state, not the claim.
        // It is ambiguity, not opposition: several applications being plausible
        // owners is a reason to report SHARED or to keep descending, never a reason
        // to say one of them is wrong. It therefore belongs to Structure and only
        // caps confidence by its weight.
        EvidenceType.MultipleCandidateOwners => EvidenceKind.Structure,
        EvidenceType.SharedPublisherDirectory => EvidenceKind.Structure,

        // PublisherMatch is emitted only for a directory that several installed
        // products registered as their install location and that is named after
        // their shared publisher. It identifies a vendor namespace rather than a
        // product, so it corroborates a candidate set without proving one owner.
        EvidenceType.PublisherMatch => EvidenceKind.Structure,

        // ---- Relationship: owned by X, or merely about X? ----------------------
        // A subject-name match says the location is *about* an application. It is
        // never ownership evidence and is never scored, which is what keeps
        // "NVIDIA App owns this, Cities: Skylines is its subject" from decaying into
        // "Cities: Skylines owns this".
        EvidenceType.UnknownApplication => EvidenceKind.Relationship,
        EvidenceType.SubjectNameMatch => EvidenceKind.Relationship,

        // InheritedFromOwner is a statement about scope rather than about identity:
        // an ancestor is owned, so this directory is inside that ownership. It is
        // classified as Provenance because it originates outside the directory's own
        // name, and it is emitted at Weak strength so independent evidence at the
        // directory can outrank it and a decisive contradiction can still forbid the
        // claim.
        EvidenceType.InheritedFromOwner => EvidenceKind.Provenance,

        // ---- Provenance from Windows itself (Task 07) ---------------------------
        // These are the records that answer "can Windows show that the application
        // actually reaches this path", which is a different and stronger question
        // than "does this path look like the application". They are Provenance for
        // the same reason DeclaredInstallLocation is: they were written by the
        // application's own installation or by Windows on its behalf, not inferred
        // from a name.
        EvidenceType.DisplayIconMatch => EvidenceKind.Provenance,
        EvidenceType.ProvenanceAnchorMatch => EvidenceKind.Provenance,

        // ---- Contradiction: what argues against the claim? --------------------
        EvidenceType.PublisherMismatch => EvidenceKind.Contradiction,
        EvidenceType.ConflictingApplicationMatch => EvidenceKind.Contradiction,
        EvidenceType.SystemManagedPath => EvidenceKind.Contradiction,

        // A type added without a kind here falls back to Identity, which is the
        // most conservative choice: Identity alone can never reach HIGH, so a
        // forgotten mapping under-claims rather than over-claims.
        _ => EvidenceKind.Identity,
    };

    /// <summary>
    /// How strongly a contradicting record argues.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative for its first implementation. A decisive
    /// contradiction names a differently identified owner, so no accumulation of
    /// weak support may override it. Everything else only caps confidence.
    /// </remarks>
    public static ContradictionKind ContradictionKindOf(EvidenceType type) => type switch
    {
        // Another application is specifically identified as the owner of this
        // scope, or the system owns it outright. These forbid the claim.
        EvidenceType.PublisherMismatch => ContradictionKind.Decisive,
        EvidenceType.ConflictingApplicationMatch => ContradictionKind.Decisive,
        EvidenceType.SystemManagedPath => ContradictionKind.Decisive,

        _ => ContradictionKind.Limiting,
    };

    /// <summary>True when this type is a contradiction that forbids ownership.</summary>
    public static bool IsDecisiveContradiction(EvidenceType type)
        => KindOf(type) == EvidenceKind.Contradiction
           && ContradictionKindOf(type) == ContradictionKind.Decisive;
}
