using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Peer
{
    public static partial class PeerSyncXmlCodec
    {
        // The caller must authenticate the exact raw body, endpoint, peer
        // binding, key epoch and session before trusting the returned value.
        public static PeerHandshakeRequest ParseAuthenticatedHandshakeRequest(
            byte[] body)
        {
            XElement root = LoadControlRoot(body, "Handshake");
            EnsureOnlyContractAttributes(root);

            Guid instanceId = ReadCanonicalGuid(root, "InstanceId");
            Guid peerInstanceId = ReadCanonicalGuid(
                root,
                "PeerInstanceId");
            if (instanceId == peerInstanceId)
            {
                throw InvalidRequest(
                    "Peer handshake instance IDs must be different.");
            }

            ulong keyEpoch = ReadPositiveEpoch(root, "KeyEpoch");
            byte[] nonce = ReadCanonicalBinary(
                root,
                "HandshakeNonce",
                PeerSyncContract.PairingNonceLength);
            try
            {
                return new PeerHandshakeRequest(
                    instanceId,
                    peerInstanceId,
                    keyEpoch,
                    nonce,
                    ReadCanonicalUtc(root, "UtcNow"),
                    ReadCanonicalBoolean(root, "SyncEnabled"));
            }
            finally
            {
                Array.Clear(nonce, 0, nonce.Length);
            }
        }

        public static byte[] SerializeHandshakeRequest(
            PeerHandshakeRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            byte[] body = SerializeDocument(
                writer =>
                {
                    writer.WriteStartElement(
                        "Handshake",
                        PeerSyncContract.XmlNamespace);
                    WriteGuidElement(writer, "InstanceId", request.InstanceId);
                    WriteGuidElement(
                        writer,
                        "PeerInstanceId",
                        request.PeerInstanceId);
                    WriteUInt64Element(writer, "KeyEpoch", request.KeyEpoch);
                    WriteOwnedBase64Element(
                        writer,
                        "HandshakeNonce",
                        request.CopyHandshakeNonce());
                    WriteUtcElement(writer, "UtcNow", request.UtcNow);
                    WriteBooleanElement(
                        writer,
                        "SyncEnabled",
                        request.SyncEnabled);
                    writer.WriteEndElement();
                });
            EnsureControlSerializedBodyLimit(body);
            ParseAuthenticatedHandshakeRequest(body);
            return body;
        }

        // A valid active session and its HMAC must already have been checked.
        public static PeerReleaseRequest ParseAuthenticatedReleaseRequest(
            byte[] body)
        {
            XElement root = LoadControlRoot(body, "Release");
            EnsureOnlyContractAttributes(root);

            byte[] sessionId = ReadCanonicalBinary(
                root,
                "SessionId",
                PeerSyncContract.SessionIdLength);
            try
            {
                return new PeerReleaseRequest(
                    ReadCanonicalGuid(root, "InstanceId"),
                    sessionId);
            }
            finally
            {
                Array.Clear(sessionId, 0, sessionId.Length);
            }
        }

        public static byte[] SerializeReleaseRequest(
            PeerReleaseRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            byte[] body = SerializeDocument(
                writer =>
                {
                    writer.WriteStartElement(
                        "Release",
                        PeerSyncContract.XmlNamespace);
                    WriteGuidElement(writer, "InstanceId", request.InstanceId);
                    WriteOwnedBase64Element(
                        writer,
                        "SessionId",
                        request.CopySessionId());
                    writer.WriteEndElement();
                });
            EnsureControlSerializedBodyLimit(body);
            ParseAuthenticatedReleaseRequest(body);
            return body;
        }

        // Revoke uses the pair-root-derived revoke key and no session ID. The
        // exact raw body must be authenticated before this parser is called.
        public static PeerRevokeRequest ParseAuthenticatedRevokeRequest(
            byte[] body)
        {
            XElement root = LoadControlRoot(body, "Revoke");
            EnsureOnlyContractAttributes(root);

            Guid instanceId = ReadCanonicalGuid(root, "InstanceId");
            Guid peerInstanceId = ReadCanonicalGuid(
                root,
                "PeerInstanceId");
            if (instanceId == peerInstanceId)
            {
                throw InvalidRequest(
                    "Peer revoke instance IDs must be different.");
            }

            return new PeerRevokeRequest(
                instanceId,
                peerInstanceId,
                ReadPositiveEpoch(root, "KeyEpoch"),
                ReadCanonicalGuid(root, "RevokeId"));
        }

        public static byte[] SerializeRevokeRequest(
            PeerRevokeRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            byte[] body = SerializeDocument(
                writer =>
                {
                    writer.WriteStartElement(
                        "Revoke",
                        PeerSyncContract.XmlNamespace);
                    WriteGuidElement(writer, "InstanceId", request.InstanceId);
                    WriteGuidElement(
                        writer,
                        "PeerInstanceId",
                        request.PeerInstanceId);
                    WriteUInt64Element(writer, "KeyEpoch", request.KeyEpoch);
                    WriteGuidElement(writer, "RevokeId", request.RevokeId);
                    writer.WriteEndElement();
                });
            EnsureControlSerializedBodyLimit(body);
            ParseAuthenticatedRevokeRequest(body);
            return body;
        }

        // The caller must apply the endpoint-specific pairing MAC or normal
        // Peer response HMAC before trusting a parsed response. Extensions are
        // ignored only in the final schema-designated envelope position.
        public static PeerControlResponse ParseControlResponse(byte[] body)
        {
            XElement root = LoadControlRoot(body, "Response");
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
            List<XElement> payloads = GetResponsePayloadElements(root);
            bool isOk = StringComparer.Ordinal.Equals(result, "OK");
            bool isError = StringComparer.Ordinal.Equals(result, "ERROR");
            if (!isOk && !isError)
            {
                throw InvalidRequest("The Peer response Result is invalid.");
            }

            if (isError)
            {
                if (code == PeerSyncResponseCode.Ok || payloads.Count != 0)
                {
                    throw InvalidRequest(
                        "A Peer error response cannot use Code=0 or contain a control payload.");
                }

                return PeerControlResponse.CreateParsedError(code, message);
            }

            if (code != PeerSyncResponseCode.Ok || payloads.Count > 1)
            {
                throw InvalidRequest(
                    "A successful Peer control response has an invalid code or payload count.");
            }

            if (payloads.Count == 0)
            {
                return PeerControlResponse.CreateParsedUnitSuccess(message);
            }

            XElement payload = payloads[0];
            if (payload.Name == Namespace + "PairingHelloResult")
            {
                return PeerControlResponse.CreateParsedPairingHelloSuccess(
                    ParsePairingHelloResult(payload),
                    message);
            }

            if (payload.Name == Namespace + "PairingKeyConfirmResult")
            {
                return PeerControlResponse
                    .CreateParsedPairingKeyConfirmSuccess(
                        ParsePairingKeyConfirmation(payload),
                        message);
            }

            if (payload.Name == Namespace + "Handshake")
            {
                return PeerControlResponse.CreateParsedHandshakeSuccess(
                    ParseHandshakeResult(payload),
                    message);
            }

            throw InvalidRequest(
                "The successful Peer response payload is not a control result.");
        }

        public static PeerControlResponse ParsePairingHelloResponse(
            byte[] body)
        {
            return EnsureExpectedControlResponse(
                ParseControlResponse(body),
                PeerControlResponseKind.PairingHello,
                "pairing hello");
        }

        public static PeerControlResponse ParsePairingKeyConfirmResponse(
            byte[] body)
        {
            return EnsureExpectedControlResponse(
                ParseControlResponse(body),
                PeerControlResponseKind.PairingKeyConfirmation,
                "pairing key confirmation");
        }

        public static PeerControlResponse ParsePairingDecisionResponse(
            byte[] body)
        {
            return EnsureExpectedControlResponse(
                ParseControlResponse(body),
                PeerControlResponseKind.UnitSuccess,
                "pairing decision");
        }

        public static PeerControlResponse ParsePairingCommitResponse(
            byte[] body)
        {
            return EnsureExpectedControlResponse(
                ParseControlResponse(body),
                PeerControlResponseKind.UnitSuccess,
                "pairing commit");
        }

        public static PeerControlResponse ParseAuthenticatedHandshakeResponse(
            byte[] body)
        {
            return EnsureExpectedControlResponse(
                ParseControlResponse(body),
                PeerControlResponseKind.Handshake,
                "handshake");
        }

        public static PeerControlResponse ParseAuthenticatedReleaseResponse(
            byte[] body)
        {
            return EnsureExpectedControlResponse(
                ParseControlResponse(body),
                PeerControlResponseKind.UnitSuccess,
                "release");
        }

        public static PeerControlResponse ParseAuthenticatedRevokeResponse(
            byte[] body)
        {
            return EnsureExpectedControlResponse(
                ParseControlResponse(body),
                PeerControlResponseKind.UnitSuccess,
                "revoke");
        }

        public static byte[] SerializeControlResponse(
            PeerControlResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            byte[] body = SerializeDocument(
                writer => WriteControlResponse(writer, response));
            EnsureControlSerializedBodyLimit(body);
            ParseControlResponse(body);
            return body;
        }

        private static PeerHandshakeResult ParseHandshakeResult(
            XElement handshake)
        {
            DateTime expiresUtc = ReadCanonicalUtc(
                handshake,
                "ExpiresUtc");
            DateTime utcNow = ReadCanonicalUtc(handshake, "UtcNow");
            long sessionLifetimeTicks = TimeSpan.FromMinutes(10).Ticks;
            if (utcNow.Ticks
                    > DateTime.MaxValue.Ticks - sessionLifetimeTicks
                || expiresUtc.Ticks
                    != utcNow.Ticks + sessionLifetimeTicks)
            {
                throw InvalidRequest(
                    "A Peer handshake session must expire exactly ten minutes after UtcNow.");
            }

            byte[] nonce = null;
            byte[] sessionId = null;
            try
            {
                nonce = ReadCanonicalBinary(
                    handshake,
                    "HandshakeNonce",
                    PeerSyncContract.PairingNonceLength);
                sessionId = ReadCanonicalBinary(
                    handshake,
                    "SessionId",
                    PeerSyncContract.SessionIdLength);
                return new PeerHandshakeResult(
                    ReadCanonicalGuid(handshake, "InstanceId"),
                    ReadPositiveEpoch(handshake, "KeyEpoch"),
                    nonce,
                    sessionId,
                    expiresUtc,
                    utcNow,
                    ReadCanonicalBoolean(handshake, "SyncEnabled"));
            }
            finally
            {
                ClearBuffer(nonce);
                ClearBuffer(sessionId);
            }
        }

        private static PeerControlResponse EnsureExpectedControlResponse(
            PeerControlResponse response,
            PeerControlResponseKind expectedSuccessKind,
            string operation)
        {
            if (response.IsSuccess
                && response.Kind != expectedSuccessKind)
            {
                throw InvalidRequest(
                    "A successful Peer "
                    + operation
                    + " response has an unexpected payload.");
            }

            return response;
        }

        private static void EnsureOnlyResponseContractAttributes(
            XElement response)
        {
            EnsureOnlyResponseContractAttributesCore(response);
        }

        private static void EnsureOnlyResponseContractAttributesCore(
            XElement element)
        {
            foreach (XAttribute attribute in element.Attributes())
            {
                if (!attribute.IsNamespaceDeclaration)
                {
                    if (element.Name == Namespace + "Exchange"
                        && attribute.Name == "Mode")
                    {
                        continue;
                    }

                    throw InvalidRequest(
                        "The Peer response contains an unknown attribute outside Extensions.");
                }
            }

            if (element.Name == Namespace + "Extensions")
            {
                // Extension-owned descendants may define their own attributes.
                // The fixed Extensions wrapper itself remains attribute-free.
                return;
            }

            foreach (XElement child in element.Elements())
            {
                EnsureOnlyResponseContractAttributesCore(child);
            }
        }

        private static void WriteControlResponse(
            XmlWriter writer,
            PeerControlResponse response)
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

            // Parsed remote messages and exception details are never reflected.
            writer.WriteElementString(
                "Message",
                PeerSyncContract.XmlNamespace,
                string.Empty);

            switch (response.Kind)
            {
                case PeerControlResponseKind.UnitSuccess:
                case PeerControlResponseKind.Error:
                    break;
                case PeerControlResponseKind.PairingHello:
                    WritePairingHelloResult(writer, response.PairingHello);
                    break;
                case PeerControlResponseKind.PairingKeyConfirmation:
                    WritePairingKeyConfirmation(
                        writer,
                        "PairingKeyConfirmResult",
                        response.PairingKeyConfirmation);
                    break;
                case PeerControlResponseKind.Handshake:
                    WriteHandshakeResult(writer, response.Handshake);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(response));
            }

            writer.WriteEndElement();
        }

        private static void WriteHandshakeResult(
            XmlWriter writer,
            PeerHandshakeResult result)
        {
            writer.WriteStartElement(
                "Handshake",
                PeerSyncContract.XmlNamespace);
            WriteGuidElement(writer, "InstanceId", result.InstanceId);
            WriteUInt64Element(writer, "KeyEpoch", result.KeyEpoch);
            WriteOwnedBase64Element(
                writer,
                "HandshakeNonce",
                result.CopyHandshakeNonce());
            WriteOwnedBase64Element(
                writer,
                "SessionId",
                result.CopySessionId());
            WriteUtcElement(writer, "ExpiresUtc", result.ExpiresUtc);
            WriteUtcElement(writer, "UtcNow", result.UtcNow);
            WriteBooleanElement(writer, "SyncEnabled", result.SyncEnabled);
            writer.WriteEndElement();
        }

        private static XElement LoadControlRoot(
            byte[] body,
            string expectedRootName)
        {
            string xml = DecodeAndInspectControlBody(body);
            return LoadSchemaValidatedRoot(
                xml,
                body.Length,
                expectedRootName);
        }

        private static string DecodeAndInspectControlBody(byte[] body)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (body.Length == 0)
            {
                throw InvalidRequest("The Peer control body is empty.");
            }

            if (body.Length > PeerSyncContract.MaximumControlBodyBytes)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.BodyTooLarge,
                    "The Peer control body exceeds the raw byte limit.");
            }

            string xml;
            try
            {
                xml = StrictUtf8.GetString(body);
            }
            catch (DecoderFallbackException exception)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.InvalidRequest,
                    "The Peer control body is not strict UTF-8.",
                    exception);
            }

            if (xml.Length > 0 && xml[0] == '\uFEFF')
            {
                xml = xml.Substring(1);
            }

            InspectDepthAndCountItems(xml, body.Length);
            return xml;
        }

        private static ulong ReadPositiveEpoch(
            XElement parent,
            string name)
        {
            ulong value = ReadCanonicalUInt64(parent, name);
            if (value == 0)
            {
                throw InvalidRequest(
                    "A Peer key epoch must be positive: " + name + ".");
            }

            return value;
        }

        private static byte[] ReadCanonicalBinary(
            XElement parent,
            string name,
            int expectedLength)
        {
            string text = ReadRequiredValue(parent, name);
            byte[] value;
            try
            {
                value = Convert.FromBase64String(text);
            }
            catch (FormatException exception)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.InvalidRequest,
                    "A Peer binary value is not valid Base64: " + name + ".",
                    exception);
            }

            if (value.Length != expectedLength
                || !StringComparer.Ordinal.Equals(
                    text,
                    Convert.ToBase64String(value)))
            {
                Array.Clear(value, 0, value.Length);
                throw InvalidRequest(
                    "A Peer binary value is not canonical or has an invalid length: "
                    + name
                    + ".");
            }

            return value;
        }

        private static string ReadCanonicalEndpoint(
            XElement parent,
            string name)
        {
            string text = ReadRequiredValue(parent, name);
            string canonical;
            if (!AdminPeerEndpoint.TryNormalize(text, out canonical)
                || !StringComparer.Ordinal.Equals(text, canonical))
            {
                throw InvalidRequest(
                    "A Peer endpoint is not canonical: " + name + ".");
            }

            return canonical;
        }

        private static PeerPairingRole ReadPairingRole(
            XElement parent,
            string name)
        {
            string text = ReadRequiredValue(parent, name);
            if (StringComparer.Ordinal.Equals(text, "initiator"))
            {
                return PeerPairingRole.Initiator;
            }

            if (StringComparer.Ordinal.Equals(text, "responder"))
            {
                return PeerPairingRole.Responder;
            }

            throw InvalidRequest("A Peer pairing role is invalid.");
        }

        private static string FormatPairingRole(PeerPairingRole role)
        {
            switch (role)
            {
                case PeerPairingRole.Initiator:
                    return "initiator";
                case PeerPairingRole.Responder:
                    return "responder";
                default:
                    throw new ArgumentOutOfRangeException(nameof(role));
            }
        }

        private static void WriteOwnedBase64Element(
            XmlWriter writer,
            string name,
            byte[] value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            try
            {
                writer.WriteElementString(
                    name,
                    PeerSyncContract.XmlNamespace,
                    Convert.ToBase64String(value));
            }
            finally
            {
                Array.Clear(value, 0, value.Length);
            }
        }

        private static void WriteUtcElement(
            XmlWriter writer,
            string name,
            DateTime value)
        {
            PeerControlModelValidation.ValidateUtc(value, nameof(value));
            writer.WriteElementString(
                name,
                PeerSyncContract.XmlNamespace,
                FormatUtc(value));
        }

        private static void WriteBooleanElement(
            XmlWriter writer,
            string name,
            bool value)
        {
            writer.WriteElementString(
                name,
                PeerSyncContract.XmlNamespace,
                value ? "true" : "false");
        }

        private static void ClearBuffer(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }

        private static void EnsureControlSerializedBodyLimit(byte[] body)
        {
            if (body.Length > PeerSyncContract.MaximumControlBodyBytes)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.BodyTooLarge,
                    "The serialized Peer control body exceeds the raw byte limit.");
            }
        }
    }
}
