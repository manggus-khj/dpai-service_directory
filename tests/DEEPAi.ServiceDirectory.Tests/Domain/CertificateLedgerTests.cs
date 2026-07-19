using System;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Domain
{
    [TestClass]
    public sealed class CertificateLedgerTests
    {
        [TestMethod]
        public void IssuedEntryPreservesImmutableIdempotencyEvidence()
        {
            byte[] csrHash = Hash(0x11);
            byte[] payloadHash = Hash(0x22);
            CertificateLedgerEntry entry = CreateEntry(
                "01A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                "AB12",
                new Guid("7f35b4b8-854d-4ca1-90bc-da196772f49f"),
                csrHash,
                payloadHash);

            csrHash[0] = 0xff;
            payloadHash[0] = 0xff;

            Assert.AreEqual(CertificateLedgerStatus.Current, entry.Status);
            Assert.AreEqual(CertificateIssuanceKind.Registration, entry.IssuanceKind);
            Assert.AreEqual("vms-bridge.example.local", entry.ServiceIdentity.ServiceHostName);
            Assert.AreEqual((byte)0x11, entry.GetCsrSha256()[0]);
            Assert.AreEqual((byte)0x22, entry.GetRequestPayloadSha256()[0]);
            Assert.IsTrue(entry.MatchesIssuanceRequest(
                entry.IssuanceRequestId,
                Hash(0x11),
                Hash(0x22)));
            Assert.IsFalse(entry.MatchesIssuanceRequest(
                entry.IssuanceRequestId,
                Hash(0x11),
                Hash(0x23)));
        }

        [TestMethod]
        public void RevocationIsOneWayAndRequiresCrlHighWater()
        {
            CertificateLedgerEntry active = CreateEntry(
                "02A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                "AB12",
                Guid.NewGuid(),
                Hash(0x11),
                Hash(0x22));
            CertificateLedgerEntry retiring = active.ScheduleRevocation(
                TestData.Utc(2));
            Assert.AreEqual(CertificateLedgerStatus.Retiring, retiring.Status);
            Assert.AreEqual(TestData.Utc(2), retiring.ScheduledRevocationUtc.Value);

            CertificateLedgerEntry revoked = retiring.Revoke(
                TestData.Utc(2),
                CertificateRevocationReason.Superseded);

            Assert.AreEqual(CertificateLedgerStatus.Revoked, revoked.Status);
            Assert.AreEqual(TestData.Utc(2), revoked.RevokedUtc.Value);
            Assert.AreEqual(
                CertificateRevocationReason.Superseded,
                revoked.RevocationReason.Value);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                active.Revoke(
                    TestData.Utc(2),
                    CertificateRevocationReason.Unspecified));
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                revoked.Revoke(
                    TestData.Utc(3),
                    CertificateRevocationReason.Superseded));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                new CertificateLedgerSnapshot(new[] { revoked }, 1, 0));

            var snapshot = new CertificateLedgerSnapshot(
                new[] { revoked },
                1,
                1);
            Assert.AreEqual(0, snapshot.CurrentCount);
            Assert.AreEqual(1UL, snapshot.CrlNumber);
        }

        [TestMethod]
        public void SnapshotRejectsDuplicateSerialRequestAndCurrentProductCode()
        {
            Guid requestId = Guid.NewGuid();
            CertificateLedgerEntry first = CreateEntry(
                "03A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                "AB12",
                requestId,
                Hash(0x11),
                Hash(0x22));
            CertificateLedgerEntry duplicateSerial = CreateEntry(
                first.SerialNumber.Hex,
                "CD34",
                Guid.NewGuid(),
                Hash(0x31),
                Hash(0x32));
            CertificateLedgerEntry duplicateRequest = CreateEntry(
                "04A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                "CD34",
                requestId,
                Hash(0x41),
                Hash(0x42));
            CertificateLedgerEntry duplicateProduct = CreateEntry(
                "05A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                "AB12",
                Guid.NewGuid(),
                Hash(0x51),
                Hash(0x52));

            Assert.ThrowsExactly<ArgumentException>(() =>
                new CertificateLedgerSnapshot(
                    new[] { first, duplicateSerial },
                    1,
                    0));
            Assert.ThrowsExactly<ArgumentException>(() =>
                new CertificateLedgerSnapshot(
                    new[] { first, duplicateRequest },
                    1,
                    0));
            Assert.ThrowsExactly<ArgumentException>(() =>
                new CertificateLedgerSnapshot(
                    new[] { first, duplicateProduct },
                    1,
                    0));
        }

        [TestMethod]
        public void SnapshotAllowsRenewalOverlapButRejectsOrphanedRetiringEntry()
        {
            CertificateLedgerEntry previous = CreateEntry(
                "06A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                "AB12",
                Guid.NewGuid(),
                Hash(0x61),
                Hash(0x62)).ScheduleRevocation(TestData.Utc(2));
            CertificateLedgerEntry current = CreateEntry(
                "07A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                "AB12",
                Guid.NewGuid(),
                Hash(0x71),
                Hash(0x72));

            var snapshot = new CertificateLedgerSnapshot(
                new[] { previous, current },
                2,
                0);
            Assert.AreEqual(1, snapshot.CurrentCount);
            CertificateLedgerEntry resolved;
            Assert.IsTrue(snapshot.TryGetCurrent(
                TestData.ProductCode("AB12"),
                out resolved));
            Assert.AreSame(current, resolved);

            Assert.ThrowsExactly<ArgumentException>(() =>
                new CertificateLedgerSnapshot(
                    new[] { previous },
                    1,
                    0));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("00A4A5A6A7A8A9AAABACADAEAFB0B1B2")]
        [DataRow("80A4A5A6A7A8A9AAABACADAEAFB0B1B2")]
        [DataRow("01a4a5a6a7a8a9aaabacadaeafb0b1b2")]
        [DataRow("01A4")]
        public void SerialNumberRejectsNonCanonicalValues(string value)
        {
            CertificateSerialNumber serialNumber;
            Assert.IsFalse(CertificateSerialNumber.TryCreate(value, out serialNumber));
            Assert.IsFalse(serialNumber.IsValid);
        }

        private static CertificateLedgerEntry CreateEntry(
            string serial,
            string productCode,
            Guid requestId,
            byte[] csrHash,
            byte[] payloadHash)
        {
            CertificateSerialNumber serialNumber;
            Assert.IsTrue(CertificateSerialNumber.TryCreate(serial, out serialNumber));
            ServiceEndpointIdentity identity;
            EndpointIdentityValidationError error;
            Assert.IsTrue(ServiceEndpointIdentity.TryCreate(
                "vms-bridge.example.local",
                "10.20.30.40",
                out identity,
                out error));
            return CertificateLedgerEntry.CreateIssued(
                serialNumber,
                TestData.ProductCode(productCode),
                requestId,
                CertificateIssuanceKind.Registration,
                identity,
                csrHash,
                payloadHash,
                Hash(0x33),
                Hash(0x44),
                TestData.Utc(1),
                TestData.Utc(0),
                TestData.Utc(3));
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
