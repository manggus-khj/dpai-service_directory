using System;
using System.Text;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.ExternalProtocol
{
    [TestClass]
    public sealed class ExternalXmlCodecTests
    {
        private const string Namespace =
            "urn:deepai:service-directory:external";
        private const string RegistrationRequestId =
            "7f35b4b8-854d-4ca1-90bc-da196772f49f";
        private const string RenewalRequestId =
            "1be2b548-ad43-44ac-b97f-75e038175d53";
        private const string SerialNumber =
            "01A4B5C6D7E8F90123456789ABCDEF01";

        [TestMethod]
        public void RegistrationParsesAndNormalizesTargetContract()
        {
            byte[] csr = { 1, 2, 3, 4 };
            ExternalRegistrationRequest request =
                ExternalXmlCodec.ParseRegistrationRequest(
                    RegistrationXml(
                        "  VMS Bridge  ",
                        " abcd ",
                        " VMS-Bridge.Example.Local ",
                        " 10.20.30.40 ",
                        Convert.ToBase64String(csr)));

            Assert.AreEqual(
                Guid.Parse(RegistrationRequestId),
                request.RegistrationRequestId);
            Assert.AreEqual("VMS Bridge", request.Name);
            Assert.AreEqual("ABCD", request.ProductCode);
            Assert.AreEqual(
                "vms-bridge.example.local",
                request.ServiceHostName);
            Assert.AreEqual("10.20.30.40", request.ServiceIpv4Address);
            Assert.AreEqual(21500, request.Port);
            CollectionAssert.AreEqual(csr, request.CertificateSigningRequest);
        }

        [TestMethod]
        public void RegistrationRejectsUnknownDuplicateAndPartialIdentityXml()
        {
            string valid = RegistrationXmlText(
                "VMS Bridge",
                "ABCD",
                "vms-bridge.example.local",
                "10.20.30.40",
                "AQIDBA==");
            string[] invalidBodies =
            {
                valid.Replace(
                    "<Name>",
                    "<Unknown>x</Unknown><Name>"),
                valid.Replace(
                    "<ServiceIpv4Address>10.20.30.40</ServiceIpv4Address>",
                    string.Empty),
                valid.Replace(
                    "<ServiceHostName>",
                    "<ServiceHostName flag=\"1\">"),
                valid.Replace(
                    "</ServiceHostName>",
                    "</ServiceHostName><ServiceHostName>other.local</ServiceHostName>")
            };

            foreach (string body in invalidBodies)
            {
                Assert.ThrowsExactly<ExternalProtocolException>(
                    () => ExternalXmlCodec.ParseRegistrationRequest(
                        Utf8(body)));
            }
        }

        [TestMethod]
        [DataRow("service.internal.", "10.20.30.40")]
        [DataRow("https://service.internal", "10.20.30.40")]
        [DataRow("2001:db8::1", "10.20.30.40")]
        [DataRow("[2001:db8::1]", "10.20.30.40")]
        [DataRow("service.internal", "010.20.30.40")]
        [DataRow("service.internal", "2001:db8::1")]
        [DataRow("service.internal", "::ffff:10.20.30.40")]
        [DataRow("service.internal", "127.0.0.1")]
        [DataRow("service.internal", "169.254.1.1")]
        [DataRow("service.internal", "224.0.0.1")]
        [DataRow("service.internal", "0.0.0.0")]
        [DataRow("service.internal", "255.255.255.255")]
        public void RegistrationRejectsInvalidEndpointIdentity(
            string serviceHostName,
            string serviceIpv4Address)
        {
            Assert.ThrowsExactly<ExternalProtocolException>(
                () => ExternalXmlCodec.ParseRegistrationRequest(
                    RegistrationXml(
                        "VMS Bridge",
                        "ABCD",
                        serviceHostName,
                        serviceIpv4Address,
                        "AQIDBA==")));
        }

        [TestMethod]
        public void RegistrationRejectsNonCanonicalIdentifiersAndCsrBase64()
        {
            string valid = RegistrationXmlText(
                "VMS Bridge",
                "ABCD",
                "service.internal",
                "10.20.30.40",
                "AQIDBA==");
            string[] invalidBodies =
            {
                valid.Replace(
                    RegistrationRequestId,
                    RegistrationRequestId.ToUpperInvariant()),
                valid.Replace(RegistrationRequestId, Guid.Empty.ToString("D")),
                valid.Replace("AQIDBA==", "AQID BA=="),
                valid.Replace("AQIDBA==", "-----BEGIN CERTIFICATE REQUEST-----")
            };

            foreach (string body in invalidBodies)
            {
                Assert.ThrowsExactly<ExternalProtocolException>(
                    () => ExternalXmlCodec.ParseRegistrationRequest(
                        Utf8(body)));
            }
        }

        [TestMethod]
        public void RenewalParsesProofAndCompleteReplacementIdentity()
        {
            byte[] nonce = Sequence(16, 1);
            byte[] csr = Sequence(64, 20);
            byte[] identityHash = Sequence(32, 40);
            byte[] signature = Sequence(72, 60);

            ExternalCertificateRenewalRequest request =
                ExternalXmlCodec.ParseCertificateRenewalRequest(
                    RenewalXml(
                        Convert.ToBase64String(nonce),
                        Convert.ToBase64String(csr),
                        Convert.ToBase64String(identityHash),
                        Convert.ToBase64String(signature)));

            Assert.AreEqual(
                Guid.Parse(RenewalRequestId),
                request.RenewalRequestId);
            Assert.AreEqual("ABCD", request.ProductCode);
            Assert.AreEqual(SerialNumber, request.CurrentSerialNumber);
            Assert.AreEqual(
                new DateTime(2026, 6, 19, 2, 0, 0, DateTimeKind.Utc),
                request.TimestampUtc);
            Assert.AreEqual("VMS Bridge", request.Name);
            Assert.AreEqual("service.internal", request.ServiceHostName);
            Assert.AreEqual("10.20.30.41", request.ServiceIpv4Address);
            Assert.AreEqual(21500, request.Port);
            CollectionAssert.AreEqual(nonce, request.Nonce);
            CollectionAssert.AreEqual(csr, request.CertificateSigningRequest);
            CollectionAssert.AreEqual(
                identityHash,
                request.ServiceIdentitySha256);
            CollectionAssert.AreEqual(signature, request.ProofSignature);
        }

        [TestMethod]
        public void RenewalRejectsNonCanonicalProofFields()
        {
            string valid = RenewalXmlText(
                Convert.ToBase64String(Sequence(16, 1)),
                "AQIDBA==",
                Convert.ToBase64String(Sequence(32, 40)),
                "AQIDBA==");
            string[] invalidBodies =
            {
                valid.Replace(SerialNumber, SerialNumber.ToLowerInvariant()),
                valid.Replace("02:00:00.000Z", "02:00:00Z"),
                valid.Replace(
                    Convert.ToBase64String(Sequence(16, 1)),
                    "AQIDBA=="),
                valid.Replace(
                    Convert.ToBase64String(Sequence(32, 40)),
                    "AQIDBA=="),
                valid.Replace(
                    "<ProofSignature>AQIDBA==</ProofSignature>",
                    "<ProofSignature />")
            };

            foreach (string body in invalidBodies)
            {
                Assert.ThrowsExactly<ExternalProtocolException>(
                    () => ExternalXmlCodec.ParseCertificateRenewalRequest(
                        Utf8(body)));
            }
        }

        [TestMethod]
        public void RequestReadersRejectUnsafeXmlAndBodyBoundaries()
        {
            Assert.ThrowsExactly<ArgumentNullException>(
                () => ExternalXmlCodec.ParseRegistrationRequest(null));
            Assert.ThrowsExactly<ExternalProtocolException>(
                () => ExternalXmlCodec.ParseRegistrationRequest(
                    new byte[0]));
            Assert.ThrowsExactly<ExternalProtocolException>(
                () => ExternalXmlCodec.ParseRegistrationRequest(
                    new byte[
                        ExternalApiContract.MaximumCertificateRequestBodyBytes
                        + 1]));
            Assert.ThrowsExactly<ExternalProtocolException>(
                () => ExternalXmlCodec.ParseRegistrationRequest(
                    new byte[] { 0xc3, 0x28 }));
            Assert.ThrowsExactly<ExternalProtocolException>(
                () => ExternalXmlCodec.ParseRegistrationRequest(
                    Utf8(
                        "<!DOCTYPE RegistrationRequest [<!ENTITY x 'y'>]>"
                        + "<RegistrationRequest xmlns=\""
                        + Namespace
                        + "\"><RegistrationRequestId>"
                        + RegistrationRequestId
                        + "</RegistrationRequestId><Name>&x;</Name>"
                        + "<ProductCode>ABCD</ProductCode>"
                        + "<ServiceHostName>service.internal</ServiceHostName>"
                        + "<ServiceIpv4Address>10.20.30.40</ServiceIpv4Address>"
                        + "<Port>21500</Port>"
                        + "<CertificateSigningRequest>AQIDBA==</CertificateSigningRequest>"
                        + "</RegistrationRequest>")));
        }

        [TestMethod]
        public void ServiceResponseUsesCanonicalDnsAndIpv4Pair()
        {
            var service = CreateService();
            byte[] body = ExternalXmlCodec.SerializeServiceResponse(
                ExternalResponse.CreateServiceSuccess(service));
            string xml = Decode(body);

            StringAssert.Contains(
                xml,
                "<ServiceHostName>service.internal</ServiceHostName>");
            StringAssert.Contains(
                xml,
                "<ServiceIpv4Address>10.20.30.40</ServiceIpv4Address>");
            Assert.DoesNotContain("ServerAddress", xml);
            StringAssert.Contains(
                xml,
                "<LastModifiedUtc>2026-07-19T02:00:00Z</LastModifiedUtc>");
        }

        [TestMethod]
        public void TrustInfoResponseUsesPinnedCaContract()
        {
            var trustInfo = new ExternalTrustInfo(
                Guid.Parse("3d8ff138-4e9a-4e52-b108-e3af248b1787"),
                new byte[] { 1, 2, 3 },
                Sequence(32, 10),
                ExternalApiContract.CrlPath);

            string xml = Decode(
                ExternalXmlCodec.SerializeTrustInfoResponse(
                    ExternalResponse.CreateTrustInfoSuccess(trustInfo)));

            StringAssert.Contains(xml, "<TrustInfo>");
            StringAssert.Contains(
                xml,
                "<SiteId>3d8ff138-4e9a-4e52-b108-e3af248b1787</SiteId>");
            StringAssert.Contains(xml, "<CaCertificate>AQID</CaCertificate>");
            StringAssert.Contains(xml, "<CrlUri>/pki/crl</CrlUri>");
        }

        [TestMethod]
        [DataRow(ExternalCertificateIssuanceStatus.Registered, "REGISTERED")]
        [DataRow(ExternalCertificateIssuanceStatus.Reregistered, "REREGISTERED")]
        [DataRow(ExternalCertificateIssuanceStatus.Replayed, "REPLAYED")]
        public void RegistrationResponseContainsCertificateResult(
            ExternalCertificateIssuanceStatus status,
            string expectedStatus)
        {
            ExternalResponse response =
                ExternalResponse.CreateRegistrationSuccess(
                    status,
                    Guid.Parse(RegistrationRequestId),
                    CreateService(),
                    CreateCertificate());

            string xml = Decode(
                ExternalXmlCodec.SerializeRegistrationResponse(response));

            StringAssert.Contains(xml, "<Status>" + expectedStatus + "</Status>");
            StringAssert.Contains(
                xml,
                "<RegistrationRequestId>"
                + RegistrationRequestId
                + "</RegistrationRequestId>");
            StringAssert.Contains(xml, "<Certificate>");
            StringAssert.Contains(
                xml,
                "<SerialNumber>" + SerialNumber + "</SerialNumber>");
            Assert.DoesNotContain("PendingId", xml);
        }

        [TestMethod]
        public void RenewalResponseUsesRenewalIdAndRenewedStatus()
        {
            ExternalResponse response = ExternalResponse.CreateRenewalSuccess(
                Guid.Parse(RenewalRequestId),
                CreateService(),
                CreateCertificate());

            string xml = Decode(
                ExternalXmlCodec.SerializeCertificateRenewalResponse(response));

            StringAssert.Contains(xml, "<Status>RENEWED</Status>");
            StringAssert.Contains(
                xml,
                "<RenewalRequestId>"
                + RenewalRequestId
                + "</RenewalRequestId>");
            Assert.DoesNotContain("RegistrationRequestId", xml);
        }

        [TestMethod]
        public void HealthAndAllSafeErrorsSerializeAgainstTargetSchema()
        {
            string health = Decode(
                ExternalXmlCodec.SerializeHealthResponse(
                    ExternalResponse.CreateHealthSuccess(
                        new DateTime(
                            2026,
                            7,
                            19,
                            2,
                            0,
                            0,
                            DateTimeKind.Utc))));
            StringAssert.Contains(
                health,
                "<UtcNow>2026-07-19T02:00:00Z</UtcNow>");

            ExternalResponseCode[] errors =
            {
                ExternalResponseCode.BadRequest,
                ExternalResponseCode.NotFound,
                ExternalResponseCode.Conflict,
                ExternalResponseCode.InvalidApiKey,
                ExternalResponseCode.LimitExceeded,
                ExternalResponseCode.RegistrationModeClosed,
                ExternalResponseCode.CertificateRequestInvalid,
                ExternalResponseCode.CertificateNotRenewable,
                ExternalResponseCode.Internal
            };
            foreach (ExternalResponseCode code in errors)
            {
                string xml = Decode(
                    ExternalXmlCodec.SerializeErrorResponse(
                        ExternalResponse.CreateError(code)));
                StringAssert.Contains(
                    xml,
                    "<Code>" + ((int)code).ToString() + "</Code>");
                Assert.DoesNotContain("System.", xml);
            }
        }

        [TestMethod]
        public void ResponseModelsRejectPartialOrNonCanonicalPayloads()
        {
            DateTime utc = new DateTime(
                2026,
                7,
                19,
                2,
                0,
                0,
                DateTimeKind.Utc);
            Assert.ThrowsExactly<ArgumentException>(
                () => new ExternalServiceItem(
                    "Service",
                    "ABCD",
                    "service.internal",
                    null,
                    21500,
                    utc));
            Assert.ThrowsExactly<ArgumentException>(
                () => new ExternalTrustInfo(
                    Guid.NewGuid(),
                    new byte[] { 1 },
                    Sequence(32, 1),
                    "https://service.internal/pki/crl"));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => ExternalResponse.CreateRegistrationSuccess(
                    ExternalCertificateIssuanceStatus.Renewed,
                    Guid.NewGuid(),
                    CreateService(),
                    CreateCertificate()));
        }

        [TestMethod]
        public void BinaryModelPropertiesReturnDefensiveCopies()
        {
            byte[] ca = { 1, 2, 3 };
            byte[] hash = Sequence(32, 1);
            var trustInfo = new ExternalTrustInfo(
                Guid.NewGuid(),
                ca,
                hash,
                ExternalApiContract.CrlPath);

            ca[0] = 99;
            hash[0] = 99;
            byte[] returnedCa = trustInfo.CaCertificate;
            byte[] returnedHash = trustInfo.CaSpkiSha256;
            returnedCa[0] = 88;
            returnedHash[0] = 88;

            Assert.AreEqual(1, trustInfo.CaCertificate[0]);
            Assert.AreEqual(1, trustInfo.CaSpkiSha256[0]);
        }

        private static ExternalServiceItem CreateService()
        {
            return new ExternalServiceItem(
                "Service",
                "ABCD",
                "service.internal",
                "10.20.30.40",
                21500,
                new DateTime(
                    2026,
                    7,
                    19,
                    2,
                    0,
                    0,
                    DateTimeKind.Utc));
        }

        private static ExternalIssuedCertificate CreateCertificate()
        {
            return new ExternalIssuedCertificate(
                new byte[] { 1, 2, 3 },
                new byte[] { 4, 5, 6 },
                SerialNumber,
                new DateTime(
                    2026,
                    7,
                    19,
                    1,
                    55,
                    0,
                    DateTimeKind.Utc),
                new DateTime(
                    2027,
                    7,
                    19,
                    2,
                    0,
                    0,
                    DateTimeKind.Utc),
                ExternalApiContract.CrlPath);
        }

        private static byte[] RegistrationXml(
            string name,
            string productCode,
            string serviceHostName,
            string serviceIpv4Address,
            string csr)
        {
            return Utf8(
                RegistrationXmlText(
                    name,
                    productCode,
                    serviceHostName,
                    serviceIpv4Address,
                    csr));
        }

        private static string RegistrationXmlText(
            string name,
            string productCode,
            string serviceHostName,
            string serviceIpv4Address,
            string csr)
        {
            return "<RegistrationRequest xmlns=\""
                + Namespace
                + "\"><RegistrationRequestId>"
                + RegistrationRequestId
                + "</RegistrationRequestId><Name>"
                + name
                + "</Name><ProductCode>"
                + productCode
                + "</ProductCode><ServiceHostName>"
                + serviceHostName
                + "</ServiceHostName><ServiceIpv4Address>"
                + serviceIpv4Address
                + "</ServiceIpv4Address><Port>21500</Port>"
                + "<CertificateSigningRequest>"
                + csr
                + "</CertificateSigningRequest></RegistrationRequest>";
        }

        private static byte[] RenewalXml(
            string nonce,
            string csr,
            string identityHash,
            string signature)
        {
            return Utf8(RenewalXmlText(nonce, csr, identityHash, signature));
        }

        private static string RenewalXmlText(
            string nonce,
            string csr,
            string identityHash,
            string signature)
        {
            return "<CertificateRenewalRequest xmlns=\""
                + Namespace
                + "\"><RenewalRequestId>"
                + RenewalRequestId
                + "</RenewalRequestId><ProductCode>ABCD</ProductCode>"
                + "<CurrentSerialNumber>"
                + SerialNumber
                + "</CurrentSerialNumber>"
                + "<TimestampUtc>2026-06-19T02:00:00.000Z</TimestampUtc>"
                + "<Nonce>"
                + nonce
                + "</Nonce><Name>VMS Bridge</Name>"
                + "<ServiceHostName>service.internal</ServiceHostName>"
                + "<ServiceIpv4Address>10.20.30.41</ServiceIpv4Address>"
                + "<Port>21500</Port><CertificateSigningRequest>"
                + csr
                + "</CertificateSigningRequest><ServiceIdentitySha256>"
                + identityHash
                + "</ServiceIdentitySha256><ProofSignature>"
                + signature
                + "</ProofSignature></CertificateRenewalRequest>";
        }

        private static byte[] Sequence(int length, int seed)
        {
            var value = new byte[length];
            for (int index = 0; index < value.Length; index++)
            {
                value[index] = (byte)(seed + index);
            }

            return value;
        }

        private static byte[] Utf8(string value)
        {
            return new UTF8Encoding(false, true).GetBytes(value);
        }

        private static string Decode(byte[] value)
        {
            return new UTF8Encoding(false, true).GetString(value);
        }
    }
}
