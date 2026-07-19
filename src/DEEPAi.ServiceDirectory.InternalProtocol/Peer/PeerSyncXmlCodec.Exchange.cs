using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Peer
{
    public static partial class PeerSyncXmlCodec
    {
        // Authentication, endpoint and live-session validation must complete
        // before this parser is called. This method only validates XML wire data.
        public static PeerPullExchangeRequest ParseAuthenticatedPullRequest(
            byte[] body)
        {
            string xml = DecodeAndInspectAuthenticatedBody(body);
            XElement root = LoadSchemaValidatedRoot(
                xml,
                body.Length,
                "Exchange");
            EnsurePullExchangeShape(root, "PullRequest");
            EnsureOnlyContractAttributes(root);

            XElement pullRequest = root.Element(Namespace + "PullRequest");
            return new PeerPullExchangeRequest(
                ReadCanonicalGuid(pullRequest, "SnapshotId"),
                ReadCanonicalUInt32(pullRequest, "BatchIndex"));
        }

        public static byte[] SerializePushRequest(
            PeerPushExchangeRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            byte[] body = SerializeDocument(
                writer =>
                {
                    writer.WriteStartElement(
                        "Exchange",
                        PeerSyncContract.XmlNamespace);
                    writer.WriteAttributeString("Mode", "Push");
                    WriteSyncData(writer, request);
                    writer.WriteEndElement();
                });
            EnsureSerializedBodyLimit(body);
            ParseAuthenticatedPushRequest(body);
            return body;
        }

        public static byte[] SerializePullRequest(
            PeerPullExchangeRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            byte[] body = SerializeDocument(
                writer =>
                {
                    writer.WriteStartElement(
                        "Exchange",
                        PeerSyncContract.XmlNamespace);
                    writer.WriteAttributeString("Mode", "Pull");
                    writer.WriteStartElement(
                        "PullRequest",
                        PeerSyncContract.XmlNamespace);
                    WriteGuidElement(
                        writer,
                        "SnapshotId",
                        request.SnapshotId);
                    WriteUInt32Element(
                        writer,
                        "BatchIndex",
                        request.BatchIndex);
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                });
            EnsureSerializedBodyLimit(body);
            ParseAuthenticatedPullRequest(body);
            return body;
        }

        // The exact raw response bytes must pass the response MAC before this
        // parser is called. Extensions are ignored only in their designated
        // final envelope position as permitted by peer.xsd.
        public static PeerExchangeResponse ParseAuthenticatedExchangeResponse(
            byte[] body)
        {
            string xml = DecodeAndInspectAuthenticatedBody(body);
            XElement root = LoadSchemaValidatedRoot(
                xml,
                body.Length,
                "Response");
            EnsureOnlyResponseContractAttributes(root);

            string result = ReadRequiredValue(root, "Result");
            uint rawCode = ReadCanonicalUInt32(root, "Code");
            PeerSyncResponseCode code = (PeerSyncResponseCode)rawCode;
            if (!Enum.IsDefined(typeof(PeerSyncResponseCode), code))
            {
                throw InvalidRequest(
                    "The Peer response contains an unknown result code.");
            }

            string message = ReadRequiredValue(root, "Message");
            List<XElement> payloadElements = GetResponsePayloadElements(root);
            bool isOk = StringComparer.Ordinal.Equals(result, "OK");
            bool isError = StringComparer.Ordinal.Equals(result, "ERROR");
            if (!isOk && !isError)
            {
                throw InvalidRequest(
                    "The Peer response Result is invalid.");
            }

            if (isError)
            {
                if (code == PeerSyncResponseCode.Ok
                    || payloadElements.Count != 0)
                {
                    throw InvalidRequest(
                        "A Peer error response cannot use Code=0 or contain an exchange payload.");
                }

                return PeerExchangeResponse.CreateParsedError(code, message);
            }

            if (code != PeerSyncResponseCode.Ok
                || payloadElements.Count != 1)
            {
                throw InvalidRequest(
                    "A successful Peer response requires Code=0 and one exchange payload.");
            }

            XElement payload = payloadElements[0];
            if (payload.Name == Namespace + "ExchangeAck")
            {
                return PeerExchangeResponse.CreateParsedPushSuccess(
                    ParseExchangeAcknowledgement(payload),
                    message);
            }

            if (payload.Name == Namespace + "Exchange")
            {
                EnsurePullExchangeShape(payload, "SyncData");
                ParsedSyncData parsed = ParseSyncData(
                    payload.Element(Namespace + "SyncData"));
                return PeerExchangeResponse.CreateParsedPullSuccess(
                    parsed.CreatePullBatch(),
                    message);
            }

            throw InvalidRequest(
                "The successful Peer response payload is not an exchange result.");
        }

        public static byte[] SerializeExchangeResponse(
            PeerExchangeResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            byte[] body = SerializeDocument(
                writer => WriteExchangeResponse(writer, response));
            EnsureSerializedBodyLimit(body);
            ParseAuthenticatedExchangeResponse(body);
            return body;
        }

        private static void WriteExchangeResponse(
            XmlWriter writer,
            PeerExchangeResponse response)
        {
            writer.WriteStartElement(
                "Response",
                PeerSyncContract.XmlNamespace);
            writer.WriteElementString(
                "Result",
                PeerSyncContract.XmlNamespace,
                response.IsSuccess ? "OK" : "ERROR");
            writer.WriteElementString(
                "Code",
                PeerSyncContract.XmlNamespace,
                ((uint)response.Code).ToString(
                    CultureInfo.InvariantCulture));

            // Never reflect a parsed remote message or an exception into an
            // outbound response. The fixed schema permits an empty Message.
            writer.WriteElementString(
                "Message",
                PeerSyncContract.XmlNamespace,
                string.Empty);

            switch (response.Kind)
            {
                case PeerExchangeResponseKind.PushAcknowledgement:
                    WriteExchangeAcknowledgement(
                        writer,
                        response.Acknowledgement);
                    break;
                case PeerExchangeResponseKind.PullBatch:
                    writer.WriteStartElement(
                        "Exchange",
                        PeerSyncContract.XmlNamespace);
                    writer.WriteAttributeString("Mode", "Pull");
                    WriteSyncData(writer, response.PullBatch);
                    writer.WriteEndElement();
                    break;
                case PeerExchangeResponseKind.Error:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(response),
                        "The Peer response kind is invalid.");
            }

            writer.WriteEndElement();
        }

        private static PeerExchangeAcknowledgement
            ParseExchangeAcknowledgement(XElement acknowledgement)
        {
            string mode = ReadRequiredValue(acknowledgement, "Mode");
            if (!StringComparer.Ordinal.Equals(mode, "Push"))
            {
                throw InvalidRequest(
                    "A Peer exchange acknowledgement must use Mode=Push.");
            }

            return new PeerExchangeAcknowledgement(
                ReadCanonicalGuid(acknowledgement, "SnapshotId"),
                ReadCanonicalUInt32(acknowledgement, "BatchIndex"),
                ReadOptionalCanonicalGuid(
                    acknowledgement,
                    "ServerSnapshotId"));
        }

        private static void WriteExchangeAcknowledgement(
            XmlWriter writer,
            PeerExchangeAcknowledgement acknowledgement)
        {
            if (acknowledgement == null)
            {
                throw new ArgumentNullException(nameof(acknowledgement));
            }

            writer.WriteStartElement(
                "ExchangeAck",
                PeerSyncContract.XmlNamespace);
            writer.WriteElementString(
                "Mode",
                PeerSyncContract.XmlNamespace,
                "Push");
            WriteGuidElement(
                writer,
                "SnapshotId",
                acknowledgement.SnapshotId);
            WriteUInt32Element(
                writer,
                "BatchIndex",
                acknowledgement.BatchIndex);
            if (acknowledgement.ServerSnapshotId.HasValue)
            {
                WriteGuidElement(
                    writer,
                    "ServerSnapshotId",
                    acknowledgement.ServerSnapshotId.Value);
            }

            writer.WriteEndElement();
        }

        private static void WriteSyncData(
            XmlWriter writer,
            PeerSyncDataBatch batch)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            writer.WriteStartElement(
                "SyncData",
                PeerSyncContract.XmlNamespace);
            WriteGuidElement(writer, "InstanceId", batch.InstanceId);
            WriteGuidElement(writer, "SnapshotId", batch.SnapshotId);
            WriteUInt64Element(
                writer,
                "LogicalClock",
                batch.LogicalClock);
            WriteUInt32Element(writer, "BatchIndex", batch.BatchIndex);
            WriteUInt64Element(writer, "TotalCount", batch.TotalCount);
            writer.WriteElementString(
                "IsLastBatch",
                PeerSyncContract.XmlNamespace,
                batch.IsLastBatch ? "true" : "false");
            writer.WriteStartElement(
                "Items",
                PeerSyncContract.XmlNamespace);
            for (int index = 0; index < batch.Items.Count; index++)
            {
                WriteService(writer, batch.Items[index]);
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        private static void WriteService(
            XmlWriter writer,
            PeerSyncServiceItem item)
        {
            writer.WriteStartElement(
                "Service",
                PeerSyncContract.XmlNamespace);
            writer.WriteElementString(
                "Name",
                PeerSyncContract.XmlNamespace,
                item.Name);
            writer.WriteElementString(
                "ProductCode",
                PeerSyncContract.XmlNamespace,
                item.ProductCode);
            writer.WriteElementString(
                "ServerAddress",
                PeerSyncContract.XmlNamespace,
                item.ServerAddress);
            writer.WriteElementString(
                "Port",
                PeerSyncContract.XmlNamespace,
                item.Port.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString(
                "LastModifiedUtc",
                PeerSyncContract.XmlNamespace,
                FormatUtc(item.LastModifiedUtc));
            writer.WriteElementString(
                "Deleted",
                PeerSyncContract.XmlNamespace,
                item.Deleted ? "true" : "false");
            if (item.DeletedUtc.HasValue)
            {
                writer.WriteElementString(
                    "DeletedUtc",
                    PeerSyncContract.XmlNamespace,
                    FormatUtc(item.DeletedUtc.Value));
            }

            WriteUInt64Element(
                writer,
                "LogicalVersion",
                item.LogicalVersion);
            WriteGuidElement(
                writer,
                "OriginInstanceId",
                item.OriginInstanceId);
            writer.WriteEndElement();
        }

        private static void EnsurePullExchangeShape(
            XElement exchange,
            string expectedChildName)
        {
            if (string.IsNullOrEmpty(expectedChildName))
            {
                throw new ArgumentException(
                    "The expected Pull payload name is required.",
                    nameof(expectedChildName));
            }

            XAttribute mode = exchange.Attribute("Mode");
            if (mode == null
                || !StringComparer.Ordinal.Equals(mode.Value, "Pull"))
            {
                throw InvalidRequest(
                    "A Peer Pull exchange must use Mode=Pull.");
            }

            int childCount = 0;
            XElement onlyChild = null;
            foreach (XElement child in exchange.Elements())
            {
                childCount++;
                onlyChild = child;
            }

            if (childCount != 1
                || onlyChild.Name != Namespace + expectedChildName)
            {
                throw InvalidRequest(
                    "A Peer Pull exchange has an invalid payload shape.");
            }
        }

        private static List<XElement> GetResponsePayloadElements(
            XElement response)
        {
            var payloads = new List<XElement>();
            foreach (XElement child in response.Elements())
            {
                if (child.Name == Namespace + "Result"
                    || child.Name == Namespace + "Code"
                    || child.Name == Namespace + "Message"
                    || child.Name == Namespace + "Extensions")
                {
                    continue;
                }

                payloads.Add(child);
            }

            return payloads;
        }

        private static Guid? ReadOptionalCanonicalGuid(
            XElement parent,
            string name)
        {
            return parent.Element(Namespace + name) == null
                ? (Guid?)null
                : ReadCanonicalGuid(parent, name);
        }

        private static void WriteGuidElement(
            XmlWriter writer,
            string name,
            Guid value)
        {
            if (value == Guid.Empty)
            {
                throw new ArgumentException(
                    "A Peer GUID cannot be empty.",
                    nameof(value));
            }

            writer.WriteElementString(
                name,
                PeerSyncContract.XmlNamespace,
                value.ToString("D").ToLowerInvariant());
        }

        private static void WriteUInt32Element(
            XmlWriter writer,
            string name,
            uint value)
        {
            writer.WriteElementString(
                name,
                PeerSyncContract.XmlNamespace,
                value.ToString(CultureInfo.InvariantCulture));
        }

        private static void WriteUInt64Element(
            XmlWriter writer,
            string name,
            ulong value)
        {
            writer.WriteElementString(
                name,
                PeerSyncContract.XmlNamespace,
                value.ToString(CultureInfo.InvariantCulture));
        }

        private static byte[] SerializeDocument(Action<XmlWriter> write)
        {
            if (write == null)
            {
                throw new ArgumentNullException(nameof(write));
            }

            var settings = new XmlWriterSettings
            {
                CheckCharacters = true,
                CloseOutput = false,
                Encoding = StrictUtf8,
                Indent = false,
                NewLineHandling = NewLineHandling.None,
                OmitXmlDeclaration = true
            };
            using (var stream = new MemoryStream())
            {
                using (XmlWriter writer = XmlWriter.Create(stream, settings))
                {
                    write(writer);
                }

                return stream.ToArray();
            }
        }

        private static void EnsureSerializedBodyLimit(byte[] body)
        {
            if (body.Length > PeerSyncContract.MaximumExchangeBodyBytes)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.BodyTooLarge,
                    "The serialized Peer sync body exceeds the raw byte limit.");
            }
        }
    }
}
