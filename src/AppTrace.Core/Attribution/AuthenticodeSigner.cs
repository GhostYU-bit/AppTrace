namespace AppTrace.Core.Attribution;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Reads the publisher named by an executable's <em>embedded</em> Authenticode
/// certificate.
/// </summary>
/// <remarks>
/// <para><b>What this does.</b> The cheap embedded-certificate path only:
/// <see cref="X509Certificate.CreateFromSignedFile(string)"/> returns the signing
/// certificate the file carries in its own security directory. It does <b>not</b>
/// build or validate a trust chain, and it does <b>not</b> consult the Windows
/// catalog store, so no full Authenticode verification happens on the attribution
/// path.</para>
/// <para><b>Why the obsoleted API is used deliberately.</b> .NET marks
/// <c>CreateFromSignedFile</c> obsolete (SYSLIB0057) in favour of
/// <see cref="X509CertificateLoader"/>, but the loader only loads standalone
/// certificates from data. Extracting the signature embedded in a PE file without
/// this API would mean either reimplementing CMS/PKCS#7 decoding or taking a
/// dependency on <c>System.Security.Cryptography.Pkcs</c>; AppTrace keeps zero
/// third-party production dependencies, so the purpose-built API is the smaller,
/// better-justified choice. The suppression is scoped to the single call.</para>
/// <para><b>What absence means.</b> A <see langword="null"/> result means "no
/// embedded publisher certificate was observed", which is deliberately not the
/// same statement as "this binary is unsigned": Windows commonly signs binaries
/// through a catalog rather than by embedding a certificate. Absence therefore
/// produces no evidence at all and is never converted into a contradiction.</para>
/// <para><b>Cost.</b> One Win32 call per file, and only for the directories the
/// bounded executable probe already visits.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class AuthenticodeSigner
{
    /// <summary>
    /// The publisher named by the executable's embedded certificate, or
    /// <see langword="null"/> when no embedded certificate was observed or it
    /// names no publisher.
    /// </summary>
    public static string? ReadPublisher(string executablePath)
    {
        try
        {
#pragma warning disable SYSLIB0057 // See the "why the obsoleted API" note above.
            var certificate = X509Certificate.CreateFromSignedFile(executablePath);
#pragma warning restore SYSLIB0057
            using var certificate2 = new X509Certificate2(certificate);
            var name = certificate2.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (CryptographicException)
        {
            // No embedded signature, or a malformed one. Both are normal outcomes
            // and say nothing about whether Windows considers the file signed.
            return null;
        }
        catch (Exception e) when (Scanning.DirectoryWalker.IsRecoverable(e))
        {
            return null;
        }
    }
}