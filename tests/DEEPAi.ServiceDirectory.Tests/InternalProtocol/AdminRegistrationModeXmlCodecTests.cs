using System;
using System.IO;
using System.Text;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.InternalProtocol
{
    [TestClass]
    public sealed class AdminRegistrationModeXmlCodecTests
    {
        private const string Namespace =
            "urn:deepai:service-directory:admin";
        private const string SerialNumber =
            "01A4B5C6D7E8F90123456789ABCDEF01";

        [TestMethod]
        public void OpenModeAndLastSuccessRoundTripCanonicalXml()
        {
            DateTime openedUtc = Utc(2026, 7, 20, 2, 0, 0);
            var mode = new AdminRegistrationModeStatus(
                AdminRegistrationModeState.Open,
                openedUtc,
                openedUtc.AddHours(1),
                3471);
            AdminLastRegistration last =
                AdminLastRegistration.CreateSuccess(
                    Utc(2026, 7, 19, 8, 30, 0),
                    AdminRegistrationOutcome.Registered,
                    "ABCD",
                    "vms-bridge.example.local",
                    "10.0.0.5",
                    SerialNumber,
                    Utc(2027, 7, 19, 8, 30, 0));

            byte[] body = AdminRegistrationModeXmlCodec
                .SerializeRegistrationModeResponse(
                    new AdminServerRegistrationModeResponse(mode, last));
            string xml = Decode(body);
            AdminResponse<AdminServerRegistrationModeResponse> parsed =
                AdminRegistrationModeXmlCodec
                    .ParseRegistrationModeResponse(body);

            Assert.IsTrue(parsed.IsSuccess);
            Assert.AreEqual(
                AdminRegistrationModeState.Open,
                parsed.Payload.RegistrationMode.State);
            Assert.AreEqual(3471,
                parsed.Payload.RegistrationMode.RemainingSeconds);
            Assert.AreEqual(
                AdminRegistrationOutcome.Registered,
                parsed.Payload.LastRegistration.Outcome);
            Assert.AreEqual(
                "vms-bridge.example.local",
                parsed.Payload.LastRegistration.ServiceHostName);
            StringAssert.Contains(xml, "<State>OPEN</State>");
            StringAssert.Contains(
                xml,
                "<RemainingSeconds>3471</RemainingSeconds>");
            StringAssert.Contains(
                xml,
                "<Outcome>REGISTERED</Outcome>");
            Assert.DoesNotContain("Pending", xml);
        }

        [TestMethod]
        [DataRow(AdminRegistrationModeState.Closed, "CLOSED")]
        [DataRow(AdminRegistrationModeState.Claimed, "CLAIMED")]
        public void NonOpenModesOmitAllTimingValues(
            AdminRegistrationModeState state,
            string expectedText)
        {
            var response = new AdminServerRegistrationModeResponse(
                new AdminRegistrationModeStatus(
                    state,
                    null,
                    null,
                    null),
                null);

            string xml = Decode(
                AdminRegistrationModeXmlCodec
                    .SerializeRegistrationModeResponse(response));

            StringAssert.Contains(
                xml,
                "<State>" + expectedText + "</State>");
            Assert.DoesNotContain("OpenedUtc", xml);
            Assert.DoesNotContain("ExpiresUtc", xml);
            Assert.DoesNotContain("RemainingSeconds", xml);
            Assert.DoesNotContain("LastRegistration", xml);
        }

        [TestMethod]
        public void FailedLastRegistrationContainsOnlySafeFailureShape()
        {
            var response = new AdminServerRegistrationModeResponse(
                new AdminRegistrationModeStatus(
                    AdminRegistrationModeState.Closed,
                    null,
                    null,
                    null),
                AdminLastRegistration.CreateFailure(
                    Utc(2026, 7, 20, 2, 10, 0),
                    "Certificate issuance did not complete."));

            byte[] body = AdminRegistrationModeXmlCodec
                .SerializeRegistrationModeResponse(response);
            string xml = Decode(body);
            AdminLastRegistration parsed = AdminRegistrationModeXmlCodec
                .ParseRegistrationModeResponse(body)
                .Payload
                .LastRegistration;

            Assert.AreEqual(AdminRegistrationOutcome.Failed, parsed.Outcome);
            Assert.AreEqual(
                "Certificate issuance did not complete.",
                parsed.FailureReason);
            StringAssert.Contains(xml, "<Outcome>FAILED</Outcome>");
            Assert.DoesNotContain("ProductCode", xml);
            Assert.DoesNotContain("ServiceHostName", xml);
            Assert.DoesNotContain("CertificateSerialNumber", xml);
        }

        [TestMethod]
        public void ModelsRejectInvalidStateAndLastResultCombinations()
        {
            DateTime openedUtc = Utc(2026, 7, 20, 2, 0, 0);
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminRegistrationModeStatus(
                    AdminRegistrationModeState.Closed,
                    openedUtc,
                    openedUtc.AddHours(1),
                    3600));
            Assert.ThrowsExactly<ArgumentException>(
                () => new AdminRegistrationModeStatus(
                    AdminRegistrationModeState.Open,
                    openedUtc,
                    openedUtc.AddMinutes(59),
                    3540));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new AdminRegistrationModeStatus(
                    AdminRegistrationModeState.Open,
                    openedUtc,
                    openedUtc.AddHours(1),
                    3601));
            Assert.ThrowsExactly<ArgumentException>(
                () => AdminLastRegistration.CreateSuccess(
                    Utc(2026, 7, 20, 2, 0, 0),
                    AdminRegistrationOutcome.Registered,
                    "ABCD",
                    "2001:db8::1",
                    "10.0.0.5",
                    SerialNumber,
                    Utc(2027, 7, 20, 2, 0, 0)));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => AdminLastRegistration.CreateFailure(
                    Utc(2026, 7, 20, 2, 0, 0),
                    "   "));
        }

        [TestMethod]
        public void ParserRejectsUnknownPartialAndLegacyPendingPayloads()
        {
            string valid = Decode(
                AdminRegistrationModeXmlCodec
                    .SerializeRegistrationModeResponse(
                        new AdminServerRegistrationModeResponse(
                            new AdminRegistrationModeStatus(
                                AdminRegistrationModeState.Open,
                                Utc(2026, 7, 20, 2, 0, 0),
                                Utc(2026, 7, 20, 3, 0, 0),
                                3600),
                            null)));
            string[] invalid =
            {
                valid.Replace(
                    "<ExpiresUtc>2026-07-20T03:00:00Z</ExpiresUtc>",
                    string.Empty),
                valid.Replace(
                    "</RegistrationMode>",
                    "<Unknown>x</Unknown></RegistrationMode>"),
                valid.Replace("<State>OPEN</State>", "<State>CLOSED</State>"),
                "<Response xmlns=\"" + Namespace + "\">"
                    + "<Result>OK</Result><Code>0</Code><Message />"
                    + "<PendingItems /><TotalCount>0</TotalCount>"
                    + "</Response>"
            };

            foreach (string xml in invalid)
            {
                Assert.ThrowsExactly<AdminProtocolException>(
                    () => AdminRegistrationModeXmlCodec
                        .ParseRegistrationModeResponse(Encode(xml)));
            }
        }

        [TestMethod]
        public void ParserRejectsUnsafeBodiesAndUnexpectedSuccessPayloads()
        {
            Assert.ThrowsExactly<ArgumentNullException>(
                () => AdminRegistrationModeXmlCodec
                    .ParseRegistrationModeResponse(null));
            Assert.ThrowsExactly<AdminProtocolException>(
                () => AdminRegistrationModeXmlCodec
                    .ParseRegistrationModeResponse(new byte[0]));
            Assert.ThrowsExactly<AdminProtocolException>(
                () => AdminRegistrationModeXmlCodec
                    .ParseRegistrationModeResponse(
                        new byte[] { 0xc3, 0x28 }));
            Assert.ThrowsExactly<AdminProtocolException>(
                () => AdminRegistrationModeXmlCodec
                    .ParseRegistrationModeResponse(
                        Encode(
                            "<!DOCTYPE Response [<!ENTITY x 'y'>]>"
                            + "<Response xmlns=\""
                            + Namespace
                            + "\"><Result>OK</Result><Code>0</Code>"
                            + "<Message>&x;</Message><RegistrationMode>"
                            + "<State>CLOSED</State></RegistrationMode>"
                            + "</Response>")));
            Assert.ThrowsExactly<AdminProtocolException>(
                () => AdminRegistrationModeXmlCodec
                    .ParseRegistrationModeResponse(
                        Encode(
                            "<Response xmlns=\""
                            + Namespace
                            + "\"><Result>OK</Result><Code>0</Code>"
                            + "<Message /></Response>")));
        }

        [TestMethod]
        public void TargetSchemaContainsRegistrationModeAndNoPendingWire()
        {
            const string resourceName =
                "DEEPAi.ServiceDirectory.InternalProtocol.Admin.admin.xsd";
            using (Stream stream = typeof(AdminRegistrationModeXmlCodec)
                .Assembly
                .GetManifestResourceStream(resourceName))
            {
                Assert.IsNotNull(stream);
                using (var reader = new StreamReader(
                    stream,
                    new UTF8Encoding(false, true),
                    true))
                {
                    string schema = reader.ReadToEnd();
                    StringAssert.Contains(schema, "RegistrationModeType");
                    StringAssert.Contains(schema, "LastRegistrationType");
                    Assert.DoesNotContain("PendingItems", schema);
                    Assert.DoesNotContain("PendingItemType", schema);
                }
            }
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

        private static byte[] Encode(string value)
        {
            return new UTF8Encoding(false, true).GetBytes(value);
        }

        private static string Decode(byte[] value)
        {
            return new UTF8Encoding(false, true).GetString(value);
        }
    }
}
