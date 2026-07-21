using System;
using System.Globalization;
using System.Text;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.InternalProtocol
{
    [TestClass]
    public sealed class PeerExchangeXmlCodecTests
    {
        private const string XmlNamespace =
            "urn:deepai:service-directory:peer";
        private const string InstanceId =
            "7a1c3bb2-9e8b-4a8d-b404-f670f746eb77";
        private const string SnapshotId =
            "6f248a04-cc3e-409a-b499-cb571e6d30b7";
        private const string ServerSnapshotId =
            "83c69c5b-6464-4ce6-a3ce-48e68b541bc2";
        private const string OriginInstanceId =
            "9f2ed127-9834-42b4-a379-eaad9df8fcec";

        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void PullRequestParseAndSerializeUseCanonicalWireValues()
        {
            string xml = CreatePullRequestDocument(
                SnapshotId,
                uint.MaxValue.ToString(CultureInfo.InvariantCulture));

            PeerPullExchangeRequest parsed = PeerSyncXmlCodec
                .ParseAuthenticatedPullRequest(Encode(xml));

            Assert.AreEqual(new Guid(SnapshotId), parsed.SnapshotId);
            Assert.AreEqual(uint.MaxValue, parsed.BatchIndex);

            byte[] serialized = PeerSyncXmlCodec.SerializePullRequest(
                parsed);
            AssertNoBomOrDeclaration(serialized);
            PeerPullExchangeRequest roundTrip = PeerSyncXmlCodec
                .ParseAuthenticatedPullRequest(serialized);
            Assert.AreEqual(parsed.SnapshotId, roundTrip.SnapshotId);
            Assert.AreEqual(parsed.BatchIndex, roundTrip.BatchIndex);
        }

        [TestMethod]
        public void PullRequestRejectsModePayloadMismatchAndNonCanonicalValues()
        {
            string valid = CreatePullRequestDocument(SnapshotId, "0");
            string[] invalidDocuments =
            {
                valid.Replace("Mode=\"Pull\"", "Mode=\"Push\""),
                valid.Replace(
                    "<PullRequest>",
                    "<SyncData>").Replace(
                    "</PullRequest>",
                    "</SyncData>"),
                valid.Replace(SnapshotId, SnapshotId.ToUpperInvariant()),
                valid.Replace("<BatchIndex>0</BatchIndex>",
                    "<BatchIndex>00</BatchIndex>"),
                valid.Replace("Mode=\"Pull\"",
                    "Mode=\"Pull\" Unexpected=\"true\""),
                valid.Replace(XmlNamespace, "urn:wrong")
            };

            foreach (string invalidDocument in invalidDocuments)
            {
                AssertPullRejected(invalidDocument);
            }
        }

        [TestMethod]
        public void PushSerializerRoundTripsActiveAndDeletedServices()
        {
            var request = new PeerPushExchangeRequest(
                new Guid(InstanceId),
                new Guid(SnapshotId),
                9,
                4,
                2,
                true,
                new[]
                {
                    CreateServiceItem("ABCD", 8, false),
                    CreateServiceItem("WXYZ", 9, true)
                });

            byte[] serialized = PeerSyncXmlCodec.SerializePushRequest(
                request);
            AssertNoBomOrDeclaration(serialized);
            string xml = StrictUtf8.GetString(serialized);
            StringAssert.Contains(xml, "Mode=\"Push\"");
            StringAssert.Contains(xml, "<IsLastBatch>true</IsLastBatch>");
            StringAssert.Contains(xml, "<DeletedUtc>");
            Assert.IsTrue(
                xml.IndexOf("<ProductCode>ABCD</ProductCode>",
                    StringComparison.Ordinal)
                < xml.IndexOf("<ProductCode>WXYZ</ProductCode>",
                    StringComparison.Ordinal));

            PeerPushExchangeRequest roundTrip = PeerSyncXmlCodec
                .ParseAuthenticatedPushRequest(serialized);
            Assert.AreEqual(request.InstanceId, roundTrip.InstanceId);
            Assert.AreEqual(request.SnapshotId, roundTrip.SnapshotId);
            Assert.AreEqual(request.LogicalClock, roundTrip.LogicalClock);
            Assert.AreEqual(request.BatchIndex, roundTrip.BatchIndex);
            Assert.AreEqual(request.TotalCount, roundTrip.TotalCount);
            Assert.AreEqual(2, roundTrip.Items.Count);
            Assert.IsFalse(roundTrip.Items[0].Deleted);
            Assert.IsTrue(roundTrip.Items[1].Deleted);
        }

        [TestMethod]
        public void BatchModelRejectsUnsortedAndInconsistentItems()
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => new PeerPushExchangeRequest(
                    new Guid(InstanceId),
                    new Guid(SnapshotId),
                    9,
                    0,
                    2,
                    true,
                    new[]
                    {
                        CreateServiceItem("WXYZ", 9, false),
                        CreateServiceItem("ABCD", 8, false)
                    }));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new PeerPushExchangeRequest(
                    new Guid(InstanceId),
                    new Guid(SnapshotId),
                    9,
                    0,
                    0,
                    true,
                    new[] { CreateServiceItem("ABCD", 8, false) }));
            Assert.ThrowsExactly<ArgumentException>(
                () => new PeerPushExchangeRequest(
                    new Guid(InstanceId),
                    new Guid(SnapshotId),
                    7,
                    0,
                    1,
                    true,
                    new[] { CreateServiceItem("ABCD", 8, false) }));
        }

        [TestMethod]
        public void ServiceModelAndPushParserRejectNonCanonicalValues()
        {
            DateTime utc = new DateTime(
                2026,
                7,
                18,
                0,
                0,
                0,
                DateTimeKind.Utc);
            Assert.ThrowsExactly<ArgumentException>(
                () => new PeerSyncServiceItem(
                    " App ",
                    "ABCD",
                    "service.internal",
                    "10.0.0.5",
                    21000,
                    utc,
                    false,
                    null,
                    1,
                    new Guid(OriginInstanceId)));
            Assert.ThrowsExactly<ArgumentException>(
                () => new PeerSyncServiceItem(
                    "App",
                    "ABCD",
                    " service.internal ",
                    "10.0.0.5",
                    21000,
                    utc,
                    false,
                    null,
                    1,
                    new Guid(OriginInstanceId)));

            string valid = CreatePushRequestDocument(
                CreateServiceXml("ABCD", "1", false),
                "1",
                "0",
                "1",
                "true");
            AssertPushRejected(
                valid.Replace(
                    "<Name>App ABCD</Name>",
                    "<Name> App ABCD </Name>"));
            AssertPushRejected(
                valid.Replace(
                    "<ServiceHostName>service.internal</ServiceHostName>",
                    "<ServiceHostName> service.internal </ServiceHostName>"));
        }

        [TestMethod]
        public void PushAcknowledgementResponseRoundTripsOptionalServerSnapshot()
        {
            string responseXml = CreatePushAcknowledgementResponse(
                SnapshotId,
                "1",
                ServerSnapshotId);
            PeerExchangeResponse parsed = PeerSyncXmlCodec
                .ParseAuthenticatedExchangeResponse(Encode(responseXml));

            Assert.IsTrue(parsed.IsSuccess);
            Assert.AreEqual(
                PeerExchangeResponseKind.PushAcknowledgement,
                parsed.Kind);
            Assert.AreEqual(PeerSyncResponseCode.Ok, parsed.Code);
            Assert.AreEqual(
                new Guid(SnapshotId),
                parsed.Acknowledgement.SnapshotId);
            Assert.AreEqual((uint)1, parsed.Acknowledgement.BatchIndex);
            Assert.AreEqual(
                new Guid(ServerSnapshotId),
                parsed.Acknowledgement.ServerSnapshotId.Value);

            byte[] serialized = PeerSyncXmlCodec.SerializeExchangeResponse(
                PeerExchangeResponse.CreatePushSuccess(
                    parsed.Acknowledgement));
            AssertNoBomOrDeclaration(serialized);
            PeerExchangeResponse roundTrip = PeerSyncXmlCodec
                .ParseAuthenticatedExchangeResponse(serialized);
            Assert.AreEqual(parsed.Kind, roundTrip.Kind);
            Assert.AreEqual(
                parsed.Acknowledgement.ServerSnapshotId,
                roundTrip.Acknowledgement.ServerSnapshotId);

            PeerExchangeResponse withoutServer = PeerSyncXmlCodec
                .ParseAuthenticatedExchangeResponse(
                    Encode(CreatePushAcknowledgementResponse(
                        SnapshotId,
                        "0",
                        null)));
            Assert.IsFalse(
                withoutServer.Acknowledgement.ServerSnapshotId.HasValue);
        }

        [TestMethod]
        public void PullSuccessResponseRoundTripsSyncDataBatch()
        {
            string responseXml = CreatePullSuccessResponse(
                CreateServiceXml("ABCD", "8", false)
                    + CreateServiceXml("WXYZ", "9", true),
                "9",
                "0",
                "2",
                "true");

            PeerExchangeResponse parsed = PeerSyncXmlCodec
                .ParseAuthenticatedExchangeResponse(Encode(responseXml));

            Assert.AreEqual(PeerExchangeResponseKind.PullBatch, parsed.Kind);
            Assert.AreEqual(new Guid(InstanceId),
                parsed.PullBatch.InstanceId);
            Assert.AreEqual(new Guid(SnapshotId),
                parsed.PullBatch.SnapshotId);
            Assert.AreEqual(2, parsed.PullBatch.Items.Count);
            Assert.AreEqual("ABCD",
                parsed.PullBatch.Items[0].ProductCode);

            byte[] serialized = PeerSyncXmlCodec.SerializeExchangeResponse(
                PeerExchangeResponse.CreatePullSuccess(parsed.PullBatch));
            AssertNoBomOrDeclaration(serialized);
            PeerExchangeResponse roundTrip = PeerSyncXmlCodec
                .ParseAuthenticatedExchangeResponse(serialized);
            Assert.AreEqual(PeerExchangeResponseKind.PullBatch,
                roundTrip.Kind);
            Assert.AreEqual(2, roundTrip.PullBatch.Items.Count);
            Assert.IsTrue(roundTrip.PullBatch.IsLastBatch);
        }

        [TestMethod]
        public void PullResponseSecureCountFailsAtOneThousandFirstItem()
        {
            var services = new StringBuilder();
            for (int index = 0; index < 1001; index++)
            {
                services.Append("<Service />");
            }

            PeerSyncProtocolException exception =
                AssertResponseFailure(
                    CreatePullSuccessResponse(
                        services.ToString(),
                        "1",
                        "0",
                        "1001",
                        "true"),
                    PeerSyncProtocolFailure.ItemLimitExceeded);
            StringAssert.Contains(exception.Message, "1,000");
        }

        [TestMethod]
        public void ErrorResponseRejectsExtensionsAndSerializesWithoutReflection()
        {
            string remoteMessage = "remote detail must not be reflected";
            string xml = "<Response xmlns=\""
                + XmlNamespace
                + "\"><Result>ERROR</Result>"
                + "<Code>2005</Code><Message>"
                + remoteMessage
                + "</Message></Response>";

            PeerExchangeResponse parsed = PeerSyncXmlCodec
                .ParseAuthenticatedExchangeResponse(Encode(xml));

            Assert.IsFalse(parsed.IsSuccess);
            Assert.AreEqual(PeerExchangeResponseKind.Error, parsed.Kind);
            Assert.AreEqual(
                PeerSyncResponseCode.RevisionCollision,
                parsed.Code);
            Assert.AreEqual(remoteMessage, parsed.Message);

            AssertResponseFailure(
                xml.Replace(
                    "</Response>",
                    "<Extensions><Future /></Extensions></Response>"),
                PeerSyncProtocolFailure.InvalidRequest);

            string xsiTypedPayload = CreatePushAcknowledgementResponse(
                SnapshotId,
                "1",
                ServerSnapshotId)
                .Replace(
                    "<Response xmlns=\"" + XmlNamespace + "\">",
                    "<Response xmlns=\"" + XmlNamespace
                        + "\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\""
                        + " xmlns:peer=\"" + XmlNamespace + "\">")
                .Replace(
                    "<ExchangeAck>",
                    "<ExchangeAck xsi:type=\"peer:ExchangeAckType\">");
            AssertResponseFailure(
                xsiTypedPayload,
                PeerSyncProtocolFailure.InvalidRequest);

            byte[] serialized = PeerSyncXmlCodec.SerializeExchangeResponse(
                parsed);
            string serializedXml = StrictUtf8.GetString(serialized);
            Assert.IsFalse(serializedXml.Contains(remoteMessage));
            PeerExchangeResponse sanitized = PeerSyncXmlCodec
                .ParseAuthenticatedExchangeResponse(serialized);
            Assert.AreEqual(string.Empty, sanitized.Message);
            Assert.AreEqual(parsed.Code, sanitized.Code);
        }

        [TestMethod]
        public void ResponseRejectsResultCodePayloadAndModeMismatches()
        {
            string ack = CreatePushAcknowledgementResponse(
                SnapshotId,
                "0",
                null);
            string pull = CreatePullSuccessResponse(
                CreateServiceXml("ABCD", "1", false),
                "1",
                "0",
                "1",
                "true");
            string[] invalidResponses =
            {
                ack.Replace("<Code>0</Code>", "<Code>2005</Code>"),
                ack.Replace(
                    "<Result>OK</Result><Code>0</Code>",
                    "<Result>ERROR</Result><Code>2005</Code>"),
                ack.Replace("<Result>OK</Result>",
                    "<Result>ERROR</Result>"),
                ack.Replace("<SnapshotId>" + SnapshotId,
                    "<SnapshotId>" + SnapshotId.ToUpperInvariant()),
                ack.Replace("<BatchIndex>0</BatchIndex>",
                    "<BatchIndex>00</BatchIndex>"),
                pull.Replace("Mode=\"Pull\"", "Mode=\"Push\""),
                "<Response xmlns=\"" + XmlNamespace
                    + "\"><Result>OK</Result><Code>0</Code>"
                    + "<Message /></Response>",
                "<Response xmlns=\"" + XmlNamespace
                    + "\"><Result>ERROR</Result><Code>0</Code>"
                    + "<Message /></Response>"
            };

            foreach (string invalidResponse in invalidResponses)
            {
                AssertResponseFailure(
                    invalidResponse,
                    PeerSyncProtocolFailure.InvalidRequest);
            }
        }

        [TestMethod]
        public void ResponseParserRejectsNullInvalidUtf8AndOversizedBody()
        {
            Assert.ThrowsExactly<ArgumentNullException>(
                () => PeerSyncXmlCodec
                    .ParseAuthenticatedExchangeResponse(null));
            Assert.ThrowsExactly<PeerSyncProtocolException>(
                () => PeerSyncXmlCodec.ParseAuthenticatedExchangeResponse(
                    new byte[] { 0xc3, 0x28 }));
            PeerSyncProtocolException tooLarge =
                Assert.ThrowsExactly<PeerSyncProtocolException>(
                    () => PeerSyncXmlCodec
                        .ParseAuthenticatedExchangeResponse(
                            new byte[
                                PeerSyncContract.MaximumExchangeBodyBytes
                                + 1]));
            Assert.AreEqual(
                PeerSyncProtocolFailure.BodyTooLarge,
                tooLarge.Failure);
        }

        private static PeerSyncServiceItem CreateServiceItem(
            string productCode,
            ulong logicalVersion,
            bool deleted)
        {
            DateTime lastModifiedUtc = DateTime.ParseExact(
                "2026-07-18T00:00:00.1234567Z",
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal
                    | DateTimeStyles.AdjustToUniversal);
            DateTime? deletedUtc = deleted
                ? (DateTime?)new DateTime(
                    2026,
                    7,
                    18,
                    1,
                    0,
                    0,
                    DateTimeKind.Utc)
                : null;
            return new PeerSyncServiceItem(
                "App & " + productCode,
                productCode,
                "service.internal",
                "10.0.0.5",
                21000,
                lastModifiedUtc,
                deleted,
                deletedUtc,
                logicalVersion,
                new Guid(OriginInstanceId));
        }

        private static string CreatePullRequestDocument(
            string snapshotId,
            string batchIndex)
        {
            return "<Exchange xmlns=\""
                + XmlNamespace
                + "\" Mode=\"Pull\"><PullRequest><SnapshotId>"
                + snapshotId
                + "</SnapshotId><BatchIndex>"
                + batchIndex
                + "</BatchIndex></PullRequest></Exchange>";
        }

        private static string CreatePushAcknowledgementResponse(
            string snapshotId,
            string batchIndex,
            string serverSnapshotId)
        {
            return "<Response xmlns=\""
                + XmlNamespace
                + "\"><Result>OK</Result><Code>0</Code><Message />"
                + "<ExchangeAck><Mode>Push</Mode><SnapshotId>"
                + snapshotId
                + "</SnapshotId><BatchIndex>"
                + batchIndex
                + "</BatchIndex>"
                + (serverSnapshotId == null
                    ? string.Empty
                    : "<ServerSnapshotId>" + serverSnapshotId
                        + "</ServerSnapshotId>")
                + "</ExchangeAck></Response>";
        }

        private static string CreatePullSuccessResponse(
            string services,
            string logicalClock,
            string batchIndex,
            string totalCount,
            string isLastBatch)
        {
            return "<Response xmlns=\""
                + XmlNamespace
                + "\"><Result>OK</Result><Code>0</Code><Message />"
                + "<Exchange Mode=\"Pull\"><SyncData><InstanceId>"
                + InstanceId
                + "</InstanceId><SnapshotId>"
                + SnapshotId
                + "</SnapshotId><LogicalClock>"
                + logicalClock
                + "</LogicalClock><BatchIndex>"
                + batchIndex
                + "</BatchIndex><TotalCount>"
                + totalCount
                + "</TotalCount><IsLastBatch>"
                + isLastBatch
                + "</IsLastBatch><Items>"
                + services
                + "</Items></SyncData></Exchange></Response>";
        }

        private static string CreatePushRequestDocument(
            string services,
            string logicalClock,
            string batchIndex,
            string totalCount,
            string isLastBatch)
        {
            return "<Exchange xmlns=\""
                + XmlNamespace
                + "\" Mode=\"Push\"><SyncData><InstanceId>"
                + InstanceId
                + "</InstanceId><SnapshotId>"
                + SnapshotId
                + "</SnapshotId><LogicalClock>"
                + logicalClock
                + "</LogicalClock><BatchIndex>"
                + batchIndex
                + "</BatchIndex><TotalCount>"
                + totalCount
                + "</TotalCount><IsLastBatch>"
                + isLastBatch
                + "</IsLastBatch><Items>"
                + services
                + "</Items></SyncData></Exchange>";
        }

        private static string CreateServiceXml(
            string productCode,
            string logicalVersion,
            bool deleted)
        {
            return "<Service><Name>App "
                + productCode
                + "</Name><ProductCode>"
                + productCode
                + "</ProductCode><ServiceHostName>service.internal"
                + "</ServiceHostName><ServiceIpv4Address>10.0.0.5"
                + "</ServiceIpv4Address><Port>21000</Port>"
                + "<LastModifiedUtc>2026-07-18T00:00:00Z"
                + "</LastModifiedUtc><Deleted>"
                + (deleted ? "true" : "false")
                + "</Deleted>"
                + (deleted
                    ? "<DeletedUtc>2026-07-18T01:00:00Z</DeletedUtc>"
                    : string.Empty)
                + "<LogicalVersion>"
                + logicalVersion
                + "</LogicalVersion><OriginInstanceId>"
                + OriginInstanceId
                + "</OriginInstanceId></Service>";
        }

        private static void AssertPullRejected(string xml)
        {
            PeerSyncProtocolException exception =
                Assert.ThrowsExactly<PeerSyncProtocolException>(
                    () => PeerSyncXmlCodec
                        .ParseAuthenticatedPullRequest(Encode(xml)));
            Assert.AreEqual(
                PeerSyncProtocolFailure.InvalidRequest,
                exception.Failure);
        }

        private static void AssertPushRejected(string xml)
        {
            PeerSyncProtocolException exception =
                Assert.ThrowsExactly<PeerSyncProtocolException>(
                    () => PeerSyncXmlCodec
                        .ParseAuthenticatedPushRequest(Encode(xml)));
            Assert.AreEqual(
                PeerSyncProtocolFailure.InvalidRequest,
                exception.Failure);
        }

        private static PeerSyncProtocolException AssertResponseFailure(
            string xml,
            PeerSyncProtocolFailure expectedFailure)
        {
            PeerSyncProtocolException exception =
                Assert.ThrowsExactly<PeerSyncProtocolException>(
                    () => PeerSyncXmlCodec
                        .ParseAuthenticatedExchangeResponse(Encode(xml)));
            Assert.AreEqual(expectedFailure, exception.Failure);
            return exception;
        }

        private static void AssertNoBomOrDeclaration(byte[] body)
        {
            Assert.IsTrue(body.Length > 0);
            Assert.IsFalse(
                body.Length >= 3
                && body[0] == 0xef
                && body[1] == 0xbb
                && body[2] == 0xbf);
            Assert.IsFalse(
                StrictUtf8.GetString(body).StartsWith(
                    "<?xml",
                    StringComparison.Ordinal));
        }

        private static byte[] Encode(string value)
        {
            return StrictUtf8.GetBytes(value);
        }
    }
}
