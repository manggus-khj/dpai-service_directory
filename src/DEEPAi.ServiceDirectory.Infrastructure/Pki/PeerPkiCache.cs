using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed class PeerPkiCacheCertificate
    {
        private readonly byte[] _leafSha256;

        internal PeerPkiCacheCertificate(
            ProductCode productCode,
            CertificateSerialNumber serialNumber,
            byte[] leafSha256,
            DateTime notAfterUtc)
        {
            if (!productCode.IsValid)
            {
                throw new ArgumentException(
                    "Peer PKI cache product code must be valid.",
                    nameof(productCode));
            }

            if (!serialNumber.IsValid)
            {
                throw new ArgumentException(
                    "Peer PKI cache serial number must be valid.",
                    nameof(serialNumber));
            }

            if (leafSha256 == null
                || leafSha256.Length
                    != CertificateLedgerEntry.Sha256Length)
            {
                throw new ArgumentException(
                    "Peer PKI cache leaf hash must contain 32 bytes.",
                    nameof(leafSha256));
            }

            if (notAfterUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Peer PKI cache expiry must use DateTimeKind.Utc.",
                    nameof(notAfterUtc));
            }

            ProductCode = productCode;
            SerialNumber = serialNumber;
            _leafSha256 = (byte[])leafSha256.Clone();
            NotAfterUtc = notAfterUtc;
        }

        internal ProductCode ProductCode { get; }

        internal CertificateSerialNumber SerialNumber { get; }

        internal DateTime NotAfterUtc { get; }

        internal byte[] GetLeafSha256()
        {
            return (byte[])_leafSha256.Clone();
        }
    }

    internal sealed class PeerPkiCacheSnapshot
    {
        internal const int MaximumActiveCertificateCount = 1000;

        private readonly byte[] _crlSha256;
        private readonly IReadOnlyList<PeerPkiCacheCertificate>
            _activeCertificates;

        internal PeerPkiCacheSnapshot(
            Guid issuerInstanceId,
            ulong pkiRevision,
            ulong crlNumber,
            byte[] crlSha256,
            IEnumerable<PeerPkiCacheCertificate> activeCertificates)
        {
            if (issuerInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Peer PKI cache issuer instance ID must not be empty.",
                    nameof(issuerInstanceId));
            }

            if (pkiRevision == 0 || crlNumber == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pkiRevision),
                    "Peer PKI cache high-water values must be positive.");
            }

            if (crlSha256 == null
                || crlSha256.Length
                    != CertificateLedgerEntry.Sha256Length)
            {
                throw new ArgumentException(
                    "Peer PKI cache CRL hash must contain 32 bytes.",
                    nameof(crlSha256));
            }

            if (activeCertificates == null)
            {
                throw new ArgumentNullException(nameof(activeCertificates));
            }

            var copy = new List<PeerPkiCacheCertificate>();
            var productCodes = new HashSet<ProductCode>();
            var serialNumbers = new HashSet<CertificateSerialNumber>();
            foreach (PeerPkiCacheCertificate certificate in activeCertificates)
            {
                if (certificate == null)
                {
                    throw new ArgumentException(
                        "Peer PKI cache cannot contain null certificates.",
                        nameof(activeCertificates));
                }

                if (!productCodes.Add(certificate.ProductCode)
                    || !serialNumbers.Add(certificate.SerialNumber))
                {
                    throw new ArgumentException(
                        "Peer PKI cache product codes and serial numbers must be unique.",
                        nameof(activeCertificates));
                }

                copy.Add(certificate);
                if (copy.Count > MaximumActiveCertificateCount)
                {
                    throw new ArgumentException(
                        "Peer PKI cache exceeds the active certificate limit.",
                        nameof(activeCertificates));
                }
            }

            copy.Sort((left, right) => string.CompareOrdinal(
                left.ProductCode.Value,
                right.ProductCode.Value));
            IssuerInstanceId = issuerInstanceId;
            PkiRevision = pkiRevision;
            CrlNumber = crlNumber;
            _crlSha256 = (byte[])crlSha256.Clone();
            _activeCertificates =
                new ReadOnlyCollection<PeerPkiCacheCertificate>(copy);
        }

        internal Guid IssuerInstanceId { get; }

        internal ulong PkiRevision { get; }

        internal ulong CrlNumber { get; }

        internal IReadOnlyList<PeerPkiCacheCertificate> ActiveCertificates =>
            _activeCertificates;

        internal byte[] GetCrlSha256()
        {
            return (byte[])_crlSha256.Clone();
        }
    }
}
