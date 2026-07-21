using System;
using System.Collections.Generic;
using System.Globalization;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class CertificateLedgerCapacityGuardTests
    {
        private const int OversizedHistoryEntryCount = 800;

        [TestMethod]
        public void SmallProjectionReservesMaximumLeafBeforeRealIssuance()
        {
            CertificateIssuanceRequestEvidence evidence = Evidence();
            CertificateLedgerEntry replayEntry;

            Assert.AreEqual(
                CertificateIssuanceReplayStatus.NewRequest,
                CertificateIssuancePreflight.Evaluate(
                    CertificateLedgerSnapshot.Empty(),
                    new CertificateLedgerEntry[0],
                    1,
                    1,
                    evidence,
                    Hash(0x33),
                    TestData.Utc(1),
                    TestData.Utc(0),
                    TestData.Utc(3),
                    out replayEntry));
            Assert.IsNull(replayEntry);
        }

        [TestMethod]
        public void OversizedCanonicalProjectionIsRejectedBeforeRealIssuance()
        {
            IReadOnlyList<CertificateLedgerEntry> history =
                CreateOversizedRevokedHistory();
            var currentLedger = new CertificateLedgerSnapshot(
                history,
                1,
                1);
            CertificateLedgerEntry replayEntry;

            Assert.ThrowsExactly<CertificateLedgerCapacityExceededException>(
                () => CertificateIssuancePreflight.Evaluate(
                        currentLedger,
                        history,
                        2,
                        1,
                        Evidence(),
                        Hash(0x33),
                        TestData.Utc(1),
                        TestData.Utc(0),
                        TestData.Utc(3),
                        out replayEntry));
        }

        [TestMethod]
        public void ReplayAndConflictReturnBeforeCapacityProjection()
        {
            CertificateIssuanceRequestEvidence evidence = Evidence();
            CertificateLedgerEntry stored = CreateStoredEntry(evidence);
            var ledger = new CertificateLedgerSnapshot(
                new[] { stored },
                1,
                1);
            CertificateLedgerEntry replayEntry;

            Assert.AreEqual(
                CertificateIssuanceReplayStatus.ExactReplay,
                CertificateIssuancePreflight.Evaluate(
                    ledger,
                    null,
                    0,
                    0,
                    evidence,
                    null,
                    default(DateTime),
                    default(DateTime),
                    default(DateTime),
                    out replayEntry));
            Assert.AreSame(stored, replayEntry);

            Assert.AreEqual(
                CertificateIssuanceReplayStatus.Conflict,
                CertificateIssuancePreflight.Evaluate(
                    ledger,
                    null,
                    0,
                    0,
                    CertificateIssuanceRequestEvidence.CreateRegistration(
                        evidence.RequestId,
                        evidence.ServiceDefinition,
                        new byte[] { 0x30, 0x02, 0x00, 0x00 }),
                    null,
                    default(DateTime),
                    default(DateTime),
                    default(DateTime),
                    out replayEntry));
            Assert.AreSame(stored, replayEntry);
        }

        private static IReadOnlyList<CertificateLedgerEntry>
            CreateOversizedRevokedHistory()
        {
            var entries = new List<CertificateLedgerEntry>(
                OversizedHistoryEntryCount);
            var maximumLeaf = new byte[
                CertificateLedgerEntry.MaximumLeafCertificateBytes];
            maximumLeaf[0] = 0x30;
            try
            {
                for (int index = 0;
                    index < OversizedHistoryEntryCount;
                    index++)
                {
                    CertificateLedgerEntry issued =
                        CertificateLedgerEntry.CreateIssued(
                            Serial(index),
                            TestData.Definition(
                                "Historical Service",
                                "AB12",
                                "history.example.local",
                                "10.20.30.41",
                                21000),
                            RequestId(index),
                            CertificateIssuanceKind.Registration,
                            Hash(0x11),
                            Hash(0x22),
                            Hash(0x33),
                            maximumLeaf,
                            TestData.Utc(1),
                            TestData.Utc(0),
                            TestData.Utc(3));
                    entries.Add(issued.Revoke(
                        TestData.Utc(2),
                        CertificateRevocationReason.Superseded));
                }
            }
            finally
            {
                Array.Clear(maximumLeaf, 0, maximumLeaf.Length);
            }

            return entries.AsReadOnly();
        }

        private static CertificateIssuanceRequestEvidence Evidence()
        {
            return CertificateIssuanceRequestEvidence.CreateRegistration(
                new Guid("7f35b4b8-854d-4ca1-90bc-da196772f49f"),
                TestData.Definition(
                    "VMS Bridge",
                    "CD34",
                    "vms-bridge.example.local",
                    "10.20.30.40",
                    21000),
                new byte[] { 0x30, 0x01, 0x00 });
        }

        private static CertificateLedgerEntry CreateStoredEntry(
            CertificateIssuanceRequestEvidence evidence)
        {
            byte[] csrSha256 = evidence.GetCsrSha256();
            byte[] payloadSha256 = evidence.GetRequestPayloadSha256();
            try
            {
                return CertificateLedgerEntry.CreateIssued(
                    Serial(OversizedHistoryEntryCount + 1),
                    evidence.ServiceDefinition,
                    evidence.RequestId,
                    evidence.IssuanceKind,
                    csrSha256,
                    payloadSha256,
                    Hash(0x33),
                    new byte[] { 0x30, 0x01, 0x00 },
                    TestData.Utc(1),
                    TestData.Utc(0),
                    TestData.Utc(3));
            }
            finally
            {
                Array.Clear(csrSha256, 0, csrSha256.Length);
                Array.Clear(payloadSha256, 0, payloadSha256.Length);
            }
        }

        private static CertificateSerialNumber Serial(int index)
        {
            string value = "01"
                + (index + 1).ToString(
                    "X30",
                    CultureInfo.InvariantCulture);
            CertificateSerialNumber serialNumber;
            Assert.IsTrue(CertificateSerialNumber.TryCreate(
                value,
                out serialNumber));
            return serialNumber;
        }

        private static Guid RequestId(int index)
        {
            return new Guid(
                index + 1,
                0,
                0,
                new byte[8]);
        }

        private static byte[] Hash(byte value)
        {
            var result = new byte[CertificateLedgerEntry.Sha256Length];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = value;
            }

            return result;
        }
    }
}
