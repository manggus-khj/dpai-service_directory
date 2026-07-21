using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DEEPAi.ServiceDirectory.Domain.Certificates
{
    public sealed class CertificateLedgerSnapshot
    {
        public const int CurrentCertificateLimit = 1000;

        private readonly IReadOnlyDictionary<CertificateSerialNumber, CertificateLedgerEntry>
            _entriesBySerial;
        private readonly IReadOnlyDictionary<Guid, CertificateLedgerEntry>
            _entriesByRequestId;
        private readonly IReadOnlyDictionary<ProductCode, CertificateLedgerEntry>
            _currentByProductCode;

        public CertificateLedgerSnapshot(
            IEnumerable<CertificateLedgerEntry> entries,
            ulong pkiRevision,
            ulong crlNumber)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            var bySerial = new Dictionary<CertificateSerialNumber, CertificateLedgerEntry>();
            var byRequestId = new Dictionary<Guid, CertificateLedgerEntry>();
            var currentByProductCode = new Dictionary<ProductCode, CertificateLedgerEntry>();
            bool containsRevocation = false;
            foreach (CertificateLedgerEntry entry in entries)
            {
                if (entry == null)
                {
                    throw new ArgumentException(
                        "Certificate ledger cannot contain null entries.",
                        nameof(entries));
                }

                if (bySerial.ContainsKey(entry.SerialNumber))
                {
                    throw new ArgumentException(
                        "Certificate ledger contains a duplicate serial number.",
                        nameof(entries));
                }

                if (byRequestId.ContainsKey(entry.IssuanceRequestId))
                {
                    throw new ArgumentException(
                        "Certificate ledger contains a duplicate issuance request ID.",
                        nameof(entries));
                }

                bySerial.Add(entry.SerialNumber, entry);
                byRequestId.Add(entry.IssuanceRequestId, entry);
                if (entry.Status == CertificateLedgerStatus.Current)
                {
                    if (currentByProductCode.ContainsKey(entry.ProductCode))
                    {
                        throw new ArgumentException(
                            "Only one current certificate is allowed per product code.",
                            nameof(entries));
                    }

                    currentByProductCode.Add(entry.ProductCode, entry);
                }
                else if (entry.Status == CertificateLedgerStatus.Revoked)
                {
                    containsRevocation = true;
                }
            }

            if (currentByProductCode.Count > CurrentCertificateLimit)
            {
                throw new ArgumentException(
                    "Current certificate count exceeds the supported limit.",
                    nameof(entries));
            }

            if ((bySerial.Count > 0 || crlNumber > 0) && pkiRevision == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pkiRevision),
                    "Non-empty PKI state must have a positive revision.");
            }

            if (containsRevocation && crlNumber == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(crlNumber),
                    "Revoked certificates require a published CRL number.");
            }

            _entriesBySerial =
                new ReadOnlyDictionary<CertificateSerialNumber, CertificateLedgerEntry>(
                    bySerial);
            _entriesByRequestId =
                new ReadOnlyDictionary<Guid, CertificateLedgerEntry>(byRequestId);
            _currentByProductCode =
                new ReadOnlyDictionary<ProductCode, CertificateLedgerEntry>(
                    currentByProductCode);
            PkiRevision = pkiRevision;
            CrlNumber = crlNumber;
        }

        public IReadOnlyDictionary<CertificateSerialNumber, CertificateLedgerEntry>
            EntriesBySerial => _entriesBySerial;

        public int CurrentCount => _currentByProductCode.Count;

        public ulong PkiRevision { get; }

        public ulong CrlNumber { get; }

        public static CertificateLedgerSnapshot Empty()
        {
            return new CertificateLedgerSnapshot(
                new CertificateLedgerEntry[0],
                0,
                0);
        }

        public bool TryGetByRequestId(
            Guid requestId,
            out CertificateLedgerEntry entry)
        {
            return _entriesByRequestId.TryGetValue(requestId, out entry);
        }

        public CertificateIssuanceReplayStatus ResolveIssuanceRequest(
            CertificateIssuanceRequestEvidence evidence,
            out CertificateLedgerEntry entry)
        {
            if (evidence == null)
            {
                throw new ArgumentNullException(nameof(evidence));
            }

            if (!_entriesByRequestId.TryGetValue(
                    evidence.RequestId,
                    out entry))
            {
                return CertificateIssuanceReplayStatus.NewRequest;
            }

            byte[] csrSha256 = evidence.GetCsrSha256();
            byte[] payloadSha256 = evidence.GetRequestPayloadSha256();
            try
            {
                return entry.MatchesIssuanceRequest(
                        evidence.IssuanceKind,
                        evidence.RequestId,
                        csrSha256,
                        payloadSha256)
                    ? CertificateIssuanceReplayStatus.ExactReplay
                    : CertificateIssuanceReplayStatus.Conflict;
            }
            finally
            {
                Array.Clear(csrSha256, 0, csrSha256.Length);
                Array.Clear(payloadSha256, 0, payloadSha256.Length);
            }
        }

        public bool TryGetBySerial(
            CertificateSerialNumber serialNumber,
            out CertificateLedgerEntry entry)
        {
            if (!serialNumber.IsValid)
            {
                throw new ArgumentException(
                    "Certificate serial number must be valid.",
                    nameof(serialNumber));
            }

            return _entriesBySerial.TryGetValue(serialNumber, out entry);
        }

        public bool TryGetCurrent(
            ProductCode productCode,
            out CertificateLedgerEntry entry)
        {
            if (!productCode.IsValid)
            {
                throw new ArgumentException(
                    "Product code must be valid.",
                    nameof(productCode));
            }

            return _currentByProductCode.TryGetValue(productCode, out entry);
        }
    }
}
