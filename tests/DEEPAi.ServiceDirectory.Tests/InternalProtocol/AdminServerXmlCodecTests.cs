using System;
using System.Text;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.InternalProtocol
{
    [TestClass]
    public sealed class AdminServerXmlCodecTests
    {
        private const string XmlNamespace =
            "urn:deepai:service-directory:admin";

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void CertificateAdministrationRequestsRoundTripCanonicalXml()
        {
            const string password = "correct horse battery staple";
            byte[] backupBody = AdminXmlCodec.SerializeCreateCaBackup(
                password);
            Assert.AreEqual(
                password,
                AdminServerXmlCodec.ParseCreateCaBackupRequest(backupBody)
                    .Password);

            byte[] revokeBody = AdminXmlCodec.SerializeRevokeCertificate(
                AdminCertificateRevocationReason.KeyCompromise);
            Assert.AreEqual(
                AdminCertificateRevocationReason.KeyCompromise,
                AdminServerXmlCodec.ParseRevokeCertificateRequest(
                    revokeBody).Reason);
            Assert.ThrowsExactly<ArgumentException>(() =>
                AdminXmlCodec.SerializeRevokeCertificate(
                    AdminCertificateRevocationReason.Superseded));
        }

        [TestMethod]
        public void CertificateAdministrationRequestsRejectInvalidShapes()
        {
            Assert.ThrowsExactly<AdminProtocolException>(() =>
                AdminServerXmlCodec.ParseCreateCaBackupRequest(Encode(
                    "<CreateCaBackup xmlns=\""
                    + XmlNamespace
                    + "\"><Password>short</Password></CreateCaBackup>")));
            Assert.ThrowsExactly<AdminProtocolException>(() =>
                AdminServerXmlCodec.ParseCreateCaBackupRequest(Encode(
                    "<CreateCaBackup xmlns=\""
                    + XmlNamespace
                    + "\"><Password>valid-length&#xA;password</Password>"
                    + "</CreateCaBackup>")));
            Assert.ThrowsExactly<AdminProtocolException>(() =>
                AdminServerXmlCodec.ParseRevokeCertificateRequest(Encode(
                    "<RevokeCertificate xmlns=\""
                    + XmlNamespace
                    + "\"><Reason>SUPERSEDED</Reason>"
                    + "</RevokeCertificate>")));
        }

        [TestMethod]
        public void ParseRequestRejectsNullEmptyOversizedAndInvalidUtf8Bodies()
        {
            Assert.ThrowsExactly<ArgumentNullException>(
                () => AdminServerXmlCodec.ParseDisableSyncRequest(null));
            Assert.ThrowsExactly<AdminProtocolException>(
                () => AdminServerXmlCodec.ParseDisableSyncRequest(
                    new byte[0]));
            Assert.ThrowsExactly<AdminProtocolException>(
                () => AdminServerXmlCodec.ParseDisableSyncRequest(
                    new byte[AdminApiContract.MaximumBodyBytes + 1]));
            Assert.ThrowsExactly<AdminProtocolException>(
                () => AdminServerXmlCodec.ParseDisableSyncRequest(
                    new byte[] { 0xc3, 0x28 }));
        }

        [TestMethod]
        public void ParseRequestAcceptsExactBodyLimitAndRejectsOneByteMore()
        {
            string document = CreateDisableSyncDocument("false");
            byte[] documentBytes = Encode(document);
            string exactDocument = document
                + new string(
                    ' ',
                    AdminApiContract.MaximumBodyBytes
                        - documentBytes.Length);
            byte[] exactBody = Encode(exactDocument);

            Assert.AreEqual(
                AdminApiContract.MaximumBodyBytes,
                exactBody.Length);
            Assert.IsFalse(
                AdminServerXmlCodec.ParseDisableSyncRequest(exactBody)
                    .ForgetPeer);

            byte[] oversizedBody = Encode(exactDocument + " ");
            Assert.AreEqual(
                AdminApiContract.MaximumBodyBytes + 1,
                oversizedBody.Length);
            Assert.ThrowsExactly<AdminProtocolException>(
                () => AdminServerXmlCodec.ParseDisableSyncRequest(
                    oversizedBody));
        }

        [TestMethod]
        public void ParseRequestRejectsDtdAndExternalEntity()
        {
            string xml =
                "<!DOCTYPE DisableSync ["
                + "<!ENTITY external SYSTEM \"file:///C:/Windows/win.ini\">"
                + "]>"
                + "<DisableSync xmlns=\""
                + XmlNamespace
                + "\"><ForgetPeer>&external;</ForgetPeer>"
                + "</DisableSync>";

            AssertDisableSyncRejected(xml);
        }

        [TestMethod]
        public void ParseRequestRejectsDepthGreaterThanSixteen()
        {
            var builder = new StringBuilder();
            builder.Append("<DisableSync xmlns=\"");
            builder.Append(XmlNamespace);
            builder.Append("\">");
            for (int depth = 0;
                depth < AdminApiContract.MaximumXmlDepth;
                depth++)
            {
                builder.Append("<Nested>");
            }

            builder.Append("false");
            for (int depth = 0;
                depth < AdminApiContract.MaximumXmlDepth;
                depth++)
            {
                builder.Append("</Nested>");
            }

            builder.Append("</DisableSync>");

            AssertDisableSyncRejected(builder.ToString());
        }

        [TestMethod]
        public void ParseEnableSyncRequestAcceptsCanonicalEndpoints()
        {
            AdminEnableSyncRequest ipv4 =
                AdminServerXmlCodec.ParseEnableSyncRequest(
                    Encode(CreateEnableSyncDocument(
                        "http://10.0.0.2:21000",
                        "false")));
            Assert.AreEqual("http://10.0.0.2:21000", ipv4.PeerEndpoint);
            Assert.IsFalse(ipv4.RePair);

            AdminEnableSyncRequest ipv6 =
                AdminServerXmlCodec.ParseEnableSyncRequest(
                    Encode(CreateEnableSyncDocument(
                        "http://[2001:db8::1]:21000",
                        "true")));
            Assert.AreEqual(
                "http://[2001:db8::1]:21000",
                ipv6.PeerEndpoint);
            Assert.IsTrue(ipv6.RePair);
        }

        [TestMethod]
        public void ParseEnableSyncRequestRejectsNonCanonicalOrInvalidEndpoint()
        {
            string[] invalidEndpoints =
            {
                "HTTP://10.0.0.2:21000",
                " http://10.0.0.2:21000",
                "http://010.0.0.2:21000",
                "http://service.internal:21000",
                "http://127.0.0.1:21000",
                "http://10.0.0.2:21001",
                "http://10.0.0.2:21000/path",
                "http://[2001:0db8:0:0:0:0:0:1]:21000"
            };

            foreach (string invalidEndpoint in invalidEndpoints)
            {
                string endpoint = invalidEndpoint;
                Assert.ThrowsExactly<AdminProtocolException>(
                    () => AdminServerXmlCodec.ParseEnableSyncRequest(
                        Encode(CreateEnableSyncDocument(
                            endpoint,
                            "false"))));
            }
        }

        [TestMethod]
        public void ParseEnableSyncRequestRejectsClosedSchemaViolations()
        {
            string endpoint =
                "<PeerEndpoint>http://10.0.0.2:21000</PeerEndpoint>";
            string rePair = "<RePair>false</RePair>";
            string[] invalidDocuments =
            {
                "<EnableSync xmlns=\"urn:wrong\">"
                    + endpoint
                    + rePair
                    + "</EnableSync>",
                "<EnableSync>"
                    + endpoint
                    + rePair
                    + "</EnableSync>",
                "<EnableSync xmlns=\""
                    + XmlNamespace
                    + "\">"
                    + rePair
                    + endpoint
                    + "</EnableSync>",
                "<EnableSync xmlns=\""
                    + XmlNamespace
                    + "\">"
                    + endpoint
                    + endpoint
                    + rePair
                    + "</EnableSync>",
                "<EnableSync xmlns=\""
                    + XmlNamespace
                    + "\" Unexpected=\"true\">"
                    + endpoint
                    + rePair
                    + "</EnableSync>",
                "<EnableSync xmlns=\""
                    + XmlNamespace
                    + "\" xmlns:xsi=\"http://www.w3.org/2001/"
                    + "XMLSchema-instance\" xsi:schemaLocation=\""
                    + XmlNamespace
                    + " admin.xsd\">"
                    + endpoint
                    + rePair
                    + "</EnableSync>",
                "<EnableSync xmlns=\""
                    + XmlNamespace
                    + "\"><PeerEndpoint Unexpected=\"true\">"
                    + "http://10.0.0.2:21000</PeerEndpoint>"
                    + rePair
                    + "</EnableSync>",
                "<EnableSync xmlns=\""
                    + XmlNamespace
                    + "\">"
                    + endpoint
                    + rePair
                    + "<Unexpected />"
                    + "</EnableSync>",
                "<EnableSync xmlns=\""
                    + XmlNamespace
                    + "\">mixed"
                    + endpoint
                    + rePair
                    + "</EnableSync>",
                "<DisableSync xmlns=\""
                    + XmlNamespace
                    + "\"><ForgetPeer>false</ForgetPeer></DisableSync>",
                "<Response xmlns=\""
                    + XmlNamespace
                    + "\"><Result>OK</Result><Code>0</Code>"
                    + "<Message /></Response>"
            };

            foreach (string invalidDocument in invalidDocuments)
            {
                string document = invalidDocument;
                Assert.ThrowsExactly<AdminProtocolException>(
                    () => AdminServerXmlCodec.ParseEnableSyncRequest(
                        Encode(document)));
            }
        }

        [TestMethod]
        public void RequestParsersRejectResponseRoot()
        {
            byte[] response = Encode(
                "<Response xmlns=\""
                + XmlNamespace
                + "\"><Result>OK</Result><Code>0</Code>"
                + "<Message /></Response>");
            Action<byte[]>[] parsers =
            {
                body =>
                {
                    AdminServerXmlCodec.ParseEnableSyncRequest(body);
                },
                body =>
                {
                    AdminServerXmlCodec.ParsePairingConfirmationRequest(
                        body);
                },
                body =>
                {
                    AdminServerXmlCodec.ParsePairingCancellationRequest(
                        body);
                },
                body =>
                {
                    AdminServerXmlCodec.ParseDisableSyncRequest(body);
                },
                body =>
                {
                    AdminServerXmlCodec.ParseLoggingSettingsRequest(body);
                }
            };

            foreach (Action<byte[]> parser in parsers)
            {
                Action<byte[]> requestParser = parser;
                Assert.ThrowsExactly<AdminProtocolException>(
                    () => requestParser(response));
            }
        }

        [TestMethod]
        public void ParsePairingRequestsRequireCanonicalNonEmptyPairingId()
        {
            Guid pairingId = new Guid(
                "b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12");
            AdminPairingConfirmationRequest confirmation =
                AdminServerXmlCodec.ParsePairingConfirmationRequest(
                    Encode(CreatePairingConfirmationDocument(
                        pairingId.ToString("D"),
                        "true")));
            Assert.AreEqual(pairingId, confirmation.PairingId);
            Assert.IsTrue(confirmation.Confirmed);

            AdminPairingCancellationRequest cancellation =
                AdminServerXmlCodec.ParsePairingCancellationRequest(
                    Encode(CreatePairingCancellationDocument(
                        pairingId.ToString("D"))));
            Assert.AreEqual(pairingId, cancellation.PairingId);

            string emptyId = Guid.Empty.ToString("D");
            Assert.ThrowsExactly<AdminProtocolException>(
                () => AdminServerXmlCodec.ParsePairingConfirmationRequest(
                    Encode(CreatePairingConfirmationDocument(
                        emptyId,
                        "true"))));
            Assert.ThrowsExactly<AdminProtocolException>(
                () => AdminServerXmlCodec.ParsePairingCancellationRequest(
                    Encode(CreatePairingCancellationDocument(emptyId))));

            string uppercaseId = pairingId.ToString("D").ToUpperInvariant();
            Assert.ThrowsExactly<AdminProtocolException>(
                () => AdminServerXmlCodec.ParsePairingConfirmationRequest(
                    Encode(CreatePairingConfirmationDocument(
                        uppercaseId,
                        "true"))));

            AdminPairingConfirmationRequest declined =
                AdminServerXmlCodec.ParsePairingConfirmationRequest(
                    Encode(CreatePairingConfirmationDocument(
                        pairingId.ToString("D"),
                        "false")));
            Assert.AreEqual(pairingId, declined.PairingId);
            Assert.IsFalse(declined.Confirmed);
        }

        [TestMethod]
        public void ParseDisableSyncRequestAcceptsOnlyCanonicalBooleanText()
        {
            Assert.IsFalse(
                AdminServerXmlCodec.ParseDisableSyncRequest(
                    Encode(CreateDisableSyncDocument("false")))
                    .ForgetPeer);
            Assert.IsTrue(
                AdminServerXmlCodec.ParseDisableSyncRequest(
                    Encode(CreateDisableSyncDocument("true")))
                    .ForgetPeer);

            AssertDisableSyncRejected(CreateDisableSyncDocument("False"));
            AssertDisableSyncRejected(CreateDisableSyncDocument("0"));
        }

        [TestMethod]
        public void ParseLoggingSettingsRequestEnforcesRetentionRange()
        {
            Assert.AreEqual(
                AdminApiContract.MinimumLogRetentionDays,
                AdminServerXmlCodec.ParseLoggingSettingsRequest(
                    Encode(CreateLoggingSettingsDocument("1")))
                    .LogRetentionDays);
            Assert.AreEqual(
                AdminApiContract.MaximumLogRetentionDays,
                AdminServerXmlCodec.ParseLoggingSettingsRequest(
                    Encode(CreateLoggingSettingsDocument("1095")))
                    .LogRetentionDays);

            string[] invalidValues =
            {
                "0",
                "1096",
                "030",
                "-1",
                "+30",
                "30.0",
                "2147483648"
            };
            foreach (string invalidValue in invalidValues)
            {
                string document = CreateLoggingSettingsDocument(
                    invalidValue);
                Assert.ThrowsExactly<AdminProtocolException>(
                    () => AdminServerXmlCodec.ParseLoggingSettingsRequest(
                        Encode(document)));
            }
        }

        [TestMethod]
        public void ParseServicesResponseUsesDomainServiceDefinitionBoundary()
        {
            string supplementaryName = string.Concat(
                Repeat("\U0001F600", 128));
            AdminResponse<AdminPage<AdminServiceItem>> response =
                AdminXmlCodec.ParseServicesResponse(
                    Encode(CreateServicesResponse(
                        supplementaryName,
                        "service.internal")));

            Assert.IsTrue(response.IsSuccess);
            Assert.AreEqual(supplementaryName, response.Payload.Items[0].Name);
            Assert.AreEqual(128, CountUnicodeScalars(
                response.Payload.Items[0].Name));
            Assert.AreEqual(
                512,
                StrictUtf8.GetByteCount(response.Payload.Items[0].Name));

            AssertServicesResponseRejected(
                CreateServicesResponse(
                    new string('N', 129),
                    "service.internal"));
            AssertServicesResponseRejected(
                CreateServicesResponse(
                    " Directory ",
                    "service.internal"));
            AssertServicesResponseRejected(
                CreateServicesResponse(
                    "Directory",
                    " service.internal "));
            AssertServicesResponseRejected(
                CreateServicesResponse(
                    "Directory",
                    "999.1.1.1"));
        }

        private static void AssertDisableSyncRejected(string xml)
        {
            Assert.ThrowsExactly<AdminProtocolException>(
                () => AdminServerXmlCodec.ParseDisableSyncRequest(
                    Encode(xml)));
        }

        private static void AssertServicesResponseRejected(string xml)
        {
            Assert.ThrowsExactly<AdminProtocolException>(
                () => AdminXmlCodec.ParseServicesResponse(Encode(xml)));
        }

        private static string CreateEnableSyncDocument(
            string endpoint,
            string rePair)
        {
            return "<EnableSync xmlns=\""
                + XmlNamespace
                + "\"><PeerEndpoint>"
                + endpoint
                + "</PeerEndpoint><RePair>"
                + rePair
                + "</RePair></EnableSync>";
        }

        private static string CreatePairingConfirmationDocument(
            string pairingId,
            string confirmed)
        {
            return "<PairingConfirmation xmlns=\""
                + XmlNamespace
                + "\"><PairingId>"
                + pairingId
                + "</PairingId><Confirmed>"
                + confirmed
                + "</Confirmed></PairingConfirmation>";
        }

        private static string CreatePairingCancellationDocument(
            string pairingId)
        {
            return "<PairingCancellation xmlns=\""
                + XmlNamespace
                + "\"><PairingId>"
                + pairingId
                + "</PairingId></PairingCancellation>";
        }

        private static string CreateDisableSyncDocument(string forgetPeer)
        {
            return "<DisableSync xmlns=\""
                + XmlNamespace
                + "\"><ForgetPeer>"
                + forgetPeer
                + "</ForgetPeer></DisableSync>";
        }

        private static string CreateLoggingSettingsDocument(
            string logRetentionDays)
        {
            return "<LoggingSettings xmlns=\""
                + XmlNamespace
                + "\"><LogRetentionDays>"
                + logRetentionDays
                + "</LogRetentionDays></LoggingSettings>";
        }

        private static string CreateServicesResponse(
            string name,
            string serverAddress)
        {
            return "<Response xmlns=\""
                + XmlNamespace
                + "\"><Result>OK</Result><Code>0</Code><Message />"
                + "<Services><Service><Name>"
                + name
                + "</Name><ProductCode>AB12</ProductCode>"
                + "<ServerAddress>"
                + serverAddress
                + "</ServerAddress><Port>21000</Port>"
                + "<LastModifiedUtc>2026-07-18T00:00:00Z"
                + "</LastModifiedUtc><Deleted>false</Deleted>"
                + "</Service></Services><TotalCount>1</TotalCount>"
                + "</Response>";
        }

        private static string[] Repeat(string value, int count)
        {
            var values = new string[count];
            for (int index = 0; index < count; index++)
            {
                values[index] = value;
            }

            return values;
        }

        private static int CountUnicodeScalars(string value)
        {
            int count = 0;
            for (int index = 0; index < value.Length; index++)
            {
                if (char.IsHighSurrogate(value[index]))
                {
                    index++;
                }

                count++;
            }

            return count;
        }

        private static byte[] Encode(string value)
        {
            return StrictUtf8.GetBytes(value);
        }
    }
}
