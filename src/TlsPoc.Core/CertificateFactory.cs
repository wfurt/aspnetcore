using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TlsPoc.Core;

public static class CertificateFactory
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>
    /// Creates a self-signed server certificate. <paramref name="algorithm"/> selects the key:
    /// "ecdsap256" (default) or "rsa2048". The choice matters for any handshake measurement -
    /// an RSA-2048 signature costs far more than an ECDSA P-256 one, so it changes how large
    /// the TLS layer's own overhead looks as a share of the handshake.
    /// </summary>
    public static X509Certificate2 CreateSelfSigned(
        string commonName,
        bool clientAuth = false,
        string algorithm = "ecdsap256")
    {
        var isRsa = algorithm.Equals("rsa2048", StringComparison.OrdinalIgnoreCase);

        using var rsa = isRsa ? RSA.Create(2048) : null;
        using var ecdsa = isRsa ? null : ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = isRsa
            ? new CertificateRequest($"CN={commonName}", rsa!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest($"CN={commonName}", ecdsa!, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(clientAuth ? ClientAuthOid : ServerAuthOid)], false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(commonName);
        request.CertificateExtensions.Add(san.Build());

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));

        // SChannel requires the private key to live in a key container, so round-trip via PKCS#12.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }
}
