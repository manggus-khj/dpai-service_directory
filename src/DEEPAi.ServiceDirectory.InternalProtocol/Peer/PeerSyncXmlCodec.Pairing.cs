using System;
using System.Xml;
using System.Xml.Linq;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Peer
{
    public static partial class PeerSyncXmlCodec
    {
        // PairingHello is the only unsigned Peer request. The caller must still
        // enforce the open pairing window and exact remote endpoint before use.
        public static PeerPairingHelloRequest ParsePairingHelloRequest(
            byte[] body)
        {
            XElement root = LoadControlRoot(body, "PairingHello");
            EnsureOnlyContractAttributes(root);
            ReadFixedPairingAlgorithm(root);

            byte[] nonce = null;
            byte[] publicKey = null;
            try
            {
                nonce = ReadCanonicalBinary(
                    root,
                    "InitiatorNonce",
                    PeerSyncContract.PairingNonceLength);
                publicKey = ReadCanonicalBinary(
                    root,
                    "InitiatorPublicKey",
                    PeerSyncContract.PairingPublicKeyLength);
                return CreatePairingHelloRequest(
                    ReadCanonicalGuid(root, "PairingId"),
                    ReadCanonicalGuid(root, "InitiatorInstanceId"),
                    ReadCanonicalEndpoint(root, "InitiatorEndpoint"),
                    nonce,
                    publicKey,
                    ReadCanonicalUInt64(
                        root,
                        "InitiatorLastPeerKeyEpoch"));
            }
            finally
            {
                ClearBuffer(nonce);
                ClearBuffer(publicKey);
            }
        }

        public static byte[] SerializePairingHelloRequest(
            PeerPairingHelloRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            byte[] body = SerializeDocument(
                writer =>
                {
                    writer.WriteStartElement(
                        "PairingHello",
                        PeerSyncContract.XmlNamespace);
                    WritePairingAlgorithm(writer);
                    WriteGuidElement(writer, "PairingId", request.PairingId);
                    WriteGuidElement(
                        writer,
                        "InitiatorInstanceId",
                        request.InitiatorInstanceId);
                    writer.WriteElementString(
                        "InitiatorEndpoint",
                        PeerSyncContract.XmlNamespace,
                        request.InitiatorEndpoint);
                    WriteOwnedBase64Element(
                        writer,
                        "InitiatorNonce",
                        request.CopyInitiatorNonce());
                    WriteOwnedBase64Element(
                        writer,
                        "InitiatorPublicKey",
                        request.CopyInitiatorPublicKey());
                    WriteUInt64Element(
                        writer,
                        "InitiatorLastPeerKeyEpoch",
                        request.InitiatorLastPeerKeyEpoch);
                    writer.WriteEndElement();
                });
            EnsureControlSerializedBodyLimit(body);
            ParsePairingHelloRequest(body);
            return body;
        }

        public static PeerPairingKeyConfirmation
            ParsePairingKeyConfirmRequest(byte[] body)
        {
            XElement root = LoadControlRoot(body, "PairingKeyConfirm");
            EnsureOnlyContractAttributes(root);
            return ParsePairingKeyConfirmation(root);
        }

        public static byte[] SerializePairingKeyConfirmRequest(
            PeerPairingKeyConfirmation request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            byte[] body = SerializeDocument(
                writer => WritePairingKeyConfirmation(
                    writer,
                    "PairingKeyConfirm",
                    request));
            EnsureControlSerializedBodyLimit(body);
            ParsePairingKeyConfirmRequest(body);
            return body;
        }

        // X-DPAI-Pairing-MAC is verified by the pairing state machine using the
        // parsed canonical fields. This codec does not accept or trust headers.
        public static PeerPairingDecision ParsePairingDecisionRequest(
            byte[] body)
        {
            XElement root = LoadControlRoot(body, "PairingDecision");
            EnsureOnlyContractAttributes(root);

            Guid senderInstanceId = ReadCanonicalGuid(
                root,
                "SenderInstanceId");
            Guid receiverInstanceId = ReadCanonicalGuid(
                root,
                "ReceiverInstanceId");
            EnsureDistinctPairingInstances(
                senderInstanceId,
                receiverInstanceId);

            byte[] transcriptHash = ReadCanonicalBinary(
                root,
                "TranscriptHash",
                PeerSyncContract.TranscriptHashLength);
            try
            {
                return new PeerPairingDecision(
                    ReadCanonicalGuid(root, "PairingId"),
                    ReadPositiveEpoch(root, "KeyEpoch"),
                    ReadPairingRole(root, "SenderRole"),
                    senderInstanceId,
                    receiverInstanceId,
                    transcriptHash,
                    ReadPairingDecisionValue(root));
            }
            finally
            {
                Array.Clear(transcriptHash, 0, transcriptHash.Length);
            }
        }

        public static byte[] SerializePairingDecisionRequest(
            PeerPairingDecision request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            byte[] body = SerializeDocument(
                writer =>
                {
                    writer.WriteStartElement(
                        "PairingDecision",
                        PeerSyncContract.XmlNamespace);
                    WritePairingBinding(
                        writer,
                        request.PairingId,
                        request.KeyEpoch,
                        request.SenderRole,
                        request.SenderInstanceId,
                        request.ReceiverInstanceId,
                        request.CopyTranscriptHash());
                    writer.WriteElementString(
                        "Decision",
                        PeerSyncContract.XmlNamespace,
                        request.Decision
                            == PeerPairingDecisionValue.Confirmed
                            ? "CONFIRMED"
                            : "CANCELLED");
                    writer.WriteEndElement();
                });
            EnsureControlSerializedBodyLimit(body);
            ParsePairingDecisionRequest(body);
            return body;
        }

        // X-DPAI-Pairing-MAC and durable pending-commit state are checked by
        // the caller. This method only validates the fixed XML wire contract.
        public static PeerPairingCommit ParsePairingCommitRequest(byte[] body)
        {
            XElement root = LoadControlRoot(body, "PairingCommit");
            EnsureOnlyContractAttributes(root);

            Guid senderInstanceId = ReadCanonicalGuid(
                root,
                "SenderInstanceId");
            Guid receiverInstanceId = ReadCanonicalGuid(
                root,
                "ReceiverInstanceId");
            EnsureDistinctPairingInstances(
                senderInstanceId,
                receiverInstanceId);
            if (!StringComparer.Ordinal.Equals(
                ReadRequiredValue(root, "Commit"),
                "COMMIT"))
            {
                throw InvalidRequest(
                    "A Peer pairing commit marker is invalid.");
            }

            byte[] transcriptHash = ReadCanonicalBinary(
                root,
                "TranscriptHash",
                PeerSyncContract.TranscriptHashLength);
            try
            {
                return new PeerPairingCommit(
                    ReadCanonicalGuid(root, "PairingId"),
                    ReadPositiveEpoch(root, "KeyEpoch"),
                    ReadPairingRole(root, "SenderRole"),
                    senderInstanceId,
                    receiverInstanceId,
                    transcriptHash);
            }
            finally
            {
                Array.Clear(transcriptHash, 0, transcriptHash.Length);
            }
        }

        public static byte[] SerializePairingCommitRequest(
            PeerPairingCommit request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            byte[] body = SerializeDocument(
                writer =>
                {
                    writer.WriteStartElement(
                        "PairingCommit",
                        PeerSyncContract.XmlNamespace);
                    WritePairingBinding(
                        writer,
                        request.PairingId,
                        request.KeyEpoch,
                        request.SenderRole,
                        request.SenderInstanceId,
                        request.ReceiverInstanceId,
                        request.CopyTranscriptHash());
                    writer.WriteElementString(
                        "Commit",
                        PeerSyncContract.XmlNamespace,
                        "COMMIT");
                    writer.WriteEndElement();
                });
            EnsureControlSerializedBodyLimit(body);
            ParsePairingCommitRequest(body);
            return body;
        }

        private static PeerPairingHelloResult ParsePairingHelloResult(
            XElement result)
        {
            ReadFixedPairingAlgorithm(result);
            byte[] nonce = null;
            byte[] publicKey = null;
            try
            {
                nonce = ReadCanonicalBinary(
                    result,
                    "ResponderNonce",
                    PeerSyncContract.PairingNonceLength);
                publicKey = ReadCanonicalBinary(
                    result,
                    "ResponderPublicKey",
                    PeerSyncContract.PairingPublicKeyLength);
                return CreatePairingHelloResult(
                    ReadCanonicalGuid(result, "PairingId"),
                    ReadCanonicalGuid(result, "ResponderInstanceId"),
                    ReadCanonicalEndpoint(result, "ResponderEndpoint"),
                    nonce,
                    publicKey,
                    ReadCanonicalUInt64(
                        result,
                        "ResponderLastPeerKeyEpoch"),
                    ReadPositiveEpoch(result, "KeyEpoch"));
            }
            finally
            {
                ClearBuffer(nonce);
                ClearBuffer(publicKey);
            }
        }

        private static PeerPairingKeyConfirmation
            ParsePairingKeyConfirmation(XElement value)
        {
            Guid senderInstanceId = ReadCanonicalGuid(
                value,
                "SenderInstanceId");
            Guid receiverInstanceId = ReadCanonicalGuid(
                value,
                "ReceiverInstanceId");
            EnsureDistinctPairingInstances(
                senderInstanceId,
                receiverInstanceId);

            byte[] transcriptHash = null;
            byte[] confirmationMac = null;
            try
            {
                transcriptHash = ReadCanonicalBinary(
                    value,
                    "TranscriptHash",
                    PeerSyncContract.TranscriptHashLength);
                confirmationMac = ReadCanonicalBinary(
                    value,
                    "ConfirmationMac",
                    PeerSyncContract.AuthenticationCodeLength);
                return new PeerPairingKeyConfirmation(
                    ReadCanonicalGuid(value, "PairingId"),
                    ReadPositiveEpoch(value, "KeyEpoch"),
                    ReadPairingRole(value, "SenderRole"),
                    senderInstanceId,
                    receiverInstanceId,
                    transcriptHash,
                    confirmationMac);
            }
            finally
            {
                ClearBuffer(transcriptHash);
                ClearBuffer(confirmationMac);
            }
        }

        private static void WritePairingHelloResult(
            XmlWriter writer,
            PeerPairingHelloResult result)
        {
            writer.WriteStartElement(
                "PairingHelloResult",
                PeerSyncContract.XmlNamespace);
            WritePairingAlgorithm(writer);
            WriteGuidElement(writer, "PairingId", result.PairingId);
            WriteGuidElement(
                writer,
                "ResponderInstanceId",
                result.ResponderInstanceId);
            writer.WriteElementString(
                "ResponderEndpoint",
                PeerSyncContract.XmlNamespace,
                result.ResponderEndpoint);
            WriteOwnedBase64Element(
                writer,
                "ResponderNonce",
                result.CopyResponderNonce());
            WriteOwnedBase64Element(
                writer,
                "ResponderPublicKey",
                result.CopyResponderPublicKey());
            WriteUInt64Element(
                writer,
                "ResponderLastPeerKeyEpoch",
                result.ResponderLastPeerKeyEpoch);
            WriteUInt64Element(writer, "KeyEpoch", result.KeyEpoch);
            writer.WriteEndElement();
        }

        private static void WritePairingKeyConfirmation(
            XmlWriter writer,
            string elementName,
            PeerPairingKeyConfirmation value)
        {
            writer.WriteStartElement(
                elementName,
                PeerSyncContract.XmlNamespace);
            WritePairingBinding(
                writer,
                value.PairingId,
                value.KeyEpoch,
                value.SenderRole,
                value.SenderInstanceId,
                value.ReceiverInstanceId,
                value.CopyTranscriptHash());
            WriteOwnedBase64Element(
                writer,
                "ConfirmationMac",
                value.CopyConfirmationMac());
            writer.WriteEndElement();
        }

        private static void WritePairingBinding(
            XmlWriter writer,
            Guid pairingId,
            ulong keyEpoch,
            PeerPairingRole senderRole,
            Guid senderInstanceId,
            Guid receiverInstanceId,
            byte[] transcriptHash)
        {
            WriteGuidElement(writer, "PairingId", pairingId);
            WriteUInt64Element(writer, "KeyEpoch", keyEpoch);
            writer.WriteElementString(
                "SenderRole",
                PeerSyncContract.XmlNamespace,
                FormatPairingRole(senderRole));
            WriteGuidElement(
                writer,
                "SenderInstanceId",
                senderInstanceId);
            WriteGuidElement(
                writer,
                "ReceiverInstanceId",
                receiverInstanceId);
            WriteOwnedBase64Element(
                writer,
                "TranscriptHash",
                transcriptHash);
        }

        private static void ReadFixedPairingAlgorithm(XElement parent)
        {
            if (!StringComparer.Ordinal.Equals(
                ReadRequiredValue(parent, "Algorithm"),
                PeerSyncContract.PairingAlgorithm))
            {
                throw InvalidRequest(
                    "The Peer pairing algorithm is not supported.");
            }
        }

        private static void WritePairingAlgorithm(XmlWriter writer)
        {
            writer.WriteElementString(
                "Algorithm",
                PeerSyncContract.XmlNamespace,
                PeerSyncContract.PairingAlgorithm);
        }

        private static PeerPairingDecisionValue ReadPairingDecisionValue(
            XElement parent)
        {
            string text = ReadRequiredValue(parent, "Decision");
            if (StringComparer.Ordinal.Equals(text, "CONFIRMED"))
            {
                return PeerPairingDecisionValue.Confirmed;
            }

            if (StringComparer.Ordinal.Equals(text, "CANCELLED"))
            {
                return PeerPairingDecisionValue.Cancelled;
            }

            throw InvalidRequest("A Peer pairing decision is invalid.");
        }

        private static void EnsureDistinctPairingInstances(
            Guid senderInstanceId,
            Guid receiverInstanceId)
        {
            if (senderInstanceId == receiverInstanceId)
            {
                throw InvalidRequest(
                    "Peer pairing sender and receiver IDs must be different.");
            }
        }

        private static PeerPairingHelloRequest CreatePairingHelloRequest(
            Guid pairingId,
            Guid initiatorInstanceId,
            string initiatorEndpoint,
            byte[] initiatorNonce,
            byte[] initiatorPublicKey,
            ulong initiatorLastPeerKeyEpoch)
        {
            try
            {
                return new PeerPairingHelloRequest(
                    pairingId,
                    initiatorInstanceId,
                    initiatorEndpoint,
                    initiatorNonce,
                    initiatorPublicKey,
                    initiatorLastPeerKeyEpoch);
            }
            catch (ArgumentException exception)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.InvalidRequest,
                    "The Peer pairing hello is semantically invalid.",
                    exception);
            }
        }

        private static PeerPairingHelloResult CreatePairingHelloResult(
            Guid pairingId,
            Guid responderInstanceId,
            string responderEndpoint,
            byte[] responderNonce,
            byte[] responderPublicKey,
            ulong responderLastPeerKeyEpoch,
            ulong keyEpoch)
        {
            try
            {
                return new PeerPairingHelloResult(
                    pairingId,
                    responderInstanceId,
                    responderEndpoint,
                    responderNonce,
                    responderPublicKey,
                    responderLastPeerKeyEpoch,
                    keyEpoch);
            }
            catch (ArgumentException exception)
            {
                throw new PeerSyncProtocolException(
                    PeerSyncProtocolFailure.InvalidRequest,
                    "The Peer pairing hello result is semantically invalid.",
                    exception);
            }
        }
    }
}
