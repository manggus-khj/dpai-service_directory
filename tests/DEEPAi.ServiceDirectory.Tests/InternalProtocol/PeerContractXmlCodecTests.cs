using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.InternalProtocol
{
    [TestClass]
    public sealed class PeerContractXmlCodecTests
    {
        private const string XmlNamespace =
            "urn:deepai:service-directory:peer";
        private const string LocalInstanceId =
            "7a1c3bb2-9e8b-4a8d-b404-f670f746eb77";
        private const string IssuerInstanceId =
            "9f2ed127-9834-42b4-a379-eaad9df8fcec";
        private const string OtherIssuerInstanceId =
            "6f248a04-cc3e-409a-b499-cb571e6d30b7";
        private const string SerialNumber =
            "01A4B5C6D7E8F90123456789ABCDEF01";

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void TargetPeerSchemaHasFixedResourceName()
        {
            string[] names = typeof(PeerContractXmlCodec).Assembly
                .GetManifestResourceNames();
            CollectionAssert.Contains(
                names,
                "DEEPAi.ServiceDirectory.InternalProtocol.Peer.peer.xsd");
        }

        [TestMethod]
        public void ServiceRecordRoundTripsCanonicalDnsAndIpv4Pair()
        {
            PeerServiceRecord source = CreateServiceRecord();

            byte[] body = PeerContractXmlCodec.SerializeServiceRecord(source);
            string xml = StrictUtf8.GetString(body);
            PeerServiceRecord parsed = PeerContractXmlCodec
                .ParseServiceRecord(body);

            StringAssert.Contains(
                xml,
                "<ServiceHostName>service.internal</ServiceHostName>");
            StringAssert.Contains(
                xml,
                "<ServiceIpv4Address>10.0.0.5</ServiceIpv4Address>");
            Assert.IsFalse(xml.Contains("ServerAddress"));
            Assert.AreEqual("service.internal", parsed.ServiceHostName);
            Assert.AreEqual("10.0.0.5", parsed.ServiceIpv4Address);
            Assert.AreEqual(8UL, parsed.LogicalVersion);
        }

        [TestMethod]
        public void ServiceRecordRejectsPartialNonCanonicalAndIpv6Identity()
        {
            string canonical = StrictUtf8.GetString(
                PeerContractXmlCodec.SerializeServiceRecord(
                    CreateServiceRecord()));

            AssertInvalidService(
                canonical.Replace(
                    "<ServiceIpv4Address>10.0.0.5</ServiceIpv4Address>",
                    string.Empty));
            AssertInvalidService(
                canonical.Replace(
                    "service.internal",
                    "Service.Internal"));
            AssertInvalidService(
                canonical.Replace("10.0.0.5", "10.00.0.5"));
            AssertInvalidService(
                canonical.Replace("10.0.0.5", "2001:db8::1"));
        }

        [TestMethod]
        public void ServiceRecordRejectsDirectoryIdentityAndLegacyAddressFields()
        {
            string canonical = StrictUtf8.GetString(
                PeerContractXmlCodec.SerializeServiceRecord(
                    CreateServiceRecord()));
            AssertInvalidService(
                canonical
                    .Replace("ServiceHostName", "DirectoryHostName")
                    .Replace(
                        "ServiceIpv4Address",
                        "DirectoryIpv4Address"));
            AssertInvalidService(
                canonical.Replace(
                    "<ServiceHostName>service.internal</ServiceHostName>"
                        + "<ServiceIpv4Address>10.0.0.5</ServiceIpv4Address>",
                    "<ServerAddress>service.internal</ServerAddress>"));
        }

        [TestMethod]
        public void ServiceRecordModelRejectsNonCanonicalOrMissingPair()
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateServiceRecord("Service.Internal", "10.0.0.5"));
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateServiceRecord("service.internal", "10.00.0.5"));
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateServiceRecord("service.internal", "::1"));
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateServiceRecord(null, "10.0.0.5"));
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateServiceRecord("service.internal", null));
        }

        [TestMethod]
        public void PkiStateRequestRoundTripsPositiveHighWater()
        {
            var source = new PeerPkiStateRequest(
                Guid.Parse(LocalInstanceId),
                Guid.Parse(IssuerInstanceId),
                42,
                18);

            byte[] body = PeerContractXmlCodec.SerializePkiStateRequest(
                source);
            PeerPkiStateRequest parsed = PeerContractXmlCodec
                .ParseAuthenticatedPkiStateRequest(body);

            Assert.AreEqual(source.InstanceId, parsed.InstanceId);
            Assert.AreEqual(
                source.KnownIssuerInstanceId,
                parsed.KnownIssuerInstanceId);
            Assert.AreEqual(42UL, parsed.KnownPkiRevision);
            Assert.AreEqual(18UL, parsed.KnownCrlNumber);
        }

        [TestMethod]
        public void PkiStateRequestRejectsZeroAndNonCanonicalHighWater()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new PeerPkiStateRequest(
                    Guid.Parse(LocalInstanceId),
                    Guid.Parse(IssuerInstanceId),
                    0,
                    18));

            string xml = CreateRequestXml(42, 18)
                .Replace(
                    "<KnownPkiRevision>42</KnownPkiRevision>",
                    "<KnownPkiRevision>042</KnownPkiRevision>");
            AssertProtocolFailure(
                () => PeerContractXmlCodec
                    .ParseAuthenticatedPkiStateRequest(Encode(xml)),
                PeerSyncProtocolFailure.InvalidRequest);
        }

        [TestMethod]
        public void PkiStateResponseRoundTripsHigherAuthenticatedState()
        {
            PeerPkiState known = CreatePkiState(42, 18, new byte[] { 1 });
            PeerPkiState received = CreatePkiState(
                43,
                19,
                new byte[] { 2, 3 });
            PeerPkiStateRequest request = CreateRequest(known);

            byte[] body = PeerContractXmlCodec.SerializePkiStateResponse(
                PeerPkiStateResponse.CreateSuccess(received));
            PeerPkiStateResponse parsed = PeerContractXmlCodec
                .ParseAuthenticatedPkiStateResponse(
                    body,
                    request,
                    known);

            Assert.IsTrue(parsed.IsSuccess);
            Assert.AreEqual(43UL, parsed.PkiState.PkiRevision);
            Assert.AreEqual(19UL, parsed.PkiState.CrlNumber);
            Assert.AreEqual(1, parsed.PkiState.ActiveCertificates.Count);
            Assert.AreEqual(
                SerialNumber,
                parsed.PkiState.ActiveCertificates[0].SerialNumber);
            CollectionAssert.AreEqual(
                new byte[] { 2, 3 },
                parsed.PkiState.GetCrl());
        }

        [TestMethod]
        public void PkiStateResponseAcceptsExactEqualRevisionReplay()
        {
            PeerPkiState known = CreatePkiState(42, 18, new byte[] { 1 });
            PeerPkiStateResponse parsed = PeerContractXmlCodec
                .ParseAuthenticatedPkiStateResponse(
                    PeerContractXmlCodec.SerializePkiStateResponse(
                        PeerPkiStateResponse.CreateSuccess(known)),
                    CreateRequest(known),
                    known);

            Assert.IsTrue(parsed.IsSuccess);
            Assert.AreEqual(42UL, parsed.PkiState.PkiRevision);
        }

        [TestMethod]
        public void PkiStateResponseRejectsIssuerAndHighWaterRollback()
        {
            PeerPkiState known = CreatePkiState(42, 18, new byte[] { 1 });
            PeerPkiStateRequest request = CreateRequest(known);

            AssertInvalidResponse(
                CreatePkiState(43, 19, new byte[] { 2 }, OtherIssuerInstanceId),
                request,
                known);
            AssertInvalidResponse(
                CreatePkiState(41, 18, new byte[] { 2 }),
                request,
                known);
            AssertInvalidResponse(
                CreatePkiState(43, 17, new byte[] { 2 }),
                request,
                known);
        }

        [TestMethod]
        public void PkiStateResponseRejectsDifferentContentAtSameRevision()
        {
            PeerPkiState known = CreatePkiState(42, 18, new byte[] { 1 });
            PeerPkiState different = CreatePkiState(
                42,
                18,
                new byte[] { 9 });

            AssertInvalidResponse(
                different,
                CreateRequest(known),
                known);
        }

        [TestMethod]
        public void PkiStateModelRejectsHashMismatchAndUnorderedMapping()
        {
            string wrongHash = Sha256Base64(new byte[] { 9 });
            Assert.ThrowsExactly<ArgumentException>(
                () => new PeerPkiState(
                    Guid.Parse(IssuerInstanceId),
                    42,
                    18,
                    wrongHash,
                    new byte[] { 1 },
                    new PeerActiveCertificate[0]));

            var unordered = new List<PeerActiveCertificate>
            {
                CreateCertificate("WXYZ", SerialNumber),
                CreateCertificate(
                    "ABCD",
                    "02A4B5C6D7E8F90123456789ABCDEF01")
            };
            Assert.ThrowsExactly<ArgumentException>(
                () => CreatePkiState(
                    42,
                    18,
                    new byte[] { 1 },
                    IssuerInstanceId,
                    unordered));

            var duplicateSerial = new List<PeerActiveCertificate>
            {
                CreateCertificate("ABCD", SerialNumber),
                CreateCertificate("WXYZ", SerialNumber)
            };
            Assert.ThrowsExactly<ArgumentException>(
                () => CreatePkiState(
                    42,
                    18,
                    new byte[] { 1 },
                    IssuerInstanceId,
                    duplicateSerial));
        }

        [TestMethod]
        public void PkiErrorResponseHasNoStatePayload()
        {
            PeerPkiState known = CreatePkiState(42, 18, new byte[] { 1 });
            byte[] body = PeerContractXmlCodec.SerializePkiStateResponse(
                PeerPkiStateResponse.CreateError(
                    PeerSyncResponseCode.Conflict));
            PeerPkiStateResponse parsed = PeerContractXmlCodec
                .ParseAuthenticatedPkiStateResponse(
                    body,
                    CreateRequest(known),
                    known);

            Assert.IsFalse(parsed.IsSuccess);
            Assert.AreEqual(PeerSyncResponseCode.Conflict, parsed.Code);
            Assert.IsNull(parsed.PkiState);
            Assert.IsFalse(StrictUtf8.GetString(body).Contains("PkiState"));
        }

        [TestMethod]
        public void PkiResponseRejectsUnknownAndInconsistentPayloads()
        {
            PeerPkiState known = CreatePkiState(42, 18, new byte[] { 1 });
            string success = StrictUtf8.GetString(
                PeerContractXmlCodec.SerializePkiStateResponse(
                    PeerPkiStateResponse.CreateSuccess(known)));

            AssertInvalidResponseXml(
                success.Replace(
                    "</PkiState>",
                    "<Unknown>value</Unknown></PkiState>"),
                known);
            AssertInvalidResponseXml(
                success
                    .Replace("<Result>OK</Result>", "<Result>ERROR</Result>")
                    .Replace("<Code>0</Code>", "<Code>1002</Code>"),
                known);
        }

        [TestMethod]
        public void TargetCodecRejectsNullInvalidUtf8DtdAndOversize()
        {
            Assert.ThrowsExactly<ArgumentNullException>(
                () => PeerContractXmlCodec.ParseServiceRecord(null));
            AssertProtocolFailure(
                () => PeerContractXmlCodec.ParseServiceRecord(
                    new byte[] { 0xff }),
                PeerSyncProtocolFailure.InvalidRequest);
            AssertProtocolFailure(
                () => PeerContractXmlCodec.ParseServiceRecord(
                    Encode(
                        "<!DOCTYPE Service [<!ENTITY x 'value'>]>"
                            + "<Service xmlns='"
                            + XmlNamespace
                            + "'>&x;</Service>")),
                PeerSyncProtocolFailure.InvalidRequest);
            AssertProtocolFailure(
                () => PeerContractXmlCodec
                    .ParseAuthenticatedPkiStateResponse(
                        new byte[
                            PeerSyncContract.MaximumExchangeBodyBytes + 1],
                        CreateRequest(
                            CreatePkiState(42, 18, new byte[] { 1 })),
                        CreatePkiState(42, 18, new byte[] { 1 })),
                PeerSyncProtocolFailure.BodyTooLarge);
        }

        [TestMethod]
        public void PkiResponseCountsCertificatesBeforeMaterialization()
        {
            var certificates = new StringBuilder();
            string certificate =
                "<Certificate><ProductCode>ABCD</ProductCode>"
                + "<SerialNumber>"
                + SerialNumber
                + "</SerialNumber><LeafSha256>"
                + Sha256Base64(new byte[] { 7 })
                + "</LeafSha256><NotAfterUtc>"
                + "2027-07-19T02:00:00Z</NotAfterUtc></Certificate>";
            for (int index = 0;
                index <= PeerSyncContract.MaximumActiveCertificateCount;
                index++)
            {
                certificates.Append(certificate);
            }

            byte[] crl = { 1 };
            string xml =
                "<Response xmlns='" + XmlNamespace + "'><Result>OK</Result>"
                + "<Code>0</Code><Message/><PkiState><IssuerInstanceId>"
                + IssuerInstanceId
                + "</IssuerInstanceId><PkiRevision>43</PkiRevision>"
                + "<CrlNumber>19</CrlNumber><CrlSha256>"
                + Sha256Base64(crl)
                + "</CrlSha256><Crl>"
                + Convert.ToBase64String(crl)
                + "</Crl><ActiveCertificates>"
                + certificates
                + "</ActiveCertificates></PkiState></Response>";
            PeerPkiState known = CreatePkiState(42, 18, new byte[] { 1 });

            AssertProtocolFailure(
                () => PeerContractXmlCodec
                    .ParseAuthenticatedPkiStateResponse(
                        Encode(xml),
                        CreateRequest(known),
                        known),
                PeerSyncProtocolFailure.ItemLimitExceeded);
        }

        private static PeerServiceRecord CreateServiceRecord(
            string hostName = "service.internal",
            string ipv4Address = "10.0.0.5")
        {
            return new PeerServiceRecord(
                "Active App",
                "ABCD",
                hostName,
                ipv4Address,
                21000,
                new DateTime(
                    2026,
                    7,
                    18,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
                false,
                null,
                8,
                Guid.Parse(OtherIssuerInstanceId));
        }

        private static PeerPkiStateRequest CreateRequest(
            PeerPkiState known)
        {
            return new PeerPkiStateRequest(
                Guid.Parse(LocalInstanceId),
                known.IssuerInstanceId,
                known.PkiRevision,
                known.CrlNumber);
        }

        private static string CreateRequestXml(
            ulong pkiRevision,
            ulong crlNumber)
        {
            return StrictUtf8.GetString(
                PeerContractXmlCodec.SerializePkiStateRequest(
                    new PeerPkiStateRequest(
                        Guid.Parse(LocalInstanceId),
                        Guid.Parse(IssuerInstanceId),
                        pkiRevision,
                        crlNumber)));
        }

        private static PeerPkiState CreatePkiState(
            ulong pkiRevision,
            ulong crlNumber,
            byte[] crl,
            string issuerInstanceId = IssuerInstanceId,
            IReadOnlyList<PeerActiveCertificate> certificates = null)
        {
            IReadOnlyList<PeerActiveCertificate> values = certificates
                ?? new[] { CreateCertificate("ABCD", SerialNumber) };
            return new PeerPkiState(
                Guid.Parse(issuerInstanceId),
                pkiRevision,
                crlNumber,
                Sha256Base64(crl),
                crl,
                values);
        }

        private static PeerActiveCertificate CreateCertificate(
            string productCode,
            string serialNumber)
        {
            return new PeerActiveCertificate(
                productCode,
                serialNumber,
                Sha256Base64(Encoding.ASCII.GetBytes(productCode)),
                new DateTime(
                    2027,
                    7,
                    19,
                    2,
                    0,
                    0,
                    DateTimeKind.Utc));
        }

        private static void AssertInvalidService(string xml)
        {
            AssertProtocolFailure(
                () => PeerContractXmlCodec.ParseServiceRecord(Encode(xml)),
                PeerSyncProtocolFailure.InvalidRequest);
        }

        private static void AssertInvalidResponse(
            PeerPkiState received,
            PeerPkiStateRequest request,
            PeerPkiState known)
        {
            AssertProtocolFailure(
                () => PeerContractXmlCodec
                    .ParseAuthenticatedPkiStateResponse(
                        PeerContractXmlCodec.SerializePkiStateResponse(
                            PeerPkiStateResponse.CreateSuccess(received)),
                        request,
                        known),
                PeerSyncProtocolFailure.InvalidRequest);
        }

        private static void AssertInvalidResponseXml(
            string xml,
            PeerPkiState known)
        {
            AssertProtocolFailure(
                () => PeerContractXmlCodec
                    .ParseAuthenticatedPkiStateResponse(
                        Encode(xml),
                        CreateRequest(known),
                        known),
                PeerSyncProtocolFailure.InvalidRequest);
        }

        private static void AssertProtocolFailure(
            Action action,
            PeerSyncProtocolFailure expectedFailure)
        {
            PeerSyncProtocolException exception =
                Assert.ThrowsExactly<PeerSyncProtocolException>(action);
            Assert.AreEqual(expectedFailure, exception.Failure);
        }

        private static string Sha256Base64(byte[] value)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                return Convert.ToBase64String(
                    algorithm.ComputeHash(value));
            }
        }

        private static byte[] Encode(string value)
        {
            return StrictUtf8.GetBytes(value);
        }
    }
}
