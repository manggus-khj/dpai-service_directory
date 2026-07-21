using System;
using System.IO;
using System.Text;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class CertificateAuthorityStateCodecTests
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly Guid SiteId = new Guid(
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        private static readonly Guid IssuerInstanceId = new Guid(
            "11111111-2222-4333-8444-555555555555");

        [TestMethod]
        public void StateCodecWritesExactCanonicalBytesAndRoundTrips()
        {
            var codec = new CertificateAuthorityStateCodec();
            CertificateAuthorityState expected = CreateState();

            byte[] encoded = codec.SerializeState(expected);
            string expectedXml =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
                + "<CertificateAuthorityState SchemaVersion=\"1\">\r\n"
                + "  <SiteId>aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee</SiteId>\r\n"
                + "  <IssuerInstanceId>11111111-2222-4333-8444-555555555555</IssuerInstanceId>\r\n"
                + "  <Role>ACTIVE_ISSUER</Role>\r\n"
                + "  <CaSerialNumber>01A4A5A6A7A8A9AAABACADAEAFB0B1B2</CaSerialNumber>\r\n"
                + "  <CaSpkiSha256>"
                + Convert.ToBase64String(Hash(0x11))
                + "</CaSpkiSha256>\r\n"
                + "  <NotBeforeUtc>2026-07-20T00:00:00.0000000Z</NotBeforeUtc>\r\n"
                + "  <NotAfterUtc>2046-07-20T00:00:00.0000000Z</NotAfterUtc>\r\n"
                + "  <PkiRevision>7</PkiRevision>\r\n"
                + "  <CrlNumber>3</CrlNumber>\r\n"
                + "</CertificateAuthorityState>\r\n";
            CollectionAssert.AreEqual(
                StrictUtf8.GetBytes(expectedXml),
                encoded);

            CertificateAuthorityState actual = codec.DeserializeState(
                encoded);
            Assert.AreEqual(SiteId, actual.SiteId);
            Assert.AreEqual(IssuerInstanceId, actual.IssuerInstanceId);
            Assert.AreEqual(
                CertificateAuthorityRole.ActiveIssuer,
                actual.Role);
            Assert.AreEqual(7UL, actual.PkiRevision);
            Assert.AreEqual(3UL, actual.CrlNumber);
            CollectionAssert.AreEqual(
                Hash(0x11),
                actual.GetCaSpkiSha256());
        }

        [TestMethod]
        public void StateHighWaterValuesIncreaseIndependently()
        {
            CertificateAuthorityState state = CreateState();

            CertificateAuthorityState pkiAdvanced =
                state.WithHighWater(8, 3);
            CertificateAuthorityState crlAdvanced =
                state.WithHighWater(7, 4);

            Assert.AreEqual(8UL, pkiAdvanced.PkiRevision);
            Assert.AreEqual(3UL, pkiAdvanced.CrlNumber);
            Assert.AreEqual(7UL, crlAdvanced.PkiRevision);
            Assert.AreEqual(4UL, crlAdvanced.CrlNumber);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                state.WithHighWater(7, 3));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                state.WithHighWater(6, 4));
        }

        [TestMethod]
        public void LedgerCodecWritesExactLeafDerAndServiceDefinition()
        {
            var codec = new CertificateAuthorityStateCodec();
            CertificateLedgerSnapshot expected = new CertificateLedgerSnapshot(
                new[] { CreateEntry() },
                7,
                3);

            byte[] encoded = codec.SerializeLedger(expected);
            string expectedXml =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
                + "<CertificateLedger SchemaVersion=\"1\" PkiRevision=\"7\" CrlNumber=\"3\">\r\n"
                + "  <Certificate>\r\n"
                + "    <SerialNumber>02A4A5A6A7A8A9AAABACADAEAFB0B1B2</SerialNumber>\r\n"
                + "    <ProductCode>AB12</ProductCode>\r\n"
                + "    <IssuanceRequestId>77777777-8888-4999-aaaa-bbbbbbbbbbbb</IssuanceRequestId>\r\n"
                + "    <IssuanceKind>REGISTRATION</IssuanceKind>\r\n"
                + "    <Name>VMS Bridge</Name>\r\n"
                + "    <ServiceHostName>vms-bridge.example.local</ServiceHostName>\r\n"
                + "    <ServiceIpv4Address>10.20.30.40</ServiceIpv4Address>\r\n"
                + "    <Port>21500</Port>\r\n"
                + "    <CsrSha256>" + Convert.ToBase64String(Hash(0x21))
                + "</CsrSha256>\r\n"
                + "    <RequestPayloadSha256>"
                + Convert.ToBase64String(Hash(0x22))
                + "</RequestPayloadSha256>\r\n"
                + "    <SubjectPublicKeyInfoSha256>"
                + Convert.ToBase64String(Hash(0x23))
                + "</SubjectPublicKeyInfoSha256>\r\n"
                + "    <LeafCertificate>MAEA</LeafCertificate>\r\n"
                + "    <IssuedUtc>2026-07-20T01:02:03.0000000Z</IssuedUtc>\r\n"
                + "    <NotBeforeUtc>2026-07-20T00:57:03.0000000Z</NotBeforeUtc>\r\n"
                + "    <NotAfterUtc>2027-07-20T01:02:03.0000000Z</NotAfterUtc>\r\n"
                + "    <Status>CURRENT</Status>\r\n"
                + "  </Certificate>\r\n"
                + "</CertificateLedger>\r\n";
            CollectionAssert.AreEqual(
                StrictUtf8.GetBytes(expectedXml),
                encoded);

            CertificateLedgerSnapshot actual = codec.DeserializeLedger(
                encoded);
            CertificateLedgerEntry entry;
            Assert.IsTrue(actual.TryGetByRequestId(
                new Guid("77777777-8888-4999-aaaa-bbbbbbbbbbbb"),
                out entry));
            Assert.AreEqual("VMS Bridge", entry.ServiceDefinition.Name);
            Assert.AreEqual(21500, entry.ServiceDefinition.Port);
            CollectionAssert.AreEqual(
                new byte[] { 0x30, 0x01, 0x00 },
                entry.GetLeafCertificate());
        }

        [TestMethod]
        public void PkiCodecsRejectNoncanonicalAndUnknownContent()
        {
            var codec = new CertificateAuthorityStateCodec();
            string stateXml = StrictUtf8.GetString(
                codec.SerializeState(CreateState()));
            string ledgerXml = StrictUtf8.GetString(
                codec.SerializeLedger(
                    new CertificateLedgerSnapshot(
                        new[] { CreateEntry() },
                        7,
                        3)));

            AssertInvalidState(codec, stateXml.Replace("\r\n", "\n"));
            AssertInvalidState(
                codec,
                stateXml.Replace(
                    SiteId.ToString("D"),
                    SiteId.ToString("D").ToUpperInvariant()));
            AssertInvalidLedger(
                codec,
                ledgerXml.Replace(
                    "    <Status>CURRENT</Status>\r\n",
                    "    <Unknown>value</Unknown>\r\n"
                    + "    <Status>CURRENT</Status>\r\n"));
            AssertInvalidLedger(
                codec,
                ledgerXml.Replace(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                    + "<!DOCTYPE CertificateLedger [<!ENTITY x \"x\">]>"));
        }

        private static CertificateAuthorityState CreateState()
        {
            CertificateSerialNumber serialNumber;
            Assert.IsTrue(CertificateSerialNumber.TryCreate(
                "01A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                out serialNumber));
            return new CertificateAuthorityState(
                SiteId,
                IssuerInstanceId,
                CertificateAuthorityRole.ActiveIssuer,
                serialNumber,
                Hash(0x11),
                Utc(2026, 7, 20, 0, 0, 0),
                Utc(2046, 7, 20, 0, 0, 0),
                7,
                3,
                null);
        }

        private static CertificateLedgerEntry CreateEntry()
        {
            CertificateSerialNumber serialNumber;
            Assert.IsTrue(CertificateSerialNumber.TryCreate(
                "02A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                out serialNumber));
            return CertificateLedgerEntry.CreateIssued(
                serialNumber,
                TestData.Definition(
                    "VMS Bridge",
                    "AB12",
                    "vms-bridge.example.local",
                    "10.20.30.40",
                    21500),
                new Guid("77777777-8888-4999-aaaa-bbbbbbbbbbbb"),
                CertificateIssuanceKind.Registration,
                Hash(0x21),
                Hash(0x22),
                Hash(0x23),
                new byte[] { 0x30, 0x01, 0x00 },
                Utc(2026, 7, 20, 1, 2, 3),
                Utc(2026, 7, 20, 0, 57, 3),
                Utc(2027, 7, 20, 1, 2, 3));
        }

        private static void AssertInvalidState(
            CertificateAuthorityStateCodec codec,
            string xml)
        {
            Assert.ThrowsExactly<InvalidDataException>(() =>
                codec.DeserializeState(StrictUtf8.GetBytes(xml)));
        }

        private static void AssertInvalidLedger(
            CertificateAuthorityStateCodec codec,
            string xml)
        {
            Assert.ThrowsExactly<InvalidDataException>(() =>
                codec.DeserializeLedger(StrictUtf8.GetBytes(xml)));
        }

        private static DateTime Utc(
            int year,
            int month,
            int day,
            int hour,
            int minute,
            int second)
        {
            return new DateTime(
                year,
                month,
                day,
                hour,
                minute,
                second,
                DateTimeKind.Utc);
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
