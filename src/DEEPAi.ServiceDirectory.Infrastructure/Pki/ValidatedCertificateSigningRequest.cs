using System;
using DEEPAi.ServiceDirectory.Domain;
using Org.BouncyCastle.Crypto;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal enum CertificateSigningRequestValidationError
    {
        None = 0,
        Empty,
        TooLarge,
        Malformed,
        NonCanonicalDer,
        SignatureAlgorithmNotAllowed,
        SignatureInvalid,
        PublicKeyUnsupported,
        PublicKeyTooWeak,
        RequestedExtensionsMissing,
        RequestedExtensionsInvalid,
        UnsupportedExtension,
        SubjectAlternativeNameInvalid,
        SubjectAlternativeNameMismatch
    }

    internal sealed class ValidatedCertificateSigningRequest
    {
        private readonly byte[] _derBytes;
        private readonly byte[] _subjectPublicKeyInfoSha256;

        internal ValidatedCertificateSigningRequest(
            byte[] derBytes,
            ServiceEndpointIdentity identity,
            AsymmetricKeyParameter publicKey,
            byte[] subjectPublicKeyInfoSha256)
        {
            _derBytes = (byte[])derBytes.Clone();
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            PublicKey = publicKey ?? throw new ArgumentNullException(nameof(publicKey));
            _subjectPublicKeyInfoSha256 = (byte[])subjectPublicKeyInfoSha256.Clone();
        }

        internal ServiceEndpointIdentity Identity { get; }

        internal AsymmetricKeyParameter PublicKey { get; }

        internal byte[] GetDerBytes()
        {
            return (byte[])_derBytes.Clone();
        }

        internal byte[] GetSubjectPublicKeyInfoSha256()
        {
            return (byte[])_subjectPublicKeyInfoSha256.Clone();
        }
    }
}
