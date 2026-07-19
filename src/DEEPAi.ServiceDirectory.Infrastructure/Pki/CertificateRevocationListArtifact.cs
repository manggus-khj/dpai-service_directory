using System;
using DEEPAi.ServiceDirectory.Domain.Certificates;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed class RevokedCertificateEntry
    {
        internal RevokedCertificateEntry(
            PkiSerialNumber serialNumber,
            DateTime revokedUtc,
            CertificateRevocationReason reason)
        {
            SerialNumber = serialNumber
                ?? throw new ArgumentNullException(nameof(serialNumber));
            if (revokedUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Revocation timestamps must use DateTimeKind.Utc.",
                    nameof(revokedUtc));
            }

            if (!Enum.IsDefined(typeof(CertificateRevocationReason), reason)
                || reason == CertificateRevocationReason.Unspecified)
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }

            RevokedUtc = revokedUtc;
            Reason = reason;
        }

        internal PkiSerialNumber SerialNumber { get; }

        internal DateTime RevokedUtc { get; }

        internal CertificateRevocationReason Reason { get; }
    }

    internal sealed class CertificateRevocationListArtifact
    {
        private readonly byte[] _derBytes;
        private readonly byte[] _sha256;

        internal CertificateRevocationListArtifact(
            ulong crlNumber,
            DateTime thisUpdateUtc,
            DateTime nextUpdateUtc,
            byte[] derBytes,
            byte[] sha256)
        {
            if (crlNumber == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(crlNumber),
                    "CRL number must be greater than zero.");
            }

            if (thisUpdateUtc.Kind != DateTimeKind.Utc
                || nextUpdateUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "CRL timestamps must use DateTimeKind.Utc.");
            }

            if (nextUpdateUtc <= thisUpdateUtc)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nextUpdateUtc),
                    "CRL nextUpdate must be after thisUpdate.");
            }

            if (derBytes == null || derBytes.Length == 0)
            {
                throw new ArgumentException("CRL DER must not be empty.", nameof(derBytes));
            }

            if (sha256 == null || sha256.Length != 32)
            {
                throw new ArgumentException(
                    "CRL SHA-256 must be exactly 32 bytes.",
                    nameof(sha256));
            }

            CrlNumber = crlNumber;
            ThisUpdateUtc = thisUpdateUtc;
            NextUpdateUtc = nextUpdateUtc;
            _derBytes = (byte[])derBytes.Clone();
            _sha256 = (byte[])sha256.Clone();
        }

        internal ulong CrlNumber { get; }

        internal DateTime ThisUpdateUtc { get; }

        internal DateTime NextUpdateUtc { get; }

        internal byte[] GetDerBytes()
        {
            return (byte[])_derBytes.Clone();
        }

        internal byte[] GetSha256()
        {
            return (byte[])_sha256.Clone();
        }

        internal string GetQuotedEtag()
        {
            return "\"" + Convert.ToBase64String(_sha256) + "\"";
        }
    }
}
