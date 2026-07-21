using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PeerPkiCacheCodecTests
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly Guid IssuerInstanceId = new Guid(
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

        [TestMethod]
        public void CodecWritesExactCanonicalBytesAndRoundTrips()
        {
            byte[] crlHash = Hash(0x33);
            byte[] leafHash = Hash(0x11);
            var snapshot = new PeerPkiCacheSnapshot(
                IssuerInstanceId,
                7,
                3,
                crlHash,
                new[]
                {
                    Certificate(
                        "CD34",
                        "02A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                        Hash(0x22),
                        Utc(2)),
                    Certificate(
                        "AB12",
                        "01A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                        leafHash,
                        Utc(1))
                });
            crlHash[0] = 0xff;
            leafHash[0] = 0xff;
            var codec = new PeerPkiCacheCodec();

            byte[] encoded = codec.Serialize(snapshot);
            string expected =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
                + "<PeerPkiCache SchemaVersion=\"1\">\r\n"
                + "  <IssuerInstanceId>aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee</IssuerInstanceId>\r\n"
                + "  <PkiRevision>7</PkiRevision>\r\n"
                + "  <CrlNumber>3</CrlNumber>\r\n"
                + "  <CrlSha256>" + Convert.ToBase64String(Hash(0x33))
                + "</CrlSha256>\r\n"
                + "  <ActiveCertificates>\r\n"
                + CertificateXml(
                    "AB12",
                    "01A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                    Hash(0x11),
                    Utc(1))
                + CertificateXml(
                    "CD34",
                    "02A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                    Hash(0x22),
                    Utc(2))
                + "  </ActiveCertificates>\r\n"
                + "</PeerPkiCache>\r\n";
            CollectionAssert.AreEqual(
                StrictUtf8.GetBytes(expected),
                encoded);

            PeerPkiCacheSnapshot actual = codec.Deserialize(encoded);
            Assert.AreEqual(IssuerInstanceId, actual.IssuerInstanceId);
            Assert.AreEqual(7UL, actual.PkiRevision);
            Assert.AreEqual(3UL, actual.CrlNumber);
            CollectionAssert.AreEqual(Hash(0x33), actual.GetCrlSha256());
            Assert.AreEqual(2, actual.ActiveCertificates.Count);
            Assert.AreEqual(
                "AB12",
                actual.ActiveCertificates[0].ProductCode.Value);
            CollectionAssert.AreEqual(
                Hash(0x11),
                actual.ActiveCertificates[0].GetLeafSha256());
        }

        [TestMethod]
        public void SnapshotRejectsDuplicateProductCodeAndSerialNumber()
        {
            PeerPkiCacheCertificate first = Certificate(
                "AB12",
                "01A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                Hash(0x11),
                Utc(1));

            Assert.ThrowsExactly<ArgumentException>(() =>
                Snapshot(
                    first,
                    Certificate(
                        "AB12",
                        "02A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                        Hash(0x22),
                        Utc(2))));
            Assert.ThrowsExactly<ArgumentException>(() =>
                Snapshot(
                    first,
                    Certificate(
                        "CD34",
                        "01A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                        Hash(0x22),
                        Utc(2))));
        }

        [TestMethod]
        public void CodecRejectsNoncanonicalAndUnknownContent()
        {
            var codec = new PeerPkiCacheCodec();
            byte[] canonical = codec.Serialize(
                Snapshot(
                    Certificate(
                        "AB12",
                        "01A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                        Hash(0x11),
                        Utc(1))));
            string xml = StrictUtf8.GetString(canonical);

            AssertInvalid(codec, xml.Replace("\r\n", "\n"));
            AssertInvalid(
                codec,
                xml.Replace(
                    IssuerInstanceId.ToString("D"),
                    IssuerInstanceId.ToString("D").ToUpperInvariant()));
            AssertInvalid(
                codec,
                xml.Replace(
                    "  <PkiRevision>7</PkiRevision>\r\n",
                    "  <Unknown>1</Unknown>\r\n"
                    + "  <PkiRevision>7</PkiRevision>\r\n"));
            AssertInvalid(
                codec,
                xml.Replace(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                    + "<!DOCTYPE PeerPkiCache [<!ENTITY x \"x\">]>"));
        }

        [TestMethod]
        public void CodecRejectsMoreThanOneThousandCertificates()
        {
            string certificate = CertificateXml(
                "AB12",
                "01A4A5A6A7A8A9AAABACADAEAFB0B1B2",
                Hash(0x11),
                Utc(1));
            var xml = new StringBuilder(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
                + "<PeerPkiCache SchemaVersion=\"1\">\r\n"
                + "  <IssuerInstanceId>aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee</IssuerInstanceId>\r\n"
                + "  <PkiRevision>7</PkiRevision>\r\n"
                + "  <CrlNumber>3</CrlNumber>\r\n"
                + "  <CrlSha256>"
                + Convert.ToBase64String(Hash(0x33))
                + "</CrlSha256>\r\n"
                + "  <ActiveCertificates>\r\n");
            for (int index = 0;
                index <= PeerPkiCacheSnapshot.MaximumActiveCertificateCount;
                index++)
            {
                xml.Append(certificate);
            }

            xml.Append("  </ActiveCertificates>\r\n</PeerPkiCache>\r\n");

            Assert.ThrowsExactly<InvalidDataException>(() =>
                new PeerPkiCacheCodec().Deserialize(
                    StrictUtf8.GetBytes(xml.ToString())));
        }

        private static PeerPkiCacheSnapshot Snapshot(
            params PeerPkiCacheCertificate[] certificates)
        {
            return new PeerPkiCacheSnapshot(
                IssuerInstanceId,
                7,
                3,
                Hash(0x33),
                new List<PeerPkiCacheCertificate>(certificates));
        }

        private static PeerPkiCacheCertificate Certificate(
            string productCode,
            string serial,
            byte[] leafHash,
            DateTime notAfterUtc)
        {
            CertificateSerialNumber serialNumber;
            Assert.IsTrue(CertificateSerialNumber.TryCreate(
                serial,
                out serialNumber));
            return new PeerPkiCacheCertificate(
                TestData.ProductCode(productCode),
                serialNumber,
                leafHash,
                notAfterUtc);
        }

        private static string CertificateXml(
            string productCode,
            string serial,
            byte[] leafHash,
            DateTime notAfterUtc)
        {
            return "    <Certificate>\r\n"
                + "      <ProductCode>" + productCode
                + "</ProductCode>\r\n"
                + "      <SerialNumber>" + serial
                + "</SerialNumber>\r\n"
                + "      <LeafSha256>"
                + Convert.ToBase64String(leafHash)
                + "</LeafSha256>\r\n"
                + "      <NotAfterUtc>"
                + notAfterUtc.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture)
                + "</NotAfterUtc>\r\n"
                + "    </Certificate>\r\n";
        }

        private static void AssertInvalid(
            PeerPkiCacheCodec codec,
            string xml)
        {
            Assert.ThrowsExactly<InvalidDataException>(() =>
                codec.Deserialize(StrictUtf8.GetBytes(xml)));
        }

        private static DateTime Utc(int day)
        {
            return new DateTime(
                2027,
                7,
                day,
                0,
                0,
                0,
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
