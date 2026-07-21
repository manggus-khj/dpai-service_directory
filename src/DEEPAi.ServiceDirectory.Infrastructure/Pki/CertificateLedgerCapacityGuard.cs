using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DEEPAi.ServiceDirectory.Domain.Certificates;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed class CertificateLedgerCapacityExceededException
        : InvalidOperationException
    {
        internal CertificateLedgerCapacityExceededException()
            : base("The projected canonical certificate ledger exceeds 16 MiB.")
        {
        }
    }

    internal static class CertificateIssuancePreflight
    {
        internal static CertificateIssuanceReplayStatus Evaluate(
            CertificateLedgerSnapshot currentLedger,
            IEnumerable<CertificateLedgerEntry> projectedExistingEntries,
            ulong projectedPkiRevision,
            ulong projectedCrlNumber,
            CertificateSerialNumber issuerCaSerialNumber,
            CertificateIssuanceRequestEvidence evidence,
            byte[] subjectPublicKeyInfoSha256,
            DateTime issuedUtc,
            DateTime notBeforeUtc,
            DateTime notAfterUtc,
            out CertificateLedgerEntry replayEntry)
        {
            if (currentLedger == null)
            {
                throw new ArgumentNullException(nameof(currentLedger));
            }

            CertificateIssuanceReplayStatus replayStatus =
                currentLedger.ResolveIssuanceRequest(
                    evidence,
                    out replayEntry);
            if (replayStatus != CertificateIssuanceReplayStatus.NewRequest)
            {
                return replayStatus;
            }

            CertificateLedgerCapacityGuard.EnsureMaximumIssuanceFits(
                projectedExistingEntries,
                projectedPkiRevision,
                projectedCrlNumber,
                issuerCaSerialNumber,
                evidence,
                subjectPublicKeyInfoSha256,
                issuedUtc,
                notBeforeUtc,
                notAfterUtc);
            return CertificateIssuanceReplayStatus.NewRequest;
        }
    }

    internal static class CertificateLedgerCapacityGuard
    {
        internal static void EnsureMaximumIssuanceFits(
            IEnumerable<CertificateLedgerEntry> projectedExistingEntries,
            ulong projectedPkiRevision,
            ulong projectedCrlNumber,
            CertificateSerialNumber issuerCaSerialNumber,
            CertificateIssuanceRequestEvidence evidence,
            byte[] subjectPublicKeyInfoSha256,
            DateTime issuedUtc,
            DateTime notBeforeUtc,
            DateTime notAfterUtc)
        {
            if (projectedExistingEntries == null)
            {
                throw new ArgumentNullException(
                    nameof(projectedExistingEntries));
            }

            if (evidence == null)
            {
                throw new ArgumentNullException(nameof(evidence));
            }

            var entries = new List<CertificateLedgerEntry>(
                projectedExistingEntries);
            CertificateSerialNumber projectionSerial =
                CreateUnusedProjectionSerial(entries);
            byte[] csrSha256 = evidence.GetCsrSha256();
            byte[] payloadSha256 = evidence.GetRequestPayloadSha256();
            var maximumLeaf = new byte[
                CertificateLedgerEntry.MaximumLeafCertificateBytes];
            maximumLeaf[0] = 0x30;
            try
            {
                entries.Add(CertificateLedgerEntry.CreateIssued(
                    projectionSerial,
                    issuerCaSerialNumber,
                    evidence.ServiceDefinition,
                    evidence.RequestId,
                    evidence.IssuanceKind,
                    csrSha256,
                    payloadSha256,
                    subjectPublicKeyInfoSha256,
                    maximumLeaf,
                    issuedUtc,
                    notBeforeUtc,
                    notAfterUtc));
            }
            finally
            {
                Array.Clear(csrSha256, 0, csrSha256.Length);
                Array.Clear(payloadSha256, 0, payloadSha256.Length);
                Array.Clear(maximumLeaf, 0, maximumLeaf.Length);
            }

            var projection = new CertificateLedgerSnapshot(
                entries,
                projectedPkiRevision,
                projectedCrlNumber);
            if (!new CertificateAuthorityStateCodec()
                    .IsLedgerWithinDocumentLimit(projection))
            {
                throw new CertificateLedgerCapacityExceededException();
            }
        }

        private static CertificateSerialNumber CreateUnusedProjectionSerial(
            IEnumerable<CertificateLedgerEntry> entries)
        {
            var used = new HashSet<string>(
                entries.Select(entry => entry.SerialNumber.Hex),
                StringComparer.Ordinal);
            for (ulong suffix = 0; ; suffix++)
            {
                string value = "7F00000000000000"
                    + suffix.ToString("X16", CultureInfo.InvariantCulture);
                if (!used.Contains(value))
                {
                    CertificateSerialNumber serialNumber;
                    if (!CertificateSerialNumber.TryCreate(
                            value,
                            out serialNumber))
                    {
                        throw new InvalidOperationException(
                            "The projection serial number is invalid.");
                    }

                    return serialNumber;
                }

                if (suffix == ulong.MaxValue)
                {
                    throw new InvalidOperationException(
                        "No projection serial number is available.");
                }
            }
        }
    }
}
