using System;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Peer
{
    public enum PeerPairingRole
    {
        Initiator = 1,
        Responder = 2
    }

    public enum PeerPairingDecisionValue
    {
        Confirmed = 1,
        Cancelled = 2
    }

    public sealed class PeerPairingHelloRequest
    {
        private readonly byte[] _initiatorNonce;
        private readonly byte[] _initiatorPublicKey;

        public PeerPairingHelloRequest(
            Guid pairingId,
            Guid initiatorInstanceId,
            string initiatorEndpoint,
            byte[] initiatorNonce,
            byte[] initiatorPublicKey,
            ulong initiatorLastPeerKeyEpoch)
        {
            PeerControlModelValidation.ValidateGuid(
                pairingId,
                nameof(pairingId));
            PeerControlModelValidation.ValidateGuid(
                initiatorInstanceId,
                nameof(initiatorInstanceId));

            PairingId = pairingId;
            InitiatorInstanceId = initiatorInstanceId;
            InitiatorEndpoint = PeerControlModelValidation
                .ValidateCanonicalEndpoint(
                    initiatorEndpoint,
                    nameof(initiatorEndpoint));
            _initiatorNonce = PeerControlModelValidation.CloneExactLength(
                initiatorNonce,
                nameof(initiatorNonce),
                PeerSyncContract.PairingNonceLength);
            _initiatorPublicKey = PeerControlModelValidation
                .CloneP256PublicKey(
                    initiatorPublicKey,
                    nameof(initiatorPublicKey));
            InitiatorLastPeerKeyEpoch = initiatorLastPeerKeyEpoch;
        }

        public string Algorithm => PeerSyncContract.PairingAlgorithm;

        public Guid PairingId { get; }

        public Guid InitiatorInstanceId { get; }

        public string InitiatorEndpoint { get; }

        public ulong InitiatorLastPeerKeyEpoch { get; }

        public byte[] CopyInitiatorNonce()
        {
            return (byte[])_initiatorNonce.Clone();
        }

        public byte[] CopyInitiatorPublicKey()
        {
            return (byte[])_initiatorPublicKey.Clone();
        }
    }

    public sealed class PeerPairingHelloResult
    {
        private readonly byte[] _responderNonce;
        private readonly byte[] _responderPublicKey;

        public PeerPairingHelloResult(
            Guid pairingId,
            Guid responderInstanceId,
            string responderEndpoint,
            byte[] responderNonce,
            byte[] responderPublicKey,
            ulong responderLastPeerKeyEpoch,
            ulong keyEpoch)
        {
            PeerControlModelValidation.ValidateGuid(
                pairingId,
                nameof(pairingId));
            PeerControlModelValidation.ValidateGuid(
                responderInstanceId,
                nameof(responderInstanceId));
            PeerControlModelValidation.ValidatePositiveEpoch(
                keyEpoch,
                nameof(keyEpoch));

            PairingId = pairingId;
            ResponderInstanceId = responderInstanceId;
            ResponderEndpoint = PeerControlModelValidation
                .ValidateCanonicalEndpoint(
                    responderEndpoint,
                    nameof(responderEndpoint));
            _responderNonce = PeerControlModelValidation.CloneExactLength(
                responderNonce,
                nameof(responderNonce),
                PeerSyncContract.PairingNonceLength);
            _responderPublicKey = PeerControlModelValidation
                .CloneP256PublicKey(
                    responderPublicKey,
                    nameof(responderPublicKey));
            ResponderLastPeerKeyEpoch = responderLastPeerKeyEpoch;
            KeyEpoch = keyEpoch;
        }

        public string Algorithm => PeerSyncContract.PairingAlgorithm;

        public Guid PairingId { get; }

        public Guid ResponderInstanceId { get; }

        public string ResponderEndpoint { get; }

        public ulong ResponderLastPeerKeyEpoch { get; }

        public ulong KeyEpoch { get; }

        public byte[] CopyResponderNonce()
        {
            return (byte[])_responderNonce.Clone();
        }

        public byte[] CopyResponderPublicKey()
        {
            return (byte[])_responderPublicKey.Clone();
        }
    }

    public sealed class PeerPairingKeyConfirmation
    {
        private readonly byte[] _transcriptHash;
        private readonly byte[] _confirmationMac;

        public PeerPairingKeyConfirmation(
            Guid pairingId,
            ulong keyEpoch,
            PeerPairingRole senderRole,
            Guid senderInstanceId,
            Guid receiverInstanceId,
            byte[] transcriptHash,
            byte[] confirmationMac)
        {
            PeerControlModelValidation.ValidatePairingBinding(
                pairingId,
                keyEpoch,
                senderRole,
                senderInstanceId,
                receiverInstanceId);

            PairingId = pairingId;
            KeyEpoch = keyEpoch;
            SenderRole = senderRole;
            SenderInstanceId = senderInstanceId;
            ReceiverInstanceId = receiverInstanceId;
            _transcriptHash = PeerControlModelValidation.CloneExactLength(
                transcriptHash,
                nameof(transcriptHash),
                PeerSyncContract.TranscriptHashLength);
            _confirmationMac = PeerControlModelValidation.CloneExactLength(
                confirmationMac,
                nameof(confirmationMac),
                PeerSyncContract.AuthenticationCodeLength);
        }

        public Guid PairingId { get; }

        public ulong KeyEpoch { get; }

        public PeerPairingRole SenderRole { get; }

        public Guid SenderInstanceId { get; }

        public Guid ReceiverInstanceId { get; }

        public byte[] CopyTranscriptHash()
        {
            return (byte[])_transcriptHash.Clone();
        }

        public byte[] CopyConfirmationMac()
        {
            return (byte[])_confirmationMac.Clone();
        }
    }

    public sealed class PeerPairingDecision
    {
        private readonly byte[] _transcriptHash;

        public PeerPairingDecision(
            Guid pairingId,
            ulong keyEpoch,
            PeerPairingRole senderRole,
            Guid senderInstanceId,
            Guid receiverInstanceId,
            byte[] transcriptHash,
            PeerPairingDecisionValue decision)
        {
            PeerControlModelValidation.ValidatePairingBinding(
                pairingId,
                keyEpoch,
                senderRole,
                senderInstanceId,
                receiverInstanceId);
            if (!Enum.IsDefined(
                typeof(PeerPairingDecisionValue),
                decision))
            {
                throw new ArgumentOutOfRangeException(nameof(decision));
            }

            PairingId = pairingId;
            KeyEpoch = keyEpoch;
            SenderRole = senderRole;
            SenderInstanceId = senderInstanceId;
            ReceiverInstanceId = receiverInstanceId;
            _transcriptHash = PeerControlModelValidation.CloneExactLength(
                transcriptHash,
                nameof(transcriptHash),
                PeerSyncContract.TranscriptHashLength);
            Decision = decision;
        }

        public Guid PairingId { get; }

        public ulong KeyEpoch { get; }

        public PeerPairingRole SenderRole { get; }

        public Guid SenderInstanceId { get; }

        public Guid ReceiverInstanceId { get; }

        public PeerPairingDecisionValue Decision { get; }

        public byte[] CopyTranscriptHash()
        {
            return (byte[])_transcriptHash.Clone();
        }
    }

    public sealed class PeerPairingCommit
    {
        private readonly byte[] _transcriptHash;

        public PeerPairingCommit(
            Guid pairingId,
            ulong keyEpoch,
            PeerPairingRole senderRole,
            Guid senderInstanceId,
            Guid receiverInstanceId,
            byte[] transcriptHash)
        {
            PeerControlModelValidation.ValidatePairingBinding(
                pairingId,
                keyEpoch,
                senderRole,
                senderInstanceId,
                receiverInstanceId);

            PairingId = pairingId;
            KeyEpoch = keyEpoch;
            SenderRole = senderRole;
            SenderInstanceId = senderInstanceId;
            ReceiverInstanceId = receiverInstanceId;
            _transcriptHash = PeerControlModelValidation.CloneExactLength(
                transcriptHash,
                nameof(transcriptHash),
                PeerSyncContract.TranscriptHashLength);
        }

        public Guid PairingId { get; }

        public ulong KeyEpoch { get; }

        public PeerPairingRole SenderRole { get; }

        public Guid SenderInstanceId { get; }

        public Guid ReceiverInstanceId { get; }

        public string Commit => "COMMIT";

        public byte[] CopyTranscriptHash()
        {
            return (byte[])_transcriptHash.Clone();
        }
    }

    public sealed class PeerHandshakeRequest
    {
        private readonly byte[] _handshakeNonce;

        public PeerHandshakeRequest(
            Guid instanceId,
            Guid peerInstanceId,
            ulong keyEpoch,
            byte[] handshakeNonce,
            DateTime utcNow,
            bool syncEnabled)
        {
            PeerControlModelValidation.ValidatePeerBinding(
                instanceId,
                peerInstanceId,
                keyEpoch);
            PeerControlModelValidation.ValidateUtc(utcNow, nameof(utcNow));

            InstanceId = instanceId;
            PeerInstanceId = peerInstanceId;
            KeyEpoch = keyEpoch;
            _handshakeNonce = PeerControlModelValidation.CloneExactLength(
                handshakeNonce,
                nameof(handshakeNonce),
                PeerSyncContract.PairingNonceLength);
            UtcNow = utcNow;
            SyncEnabled = syncEnabled;
        }

        public Guid InstanceId { get; }

        public Guid PeerInstanceId { get; }

        public ulong KeyEpoch { get; }

        public DateTime UtcNow { get; }

        public bool SyncEnabled { get; }

        public byte[] CopyHandshakeNonce()
        {
            return (byte[])_handshakeNonce.Clone();
        }
    }

    public sealed class PeerHandshakeResult
    {
        private readonly byte[] _handshakeNonce;
        private readonly byte[] _sessionId;

        public PeerHandshakeResult(
            Guid instanceId,
            ulong keyEpoch,
            byte[] handshakeNonce,
            byte[] sessionId,
            DateTime expiresUtc,
            DateTime utcNow,
            bool syncEnabled)
        {
            PeerControlModelValidation.ValidateGuid(
                instanceId,
                nameof(instanceId));
            PeerControlModelValidation.ValidatePositiveEpoch(
                keyEpoch,
                nameof(keyEpoch));
            PeerControlModelValidation.ValidateUtc(
                expiresUtc,
                nameof(expiresUtc));
            PeerControlModelValidation.ValidateUtc(utcNow, nameof(utcNow));
            long sessionLifetimeTicks = TimeSpan.FromMinutes(10).Ticks;
            if (utcNow.Ticks
                    > DateTime.MaxValue.Ticks - sessionLifetimeTicks
                || expiresUtc.Ticks
                    != utcNow.Ticks + sessionLifetimeTicks)
            {
                throw new ArgumentException(
                    "A Peer handshake session must expire exactly ten minutes after UtcNow.",
                    nameof(expiresUtc));
            }

            InstanceId = instanceId;
            KeyEpoch = keyEpoch;
            _handshakeNonce = PeerControlModelValidation.CloneExactLength(
                handshakeNonce,
                nameof(handshakeNonce),
                PeerSyncContract.PairingNonceLength);
            _sessionId = PeerControlModelValidation.CloneExactLength(
                sessionId,
                nameof(sessionId),
                PeerSyncContract.SessionIdLength);
            ExpiresUtc = expiresUtc;
            UtcNow = utcNow;
            SyncEnabled = syncEnabled;
        }

        public Guid InstanceId { get; }

        public ulong KeyEpoch { get; }

        public DateTime ExpiresUtc { get; }

        public DateTime UtcNow { get; }

        public bool SyncEnabled { get; }

        public byte[] CopyHandshakeNonce()
        {
            return (byte[])_handshakeNonce.Clone();
        }

        public byte[] CopySessionId()
        {
            return (byte[])_sessionId.Clone();
        }
    }

    public sealed class PeerReleaseRequest
    {
        private readonly byte[] _sessionId;

        public PeerReleaseRequest(Guid instanceId, byte[] sessionId)
        {
            PeerControlModelValidation.ValidateGuid(
                instanceId,
                nameof(instanceId));

            InstanceId = instanceId;
            _sessionId = PeerControlModelValidation.CloneExactLength(
                sessionId,
                nameof(sessionId),
                PeerSyncContract.SessionIdLength);
        }

        public Guid InstanceId { get; }

        public byte[] CopySessionId()
        {
            return (byte[])_sessionId.Clone();
        }
    }

    public sealed class PeerRevokeRequest
    {
        public PeerRevokeRequest(
            Guid instanceId,
            Guid peerInstanceId,
            ulong keyEpoch,
            Guid revokeId)
        {
            PeerControlModelValidation.ValidatePeerBinding(
                instanceId,
                peerInstanceId,
                keyEpoch);
            PeerControlModelValidation.ValidateGuid(
                revokeId,
                nameof(revokeId));

            InstanceId = instanceId;
            PeerInstanceId = peerInstanceId;
            KeyEpoch = keyEpoch;
            RevokeId = revokeId;
        }

        public Guid InstanceId { get; }

        public Guid PeerInstanceId { get; }

        public ulong KeyEpoch { get; }

        public Guid RevokeId { get; }
    }

    public enum PeerControlResponseKind
    {
        UnitSuccess = 1,
        PairingHello = 2,
        PairingKeyConfirmation = 3,
        Handshake = 4,
        Error = 5
    }

    public sealed class PeerControlResponse
    {
        private PeerControlResponse(
            PeerControlResponseKind kind,
            PeerSyncResponseCode code,
            string message,
            PeerPairingHelloResult pairingHello,
            PeerPairingKeyConfirmation pairingKeyConfirmation,
            PeerHandshakeResult handshake)
        {
            if (!Enum.IsDefined(typeof(PeerControlResponseKind), kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }

            if (!Enum.IsDefined(typeof(PeerSyncResponseCode), code))
            {
                throw new ArgumentOutOfRangeException(nameof(code));
            }

            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (message.Length > 512)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(message),
                    "A Peer response message cannot exceed 512 characters.");
            }

            bool isError = kind == PeerControlResponseKind.Error;
            if (isError != (code != PeerSyncResponseCode.Ok))
            {
                throw new ArgumentException(
                    "Peer control response kind and code are inconsistent.",
                    nameof(code));
            }

            int payloadCount = (pairingHello == null ? 0 : 1)
                + (pairingKeyConfirmation == null ? 0 : 1)
                + (handshake == null ? 0 : 1);
            bool expectsPayload = kind != PeerControlResponseKind.UnitSuccess
                && kind != PeerControlResponseKind.Error;
            if (payloadCount != (expectsPayload ? 1 : 0)
                || (kind == PeerControlResponseKind.PairingHello
                    && pairingHello == null)
                || (kind == PeerControlResponseKind.PairingKeyConfirmation
                    && pairingKeyConfirmation == null)
                || (kind == PeerControlResponseKind.Handshake
                    && handshake == null))
            {
                throw new ArgumentException(
                    "Peer control response kind and payload are inconsistent.",
                    nameof(kind));
            }

            Kind = kind;
            Code = code;
            Message = message;
            PairingHello = pairingHello;
            PairingKeyConfirmation = pairingKeyConfirmation;
            Handshake = handshake;
        }

        public PeerControlResponseKind Kind { get; }

        public string Result => IsSuccess ? "OK" : "ERROR";

        public PeerSyncResponseCode Code { get; }

        public string Message { get; }

        public PeerPairingHelloResult PairingHello { get; }

        public PeerPairingKeyConfirmation PairingKeyConfirmation { get; }

        public PeerHandshakeResult Handshake { get; }

        public bool IsSuccess => Code == PeerSyncResponseCode.Ok;

        public static PeerControlResponse CreateUnitSuccess()
        {
            return CreateSuccess(
                PeerControlResponseKind.UnitSuccess,
                null,
                null,
                null,
                string.Empty);
        }

        public static PeerControlResponse CreatePairingHelloSuccess(
            PeerPairingHelloResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return CreateSuccess(
                PeerControlResponseKind.PairingHello,
                result,
                null,
                null,
                string.Empty);
        }

        public static PeerControlResponse CreatePairingKeyConfirmSuccess(
            PeerPairingKeyConfirmation result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return CreateSuccess(
                PeerControlResponseKind.PairingKeyConfirmation,
                null,
                result,
                null,
                string.Empty);
        }

        public static PeerControlResponse CreateHandshakeSuccess(
            PeerHandshakeResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return CreateSuccess(
                PeerControlResponseKind.Handshake,
                null,
                null,
                result,
                string.Empty);
        }

        public static PeerControlResponse CreateError(
            PeerSyncResponseCode code)
        {
            if (code == PeerSyncResponseCode.Ok)
            {
                throw new ArgumentOutOfRangeException(nameof(code));
            }

            return new PeerControlResponse(
                PeerControlResponseKind.Error,
                code,
                string.Empty,
                null,
                null,
                null);
        }

        internal static PeerControlResponse CreateParsedUnitSuccess(
            string message)
        {
            return CreateSuccess(
                PeerControlResponseKind.UnitSuccess,
                null,
                null,
                null,
                message);
        }

        internal static PeerControlResponse CreateParsedPairingHelloSuccess(
            PeerPairingHelloResult result,
            string message)
        {
            return CreateSuccess(
                PeerControlResponseKind.PairingHello,
                result,
                null,
                null,
                message);
        }

        internal static PeerControlResponse
            CreateParsedPairingKeyConfirmSuccess(
            PeerPairingKeyConfirmation result,
            string message)
        {
            return CreateSuccess(
                PeerControlResponseKind.PairingKeyConfirmation,
                null,
                result,
                null,
                message);
        }

        internal static PeerControlResponse CreateParsedHandshakeSuccess(
            PeerHandshakeResult result,
            string message)
        {
            return CreateSuccess(
                PeerControlResponseKind.Handshake,
                null,
                null,
                result,
                message);
        }

        internal static PeerControlResponse CreateParsedError(
            PeerSyncResponseCode code,
            string message)
        {
            return new PeerControlResponse(
                PeerControlResponseKind.Error,
                code,
                message,
                null,
                null,
                null);
        }

        private static PeerControlResponse CreateSuccess(
            PeerControlResponseKind kind,
            PeerPairingHelloResult pairingHello,
            PeerPairingKeyConfirmation pairingKeyConfirmation,
            PeerHandshakeResult handshake,
            string message)
        {
            return new PeerControlResponse(
                kind,
                PeerSyncResponseCode.Ok,
                message,
                pairingHello,
                pairingKeyConfirmation,
                handshake);
        }
    }

    internal static class PeerControlModelValidation
    {
        private static readonly byte[] P256PublicKeyHeader =
        {
            0x45, 0x43, 0x4b, 0x31,
            0x20, 0x00, 0x00, 0x00
        };

        internal static void ValidateGuid(Guid value, string parameterName)
        {
            if (value == Guid.Empty)
            {
                throw new ArgumentException(
                    "A Peer control GUID cannot be empty.",
                    parameterName);
            }
        }

        internal static void ValidatePositiveEpoch(
            ulong value,
            string parameterName)
        {
            if (value == 0)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }

        internal static void ValidatePeerBinding(
            Guid instanceId,
            Guid peerInstanceId,
            ulong keyEpoch)
        {
            ValidateGuid(instanceId, nameof(instanceId));
            ValidateGuid(peerInstanceId, nameof(peerInstanceId));
            if (instanceId == peerInstanceId)
            {
                throw new ArgumentException(
                    "Peer control instance IDs must be different.",
                    nameof(peerInstanceId));
            }

            ValidatePositiveEpoch(keyEpoch, nameof(keyEpoch));
        }

        internal static void ValidatePairingBinding(
            Guid pairingId,
            ulong keyEpoch,
            PeerPairingRole senderRole,
            Guid senderInstanceId,
            Guid receiverInstanceId)
        {
            ValidateGuid(pairingId, nameof(pairingId));
            ValidatePeerBinding(
                senderInstanceId,
                receiverInstanceId,
                keyEpoch);
            if (!Enum.IsDefined(typeof(PeerPairingRole), senderRole))
            {
                throw new ArgumentOutOfRangeException(nameof(senderRole));
            }
        }

        internal static string ValidateCanonicalEndpoint(
            string value,
            string parameterName)
        {
            string canonical;
            if (!AdminPeerEndpoint.TryNormalize(value, out canonical)
                || !StringComparer.Ordinal.Equals(value, canonical))
            {
                throw new ArgumentException(
                    "A Peer endpoint must be canonical.",
                    parameterName);
            }

            return canonical;
        }

        internal static void ValidateUtc(
            DateTime value,
            string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "A Peer control timestamp must be UTC.",
                    parameterName);
            }
        }

        internal static byte[] CloneExactLength(
            byte[] value,
            string parameterName,
            int expectedLength)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length != expectedLength)
            {
                throw new ArgumentException(
                    "A Peer control binary value has an invalid length.",
                    parameterName);
            }

            return (byte[])value.Clone();
        }

        internal static byte[] CloneP256PublicKey(
            byte[] value,
            string parameterName)
        {
            byte[] copy = CloneExactLength(
                value,
                parameterName,
                PeerSyncContract.PairingPublicKeyLength);
            for (int index = 0;
                index < P256PublicKeyHeader.Length;
                index++)
            {
                if (copy[index] != P256PublicKeyHeader[index])
                {
                    Array.Clear(copy, 0, copy.Length);
                    throw new ArgumentException(
                        "A Peer pairing public key is not an ECDH P-256 public blob.",
                        parameterName);
                }
            }

            try
            {
                // CNG import validates that the encoded coordinates form an
                // accepted point on P-256, not merely that the wire header and
                // lengths look correct.
                using (CngKey imported = CngKey.Import(
                    copy,
                    CngKeyBlobFormat.EccPublicBlob))
                {
                    if (!CngAlgorithmGroup.ECDiffieHellman.Equals(
                            imported.AlgorithmGroup)
                        || !CngAlgorithm.ECDiffieHellmanP256.Equals(
                            imported.Algorithm)
                        || imported.KeySize != 256)
                    {
                        throw new CryptographicException(
                            "The imported pairing key is not ECDH P-256.");
                    }
                }
            }
            catch (CryptographicException exception)
            {
                Array.Clear(copy, 0, copy.Length);
                throw new ArgumentException(
                    "A Peer pairing public key is not a valid ECDH P-256 curve point.",
                    parameterName,
                    exception);
            }

            return copy;
        }
    }
}
