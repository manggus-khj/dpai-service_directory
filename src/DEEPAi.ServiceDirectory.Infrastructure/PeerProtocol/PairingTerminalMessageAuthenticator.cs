using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal enum PairingTerminalDecision
    {
        Confirmed = 1,
        Cancelled = 2
    }

    // Creates the purpose-separated MACs for the pairing decision and durable
    // commit messages. Pairing-window state, endpoint binding, terminal-state
    // replay handling, and storage of exact signed responses remain caller
    // responsibilities.
    internal static class PairingTerminalMessageAuthenticator
    {
        private const int AuthenticationCodeLength = 32;
        private const int TranscriptHashLength = 32;

        private static readonly Encoding Ascii = Encoding.ASCII;
        private static readonly byte[] DecisionInitiatorKeyLabel =
            Ascii.GetBytes("pair-decision-initiator-v1");
        private static readonly byte[] DecisionResponderKeyLabel =
            Ascii.GetBytes("pair-decision-responder-v1");
        private static readonly byte[] CommitInitiatorKeyLabel =
            Ascii.GetBytes("pair-commit-initiator-v1");
        private static readonly byte[] CommitResponderKeyLabel =
            Ascii.GetBytes("pair-commit-responder-v1");
        private static readonly byte[] DecisionRequestDomain =
            Ascii.GetBytes("DPAI-SD-PAIR-DECISION-REQUEST-v1");
        private static readonly byte[] DecisionResponseDomain =
            Ascii.GetBytes("DPAI-SD-PAIR-DECISION-RESPONSE-v1");
        private static readonly byte[] CommitRequestDomain =
            Ascii.GetBytes("DPAI-SD-PAIR-COMMIT-REQUEST-v1");
        private static readonly byte[] CommitResponseDomain =
            Ascii.GetBytes("DPAI-SD-PAIR-COMMIT-RESPONSE-v1");
        private static readonly byte[] CommitMarker =
            Ascii.GetBytes("COMMIT");
        private static readonly byte[] ConfirmedDecision =
            Ascii.GetBytes("CONFIRMED");
        private static readonly byte[] CancelledDecision =
            Ascii.GetBytes("CANCELLED");

        internal static byte[] CreateDecisionRequestMac(
            byte[] k0,
            byte[] transcriptHash,
            Guid pairingId,
            ulong keyEpoch,
            PairingConfirmationDirection senderRole,
            Guid senderInstanceId,
            Guid receiverInstanceId,
            PairingTerminalDecision decision)
        {
            byte[] decisionValue = GetDecisionValue(decision);
            return CreateRequestMac(
                k0,
                transcriptHash,
                pairingId,
                keyEpoch,
                senderRole,
                senderInstanceId,
                receiverInstanceId,
                GetDecisionKeyLabel(senderRole),
                DecisionRequestDomain,
                decisionValue);
        }

        internal static byte[] CreateCommitRequestMac(
            byte[] pairRoot,
            byte[] transcriptHash,
            Guid pairingId,
            ulong keyEpoch,
            PairingConfirmationDirection senderRole,
            Guid senderInstanceId,
            Guid receiverInstanceId)
        {
            return CreateRequestMac(
                pairRoot,
                transcriptHash,
                pairingId,
                keyEpoch,
                senderRole,
                senderInstanceId,
                receiverInstanceId,
                GetCommitKeyLabel(senderRole),
                CommitRequestDomain,
                CommitMarker);
        }

        internal static byte[] CreateDecisionResponseMac(
            byte[] k0,
            byte[] transcriptHash,
            Guid pairingId,
            ulong keyEpoch,
            PairingConfirmationDirection senderRole,
            Guid senderInstanceId,
            Guid receiverInstanceId,
            byte[] requestMac,
            int httpStatus,
            string result,
            uint code,
            byte[] rawResponseBody)
        {
            return CreateResponseMac(
                k0,
                transcriptHash,
                pairingId,
                keyEpoch,
                senderRole,
                senderInstanceId,
                receiverInstanceId,
                requestMac,
                httpStatus,
                result,
                code,
                rawResponseBody,
                GetDecisionKeyLabel(senderRole),
                DecisionResponseDomain);
        }

        internal static byte[] CreateCommitResponseMac(
            byte[] pairRoot,
            byte[] transcriptHash,
            Guid pairingId,
            ulong keyEpoch,
            PairingConfirmationDirection senderRole,
            Guid senderInstanceId,
            Guid receiverInstanceId,
            byte[] requestMac,
            int httpStatus,
            string result,
            uint code,
            byte[] rawResponseBody)
        {
            return CreateResponseMac(
                pairRoot,
                transcriptHash,
                pairingId,
                keyEpoch,
                senderRole,
                senderInstanceId,
                receiverInstanceId,
                requestMac,
                httpStatus,
                result,
                code,
                rawResponseBody,
                GetCommitKeyLabel(senderRole),
                CommitResponseDomain);
        }

        internal static bool VerifyMac(byte[] expected, byte[] candidate)
        {
            ValidateExactLength(
                expected,
                nameof(expected),
                AuthenticationCodeLength);
            return candidate != null
                && candidate.Length == AuthenticationCodeLength
                && PairingCryptography.FixedTimeEquals32(
                    expected,
                    candidate);
        }

        private static byte[] CreateRequestMac(
            byte[] rootKey,
            byte[] transcriptHash,
            Guid pairingId,
            ulong keyEpoch,
            PairingConfirmationDirection senderRole,
            Guid senderInstanceId,
            Guid receiverInstanceId,
            byte[] purposeKeyLabel,
            byte[] domain,
            byte[] terminalValue)
        {
            ValidateBinding(
                rootKey,
                transcriptHash,
                pairingId,
                keyEpoch,
                senderRole,
                senderInstanceId,
                receiverInstanceId);

            byte[] pairingIdBytes = null;
            byte[] epochBytes = null;
            byte[] roleBytes = null;
            byte[] senderBytes = null;
            byte[] receiverBytes = null;
            byte[] canonical = null;
            byte[] purposeKey = null;
            try
            {
                pairingIdBytes = GuidBytes(pairingId);
                epochBytes = UnsignedBytes(keyEpoch);
                roleBytes = RoleBytes(senderRole);
                senderBytes = GuidBytes(senderInstanceId);
                receiverBytes = GuidBytes(receiverInstanceId);
                canonical = EncodeLengthPrefixed(
                    domain,
                    transcriptHash,
                    pairingIdBytes,
                    epochBytes,
                    roleBytes,
                    senderBytes,
                    receiverBytes,
                    terminalValue);
                purposeKey = DerivePurposeKey(
                    rootKey,
                    purposeKeyLabel,
                    transcriptHash);
                return ComputeHmacSha256(purposeKey, canonical);
            }
            finally
            {
                Clear(pairingIdBytes);
                Clear(epochBytes);
                Clear(roleBytes);
                Clear(senderBytes);
                Clear(receiverBytes);
                Clear(canonical);
                Clear(purposeKey);
            }
        }

        private static byte[] CreateResponseMac(
            byte[] rootKey,
            byte[] transcriptHash,
            Guid pairingId,
            ulong keyEpoch,
            PairingConfirmationDirection senderRole,
            Guid senderInstanceId,
            Guid receiverInstanceId,
            byte[] requestMac,
            int httpStatus,
            string result,
            uint code,
            byte[] rawResponseBody,
            byte[] purposeKeyLabel,
            byte[] domain)
        {
            ValidateBinding(
                rootKey,
                transcriptHash,
                pairingId,
                keyEpoch,
                senderRole,
                senderInstanceId,
                receiverInstanceId);
            ValidateExactLength(
                requestMac,
                nameof(requestMac),
                AuthenticationCodeLength);
            if (rawResponseBody == null)
            {
                throw new ArgumentNullException(nameof(rawResponseBody));
            }

            ValidateResponseStatusAndCode(httpStatus, result, code);

            byte[] pairingIdBytes = null;
            byte[] epochBytes = null;
            byte[] roleBytes = null;
            byte[] senderBytes = null;
            byte[] receiverBytes = null;
            byte[] requestMacHash = null;
            byte[] statusBytes = null;
            byte[] resultBytes = null;
            byte[] codeBytes = null;
            byte[] bodyHash = null;
            byte[] canonical = null;
            byte[] purposeKey = null;
            try
            {
                pairingIdBytes = GuidBytes(pairingId);
                epochBytes = UnsignedBytes(keyEpoch);
                roleBytes = RoleBytes(senderRole);
                senderBytes = GuidBytes(senderInstanceId);
                receiverBytes = GuidBytes(receiverInstanceId);
                requestMacHash = ComputeSha256(requestMac);
                statusBytes = Ascii.GetBytes(
                    httpStatus.ToString(CultureInfo.InvariantCulture));
                resultBytes = Ascii.GetBytes(result);
                codeBytes = Ascii.GetBytes(
                    code.ToString(CultureInfo.InvariantCulture));
                bodyHash = ComputeSha256(rawResponseBody);
                canonical = EncodeLengthPrefixed(
                    domain,
                    transcriptHash,
                    pairingIdBytes,
                    epochBytes,
                    roleBytes,
                    senderBytes,
                    receiverBytes,
                    requestMacHash,
                    statusBytes,
                    resultBytes,
                    codeBytes,
                    bodyHash);
                purposeKey = DerivePurposeKey(
                    rootKey,
                    purposeKeyLabel,
                    transcriptHash);
                return ComputeHmacSha256(purposeKey, canonical);
            }
            finally
            {
                Clear(pairingIdBytes);
                Clear(epochBytes);
                Clear(roleBytes);
                Clear(senderBytes);
                Clear(receiverBytes);
                Clear(requestMacHash);
                Clear(statusBytes);
                Clear(resultBytes);
                Clear(codeBytes);
                Clear(bodyHash);
                Clear(canonical);
                Clear(purposeKey);
            }
        }

        private static void ValidateResponseStatusAndCode(
            int httpStatus,
            string result,
            uint rawCode)
        {
            PeerSyncResponseCode code = (PeerSyncResponseCode)rawCode;
            if (!Enum.IsDefined(typeof(PeerSyncResponseCode), code))
            {
                throw new ArgumentOutOfRangeException(nameof(rawCode));
            }

            bool isSuccess = StringComparer.Ordinal.Equals(result, "OK");
            bool isError = StringComparer.Ordinal.Equals(result, "ERROR");
            if (code == PeerSyncResponseCode.Ok)
            {
                if (!isSuccess || httpStatus != 200)
                {
                    throw new ArgumentException(
                        "A successful pairing response must use HTTP 200, Result=OK, and Code=0.",
                        nameof(result));
                }

                return;
            }

            if (!isError || !IsStatusAllowedForCode(httpStatus, code))
            {
                throw new ArgumentException(
                    "The pairing response HTTP status, Result, and Code are inconsistent.",
                    nameof(result));
            }
        }

        private static bool IsStatusAllowedForCode(
            int httpStatus,
            PeerSyncResponseCode code)
        {
            switch (code)
            {
                case PeerSyncResponseCode.BadRequest:
                    return httpStatus == 400;
                case PeerSyncResponseCode.NotFound:
                    return httpStatus == 404;
                case PeerSyncResponseCode.Conflict:
                case PeerSyncResponseCode.PeerMismatch:
                case PeerSyncResponseCode.SyncDisabled:
                case PeerSyncResponseCode.RevisionCollision:
                case PeerSyncResponseCode.DirectoryCapacity:
                case PeerSyncResponseCode.LogicalClockExhausted:
                    return httpStatus == 409;
                case PeerSyncResponseCode.LimitExceeded:
                    return httpStatus == 413 || httpStatus == 429;
                case PeerSyncResponseCode.NotPeer:
                    return httpStatus == 403;
                case PeerSyncResponseCode.ClockSkew:
                    return httpStatus == 401;
                case PeerSyncResponseCode.Internal:
                    return httpStatus == 500;
                case PeerSyncResponseCode.Ok:
                default:
                    return false;
            }
        }

        private static void ValidateBinding(
            byte[] rootKey,
            byte[] transcriptHash,
            Guid pairingId,
            ulong keyEpoch,
            PairingConfirmationDirection senderRole,
            Guid senderInstanceId,
            Guid receiverInstanceId)
        {
            ValidateExactLength(
                rootKey,
                nameof(rootKey),
                AuthenticationCodeLength);
            ValidateExactLength(
                transcriptHash,
                nameof(transcriptHash),
                TranscriptHashLength);
            if (pairingId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The pairing ID must not be empty.",
                    nameof(pairingId));
            }

            if (keyEpoch == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(keyEpoch));
            }

            GetRoleText(senderRole);
            if (senderInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The sender instance ID must not be empty.",
                    nameof(senderInstanceId));
            }

            if (receiverInstanceId == Guid.Empty
                || receiverInstanceId == senderInstanceId)
            {
                throw new ArgumentException(
                    "The receiver instance ID must be non-empty and different from the sender.",
                    nameof(receiverInstanceId));
            }
        }

        private static byte[] DerivePurposeKey(
            byte[] rootKey,
            byte[] label,
            byte[] transcriptHash)
        {
            var input = new byte[label.Length + transcriptHash.Length];
            try
            {
                Buffer.BlockCopy(label, 0, input, 0, label.Length);
                Buffer.BlockCopy(
                    transcriptHash,
                    0,
                    input,
                    label.Length,
                    transcriptHash.Length);
                return ComputeHmacSha256(rootKey, input);
            }
            finally
            {
                Clear(input);
            }
        }

        private static byte[] EncodeLengthPrefixed(params byte[][] fields)
        {
            long totalLength = 0;
            foreach (byte[] field in fields)
            {
                if (field == null)
                {
                    throw new ArgumentNullException(nameof(fields));
                }

                totalLength = checked(totalLength + sizeof(uint)
                    + field.Length);
            }

            if (totalLength > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(fields));
            }

            var result = new byte[(int)totalLength];
            int offset = 0;
            foreach (byte[] field in fields)
            {
                WriteUInt32BigEndian(
                    result,
                    offset,
                    checked((uint)field.Length));
                offset += sizeof(uint);
                Buffer.BlockCopy(
                    field,
                    0,
                    result,
                    offset,
                    field.Length);
                offset += field.Length;
            }

            return result;
        }

        private static byte[] ComputeSha256(byte[] value)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(value);
            }
        }

        private static byte[] ComputeHmacSha256(byte[] key, byte[] value)
        {
            byte[] keyCopy = (byte[])key.Clone();
            try
            {
                using (var hmac = new HMACSHA256(keyCopy))
                {
                    return hmac.ComputeHash(value);
                }
            }
            finally
            {
                Clear(keyCopy);
            }
        }

        private static byte[] GuidBytes(Guid value)
        {
            return Ascii.GetBytes(
                value.ToString("D").ToLowerInvariant());
        }

        private static byte[] UnsignedBytes(ulong value)
        {
            return Ascii.GetBytes(
                value.ToString(CultureInfo.InvariantCulture));
        }

        private static byte[] RoleBytes(
            PairingConfirmationDirection role)
        {
            return Ascii.GetBytes(GetRoleText(role));
        }

        private static string GetRoleText(
            PairingConfirmationDirection role)
        {
            switch (role)
            {
                case PairingConfirmationDirection.Initiator:
                    return "initiator";
                case PairingConfirmationDirection.Responder:
                    return "responder";
                default:
                    throw new ArgumentOutOfRangeException(nameof(role));
            }
        }

        private static byte[] GetDecisionValue(
            PairingTerminalDecision decision)
        {
            switch (decision)
            {
                case PairingTerminalDecision.Confirmed:
                    return ConfirmedDecision;
                case PairingTerminalDecision.Cancelled:
                    return CancelledDecision;
                default:
                    throw new ArgumentOutOfRangeException(nameof(decision));
            }
        }

        private static byte[] GetDecisionKeyLabel(
            PairingConfirmationDirection role)
        {
            switch (role)
            {
                case PairingConfirmationDirection.Initiator:
                    return DecisionInitiatorKeyLabel;
                case PairingConfirmationDirection.Responder:
                    return DecisionResponderKeyLabel;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role));
            }
        }

        private static byte[] GetCommitKeyLabel(
            PairingConfirmationDirection role)
        {
            switch (role)
            {
                case PairingConfirmationDirection.Initiator:
                    return CommitInitiatorKeyLabel;
                case PairingConfirmationDirection.Responder:
                    return CommitResponderKeyLabel;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role));
            }
        }

        private static void WriteUInt32BigEndian(
            byte[] destination,
            int offset,
            uint value)
        {
            destination[offset] = (byte)(value >> 24);
            destination[offset + 1] = (byte)(value >> 16);
            destination[offset + 2] = (byte)(value >> 8);
            destination[offset + 3] = (byte)value;
        }

        private static void ValidateExactLength(
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
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "The value must contain exactly {0} bytes.",
                        expectedLength),
                    parameterName);
            }
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }
    }
}
