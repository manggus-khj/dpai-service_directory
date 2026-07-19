using System;
using System.Collections.Generic;
using System.Text;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.ExternalProtocol
{
    [TestClass]
    public sealed class ExternalXmlCodecTests
    {
        private const string XmlNamespace =
            "urn:deepai:service-directory:external";

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void ParseRegistrationRequestRejectsNullEmptyOversizedAndInvalidUtf8Bodies()
        {
            Assert.ThrowsExactly<ArgumentNullException>(
                () => ExternalXmlCodec.ParseRegistrationRequest(null));
            Assert.ThrowsExactly<ExternalProtocolException>(
                () => ExternalXmlCodec.ParseRegistrationRequest(
                    new byte[0]));
            Assert.ThrowsExactly<ExternalProtocolException>(
                () => ExternalXmlCodec.ParseRegistrationRequest(
                    new byte[ExternalApiContract.MaximumBodyBytes + 1]));
            Assert.ThrowsExactly<ExternalProtocolException>(
                () => ExternalXmlCodec.ParseRegistrationRequest(
                    new byte[] { 0xc3, 0x28 }));
        }

        [TestMethod]
        public void ParseRegistrationRequestAcceptsExactBodyLimitAndRejectsOneByteMore()
        {
            string baseDocument = CreateRegistrationDocument(
                "Directory",
                "AB12",
                "service.internal",
                "21000");
            byte[] baseBody = Encode(baseDocument);
            Assert.IsTrue(
                baseBody.Length < ExternalApiContract.MaximumBodyBytes);

            string exactDocument = baseDocument
                + new string(
                    ' ',
                    ExternalApiContract.MaximumBodyBytes
                        - baseBody.Length);
            byte[] exactBody = Encode(exactDocument);
            Assert.AreEqual(
                ExternalApiContract.MaximumBodyBytes,
                exactBody.Length);

            ExternalRegistrationRequest request =
                ExternalXmlCodec.ParseRegistrationRequest(exactBody);
            Assert.AreEqual("AB12", request.ProductCode);

            byte[] oversizedBody = Encode(exactDocument + " ");
            Assert.AreEqual(
                ExternalApiContract.MaximumBodyBytes + 1,
                oversizedBody.Length);
            Assert.ThrowsExactly<ExternalProtocolException>(
                () => ExternalXmlCodec.ParseRegistrationRequest(
                    oversizedBody));
        }

        [TestMethod]
        public void ParseRegistrationRequestRejectsDtdAndExternalEntity()
        {
            string xml =
                "<!DOCTYPE RegistrationRequest ["
                + "<!ENTITY external SYSTEM \"file:///C:/Windows/win.ini\">"
                + "]>"
                + "<RegistrationRequest xmlns=\""
                + XmlNamespace
                + "\">"
                + "<Name>&external;</Name>"
                + "<ProductCode>AB12</ProductCode>"
                + "<ServerAddress>service.internal</ServerAddress>"
                + "<Port>21000</Port>"
                + "</RegistrationRequest>";

            AssertRegistrationRejected(xml);
        }

        [TestMethod]
        public void ParseRegistrationRequestRejectsDepthGreaterThanSixteen()
        {
            var builder = new StringBuilder();
            builder.Append("<RegistrationRequest xmlns=\"");
            builder.Append(XmlNamespace);
            builder.Append("\">");
            for (int depth = 0;
                depth < ExternalApiContract.MaximumXmlDepth;
                depth++)
            {
                builder.Append("<Nested>");
            }

            builder.Append("value");
            for (int depth = 0;
                depth < ExternalApiContract.MaximumXmlDepth;
                depth++)
            {
                builder.Append("</Nested>");
            }

            builder.Append("</RegistrationRequest>");

            AssertRegistrationRejected(builder.ToString());
        }

        [TestMethod]
        public void ParseRegistrationRequestRejectsClosedSchemaViolations()
        {
            string validChildren = CreateValidRegistrationChildren();
            string[] invalidDocuments =
            {
                "<RegistrationRequest xmlns=\"urn:wrong\">"
                    + validChildren
                    + "</RegistrationRequest>",
                "<RegistrationRequest>"
                    + validChildren
                    + "</RegistrationRequest>",
                "<RegistrationRequest xmlns=\""
                    + XmlNamespace
                    + "\"><ProductCode>AB12</ProductCode>"
                    + "<Name>Directory</Name>"
                    + "<ServerAddress>service.internal</ServerAddress>"
                    + "<Port>21000</Port></RegistrationRequest>",
                "<RegistrationRequest xmlns=\""
                    + XmlNamespace
                    + "\"><Name>Directory</Name><Name>Duplicate</Name>"
                    + "<ProductCode>AB12</ProductCode>"
                    + "<ServerAddress>service.internal</ServerAddress>"
                    + "<Port>21000</Port></RegistrationRequest>",
                "<RegistrationRequest xmlns=\""
                    + XmlNamespace
                    + "\" Unexpected=\"true\">"
                    + validChildren
                    + "</RegistrationRequest>",
                "<RegistrationRequest xmlns=\""
                    + XmlNamespace
                    + "\"><Name Unexpected=\"true\">Directory</Name>"
                    + "<ProductCode>AB12</ProductCode>"
                    + "<ServerAddress>service.internal</ServerAddress>"
                    + "<Port>21000</Port></RegistrationRequest>",
                "<RegistrationRequest xmlns=\""
                    + XmlNamespace
                    + "\">"
                    + validChildren
                    + "<Unexpected>value</Unexpected>"
                    + "</RegistrationRequest>",
                "<RegistrationRequest xmlns=\""
                    + XmlNamespace
                    + "\">mixed-text"
                    + validChildren
                    + "</RegistrationRequest>",
                "<Response xmlns=\""
                    + XmlNamespace
                    + "\"><Result>OK</Result><Code>0</Code>"
                    + "<Message /></Response>"
            };

            foreach (string invalidDocument in invalidDocuments)
            {
                AssertRegistrationRejected(invalidDocument);
            }
        }

        [TestMethod]
        public void ParseRegistrationRequestRejectsSchemaValidDomainInvalidValues()
        {
            string[] invalidDocuments =
            {
                CreateRegistrationDocument(
                    "Directory&#x9;Service",
                    "AB12",
                    "service.internal",
                    "21000"),
                CreateRegistrationDocument(
                    "Directory",
                    "AB12",
                    "http://service.internal",
                    "21000"),
                CreateRegistrationDocument(
                    "Directory",
                    "AB12",
                    "010.20.30.40",
                    "21000")
            };

            foreach (string invalidDocument in invalidDocuments)
            {
                AssertRegistrationRejected(invalidDocument);
            }
        }

        [TestMethod]
        public void ParseRegistrationRequestReturnsNormalizedPurposeSpecificModel()
        {
            string xml = CreateRegistrationDocument(
                "  Directory Service  ",
                " ab12 ",
                " service-01.internal ",
                "21000");

            ExternalRegistrationRequest request =
                ExternalXmlCodec.ParseRegistrationRequest(
                    Encode(xml));

            Assert.AreEqual("Directory Service", request.Name);
            Assert.AreEqual("AB12", request.ProductCode);
            Assert.AreEqual(
                "service-01.internal",
                request.ServerAddress);
            Assert.AreEqual(21000, request.Port);
        }

        [TestMethod]
        public void ParseRegistrationRequestAllowsTrimmedBoundaryNameAndAddress()
        {
            string name = new string('\u4e00', 128);
            string serverAddress = CreateMaximumLengthDnsName();
            Assert.AreEqual(128, name.Length);
            Assert.AreEqual(384, StrictUtf8.GetByteCount(name));
            Assert.AreEqual(253, serverAddress.Length);

            ExternalRegistrationRequest request =
                ExternalXmlCodec.ParseRegistrationRequest(
                    Encode(
                        CreateRegistrationDocument(
                            "  " + name + "  ",
                            "AB12",
                            "  " + serverAddress + "  ",
                            "21000")));

            Assert.AreEqual(name, request.Name);
            Assert.AreEqual(serverAddress, request.ServerAddress);
        }

        [TestMethod]
        public void ParseAndSerializeAllow128SupplementaryScalarsAt512Utf8Bytes()
        {
            string scalar = char.ConvertFromUtf32(0x1f600);
            string name = Repeat(scalar, 128);
            Assert.AreEqual(256, name.Length);
            Assert.AreEqual(512, StrictUtf8.GetByteCount(name));

            ExternalRegistrationRequest request =
                ExternalXmlCodec.ParseRegistrationRequest(
                    Encode(
                        CreateRegistrationDocument(
                            " " + name + " ",
                            "AB12",
                            "service.internal",
                            "21000")));
            Assert.AreEqual(name, request.Name);

            var service = new ExternalServiceItem(
                " " + name + " ",
                "AB12",
                "service.internal",
                21000,
                new DateTime(
                    2026,
                    7,
                    18,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc));
            string responseXml = DecodeBomless(
                ExternalXmlCodec.SerializeServiceResponse(
                    ExternalResponse.CreateServiceSuccess(service)));

            StringAssert.Contains(
                responseXml,
                "<Name>" + name + "</Name>");
        }

        [TestMethod]
        public void RequestAndResponseModelsReject129UnicodeScalars()
        {
            string name = Repeat(
                char.ConvertFromUtf32(0x1f600),
                129);
            Assert.AreEqual(258, name.Length);
            Assert.AreEqual(516, StrictUtf8.GetByteCount(name));

            AssertRegistrationRejected(
                CreateRegistrationDocument(
                    name,
                    "AB12",
                    "service.internal",
                    "21000"));
            Assert.ThrowsExactly<ArgumentException>(
                () => new ExternalServiceItem(
                    name,
                    "AB12",
                    "service.internal",
                    21000,
                    new DateTime(
                        2026,
                        7,
                        18,
                        0,
                        0,
                        0,
                        DateTimeKind.Utc)));
        }

        [TestMethod]
        public void SerializeHealthResponseUsesBomlessUtf8UtcAndNoVersionFields()
        {
            DateTime utcNow = new DateTime(
                2026,
                7,
                17,
                2,
                3,
                4,
                DateTimeKind.Utc).AddTicks(1234000L);
            ExternalResponse response =
                ExternalResponse.CreateHealthSuccess(utcNow);

            byte[] body = ExternalXmlCodec.SerializeHealthResponse(
                response);
            string xml = DecodeBomless(body);

            AssertResponseEnvelope(xml, "OK", "0");
            StringAssert.Contains(
                xml,
                "<UtcNow>2026-07-17T02:03:04.1234Z</UtcNow>");
            AssertElementOrder(
                xml,
                "<Result>",
                "<Code>",
                "<Message",
                "<UtcNow>");
            AssertInternalFieldsAreAbsent(xml);
        }

        [TestMethod]
        public void SerializeServiceResponseNormalizesFieldsAndHidesInternalState()
        {
            DateTime lastModifiedUtc = new DateTime(
                2026,
                7,
                17,
                5,
                6,
                7,
                DateTimeKind.Utc).AddTicks(7654321L);
            var service = new ExternalServiceItem(
                " VMS Bridge ",
                " ab12 ",
                " service-01.internal ",
                21500,
                lastModifiedUtc);
            ExternalResponse response =
                ExternalResponse.CreateServiceSuccess(service);

            byte[] body = ExternalXmlCodec.SerializeServiceResponse(
                response);
            string xml = DecodeBomless(body);

            AssertResponseEnvelope(xml, "OK", "0");
            StringAssert.Contains(xml, "<Name>VMS Bridge</Name>");
            StringAssert.Contains(xml, "<ProductCode>AB12</ProductCode>");
            StringAssert.Contains(
                xml,
                "<ServerAddress>service-01.internal</ServerAddress>");
            StringAssert.Contains(xml, "<Port>21500</Port>");
            StringAssert.Contains(
                xml,
                "<LastModifiedUtc>2026-07-17T05:06:07.7654321Z</LastModifiedUtc>");
            AssertInternalFieldsAreAbsent(xml);
        }

        [TestMethod]
        public void SerializeRegistrationResponseUsesClosedStatusAndLowercaseGuid()
        {
            Guid pendingId = Guid.Parse(
                "B3F2D9F0-4C64-4DAD-A855-44EA8F6E0A12");
            var cases = new Dictionary<ExternalRegistrationStatus, string>
            {
                {
                    ExternalRegistrationStatus.PendingNew,
                    "PENDING_NEW"
                },
                {
                    ExternalRegistrationStatus.PendingModify,
                    "PENDING_MODIFY"
                },
                {
                    ExternalRegistrationStatus.PendingExists,
                    "PENDING_EXISTS"
                }
            };

            foreach (KeyValuePair<ExternalRegistrationStatus, string> item
                in cases)
            {
                ExternalResponse response =
                    ExternalResponse.CreateRegistrationSuccess(
                        item.Key,
                        pendingId);
                string xml = DecodeBomless(
                    ExternalXmlCodec.SerializeRegistrationResponse(
                        response));

                AssertResponseEnvelope(xml, "OK", "0");
                StringAssert.Contains(
                    xml,
                    "<Status>" + item.Value + "</Status>");
                StringAssert.Contains(
                    xml,
                    "<PendingId>b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12</PendingId>");
                AssertElementOrder(
                    xml,
                    "<Result>",
                    "<Code>",
                    "<Message",
                    "<Status>",
                    "<PendingId>");
                AssertInternalFieldsAreAbsent(xml);
            }

            ExternalResponse alreadyRegistered =
                ExternalResponse.CreateRegistrationSuccess(
                    ExternalRegistrationStatus.AlreadyRegistered,
                    null);
            string alreadyRegisteredXml = DecodeBomless(
                ExternalXmlCodec.SerializeRegistrationResponse(
                    alreadyRegistered));

            StringAssert.Contains(
                alreadyRegisteredXml,
                "<Status>ALREADY_REGISTERED</Status>");
            Assert.DoesNotContain(
                "<PendingId>",
                alreadyRegisteredXml);
        }

        [TestMethod]
        public void RegistrationResponseModelEnforcesPendingIdCardinality()
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => ExternalResponse.CreateRegistrationSuccess(
                    ExternalRegistrationStatus.PendingNew,
                    null));
            Assert.ThrowsExactly<ArgumentException>(
                () => ExternalResponse.CreateRegistrationSuccess(
                    ExternalRegistrationStatus.PendingModify,
                    Guid.Empty));
            Assert.ThrowsExactly<ArgumentException>(
                () => ExternalResponse.CreateRegistrationSuccess(
                    ExternalRegistrationStatus.AlreadyRegistered,
                    Guid.NewGuid()));
        }

        [TestMethod]
        public void SerializeErrorResponseUsesClosedErrorEnvelopeWithoutPayload()
        {
            ExternalResponse response = ExternalResponse.CreateError(
                ExternalResponseCode.InvalidApiKey);

            byte[] body = ExternalXmlCodec.SerializeErrorResponse(
                response);
            string xml = DecodeBomless(body);

            AssertResponseEnvelope(xml, "ERROR", "1003");
            StringAssert.Contains(
                xml,
                "<Message>The API key is invalid.</Message>");
            Assert.DoesNotContain("<UtcNow>", xml);
            Assert.DoesNotContain("<Service>", xml);
            Assert.DoesNotContain("<Status>", xml);
            Assert.DoesNotContain("<PendingId>", xml);
            AssertInternalFieldsAreAbsent(xml);

            CollectionAssert.AreEqual(
                body,
                ExternalXmlCodec.SerializeHealthResponse(response));
            CollectionAssert.AreEqual(
                body,
                ExternalXmlCodec.SerializeServiceResponse(response));
            CollectionAssert.AreEqual(
                body,
                ExternalXmlCodec.SerializeRegistrationResponse(response));
        }

        [TestMethod]
        public void ErrorResponseModelUsesClosedCodesAndFixedSafeMessages()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => ExternalResponse.CreateError(
                    ExternalResponseCode.Ok));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => ExternalResponse.CreateError(
                    (ExternalResponseCode)9999));

            var expectedMessages = new Dictionary<ExternalResponseCode, string>
            {
                { ExternalResponseCode.BadRequest, "The request is invalid." },
                {
                    ExternalResponseCode.NotFound,
                    "The requested service was not found."
                },
                {
                    ExternalResponseCode.Conflict,
                    "The request conflicts with the current state."
                },
                {
                    ExternalResponseCode.InvalidApiKey,
                    "The API key is invalid."
                },
                {
                    ExternalResponseCode.LimitExceeded,
                    "The request limit was exceeded."
                },
                {
                    ExternalResponseCode.Internal,
                    "The service directory could not process the request."
                }
            };

            foreach (KeyValuePair<ExternalResponseCode, string> item
                in expectedMessages)
            {
                ExternalResponse response = ExternalResponse.CreateError(
                    item.Key);
                Assert.AreEqual(item.Value, response.Message);
                Assert.DoesNotContain(
                    "sensitive internal exception",
                    response.Message);
            }
        }

        [TestMethod]
        public void EndpointSerializersRejectMismatchedSuccessPayloads()
        {
            DateTime utcNow = new DateTime(
                2026,
                7,
                17,
                0,
                0,
                0,
                DateTimeKind.Utc);
            ExternalResponse health =
                ExternalResponse.CreateHealthSuccess(utcNow);
            ExternalResponse service =
                ExternalResponse.CreateServiceSuccess(
                    new ExternalServiceItem(
                        "Directory",
                        "AB12",
                        "service.internal",
                        21000,
                        utcNow));

            Assert.ThrowsExactly<ArgumentException>(
                () => ExternalXmlCodec.SerializeServiceResponse(health));
            Assert.ThrowsExactly<ArgumentException>(
                () => ExternalXmlCodec.SerializeRegistrationResponse(
                    service));
            Assert.ThrowsExactly<ArgumentException>(
                () => ExternalXmlCodec.SerializeErrorResponse(health));
        }

        [TestMethod]
        public void ResponseModelsRequireUtcDateTimeKinds()
        {
            DateTime local = DateTime.SpecifyKind(
                new DateTime(2026, 7, 17, 0, 0, 0),
                DateTimeKind.Local);
            DateTime unspecified = DateTime.SpecifyKind(
                new DateTime(2026, 7, 17, 0, 0, 0),
                DateTimeKind.Unspecified);

            Assert.ThrowsExactly<ArgumentException>(
                () => ExternalResponse.CreateHealthSuccess(local));
            Assert.ThrowsExactly<ArgumentException>(
                () => ExternalResponse.CreateHealthSuccess(unspecified));
            Assert.ThrowsExactly<ArgumentException>(
                () => new ExternalServiceItem(
                    "Directory",
                    "AB12",
                    "service.internal",
                    21000,
                    local));
            Assert.ThrowsExactly<ArgumentException>(
                () => new ExternalServiceItem(
                    "Directory",
                    "AB12",
                    "service.internal",
                    21000,
                    unspecified));
        }

        private static string CreateValidRegistrationChildren()
        {
            return "<Name>Directory</Name>"
                + "<ProductCode>AB12</ProductCode>"
                + "<ServerAddress>service.internal</ServerAddress>"
                + "<Port>21000</Port>";
        }

        private static string CreateMaximumLengthDnsName()
        {
            return new string('a', 63)
                + "."
                + new string('b', 63)
                + "."
                + new string('c', 63)
                + "."
                + new string('d', 61);
        }

        private static string Repeat(string value, int count)
        {
            var builder = new StringBuilder(value.Length * count);
            for (int index = 0; index < count; index++)
            {
                builder.Append(value);
            }

            return builder.ToString();
        }

        private static string CreateRegistrationDocument(
            string name,
            string productCode,
            string serverAddress,
            string port)
        {
            return "<RegistrationRequest xmlns=\""
                + XmlNamespace
                + "\"><Name>"
                + name
                + "</Name><ProductCode>"
                + productCode
                + "</ProductCode><ServerAddress>"
                + serverAddress
                + "</ServerAddress><Port>"
                + port
                + "</Port></RegistrationRequest>";
        }

        private static void AssertRegistrationRejected(string xml)
        {
            Assert.ThrowsExactly<ExternalProtocolException>(
                () => ExternalXmlCodec.ParseRegistrationRequest(
                    Encode(xml)));
        }

        private static byte[] Encode(string value)
        {
            return StrictUtf8.GetBytes(value);
        }

        private static string DecodeBomless(byte[] body)
        {
            Assert.IsNotNull(body);
            Assert.IsTrue(body.Length > 0);
            Assert.IsFalse(
                body.Length >= 3
                    && body[0] == 0xef
                    && body[1] == 0xbb
                    && body[2] == 0xbf,
                "The external XML response must not contain a UTF-8 BOM.");

            string xml = StrictUtf8.GetString(body);
            Assert.IsFalse(
                xml.Length > 0 && xml[0] == '\ufeff',
                "The external XML response must not begin with U+FEFF.");
            return xml;
        }

        private static void AssertResponseEnvelope(
            string xml,
            string result,
            string code)
        {
            Assert.IsTrue(
                xml.StartsWith(
                    "<Response xmlns=\"" + XmlNamespace + "\">",
                    StringComparison.Ordinal));
            StringAssert.Contains(
                xml,
                "<Result>" + result + "</Result>");
            StringAssert.Contains(
                xml,
                "<Code>" + code + "</Code>");
            StringAssert.EndsWith(xml, "</Response>");
        }

        private static void AssertElementOrder(
            string xml,
            params string[] elementMarkers)
        {
            int previousIndex = -1;
            foreach (string marker in elementMarkers)
            {
                int currentIndex = xml.IndexOf(
                    marker,
                    StringComparison.Ordinal);
                Assert.IsTrue(
                    currentIndex > previousIndex,
                    "Element marker is missing or out of order: "
                        + marker);
                previousIndex = currentIndex;
            }
        }

        private static void AssertInternalFieldsAreAbsent(string xml)
        {
            string[] forbiddenMarkers =
            {
                "<Deleted>",
                "<DeletedUtc>",
                "<LogicalVersion>",
                "<OriginInstanceId>",
                "<InstanceId>",
                "<ApiVersion>",
                "<ProtocolVersion>",
                "<ProductVersion>",
                "<Version>",
                "<Build>",
                "<Patch>",
                "<PeerInstanceId>",
                "<InternalPath>",
                "<StackTrace>"
            };

            foreach (string forbiddenMarker in forbiddenMarkers)
            {
                Assert.DoesNotContain(forbiddenMarker, xml);
            }
        }
    }
}
