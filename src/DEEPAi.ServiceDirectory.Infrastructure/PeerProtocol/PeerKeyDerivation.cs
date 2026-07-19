using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal enum PeerPairBoundKeyPurpose
    {
        HandshakeRequest = 1,
        HandshakeResponse = 2,
        RevokeRequest = 3,
        RevokeResponse = 4
    }

    internal enum PeerSessionKeyPurpose
    {
        Request = 1,
        Response = 2
    }

    internal static class PeerKeyDerivation
    {
        internal const int PairRootLength = 32;
        internal const int HandshakeNonceLength = 32;
        internal const int SessionIdLength = 16;
        internal const int DerivedKeyLength = 32;

        private static readonly Encoding Ascii = Encoding.ASCII;

        private static readonly byte[] HandshakeRequestLabel =
            Ascii.GetBytes("peer-handshake-request-v1");
        private static readonly byte[] HandshakeResponseLabel =
            Ascii.GetBytes("peer-handshake-response-v1");
        private static readonly byte[] SessionRequestLabel =
            Ascii.GetBytes("peer-session-request-v1");
        private static readonly byte[] SessionResponseLabel =
            Ascii.GetBytes("peer-session-response-v1");
        private static readonly byte[] RevokeRequestLabel =
            Ascii.GetBytes("peer-revoke-request-v1");
        private static readonly byte[] RevokeResponseLabel =
            Ascii.GetBytes("peer-revoke-response-v1");

        // Pair-bound keys are used only for handshake and revoke messages.
        // The returned 32-byte key is caller-owned and must be cleared after use.
        internal static byte[] DerivePairBoundKey(
            byte[] pairRoot,
            ulong keyEpoch,
            Guid localInstanceId,
            Guid peerInstanceId,
            PeerPairBoundKeyPurpose purpose)
        {
            ValidatePairBinding(
                pairRoot,
                keyEpoch,
                localInstanceId,
                peerInstanceId);

            byte[] label = GetPairBoundLabel(purpose);
            byte[] input = CreateCommonInput(
                label,
                keyEpoch,
                localInstanceId,
                peerInstanceId,
                null,
                null,
                null);
            try
            {
                return ComputeHmacSha256(pairRoot, input);
            }
            finally
            {
                Zero(input);
            }
        }

        // Session keys are bound to both handshake nonces and the issued
        // 128-bit session ID. Request and response keys are purpose-separated.
        // The returned 32-byte key is caller-owned and must be cleared after use.
        internal static byte[] DeriveSessionKey(
            byte[] pairRoot,
            ulong keyEpoch,
            Guid localInstanceId,
            Guid peerInstanceId,
            byte[] handshakeRequestNonce,
            byte[] handshakeResponseNonce,
            byte[] sessionId,
            PeerSessionKeyPurpose purpose)
        {
            ValidatePairBinding(
                pairRoot,
                keyEpoch,
                localInstanceId,
                peerInstanceId);
            ValidateExactLength(
                handshakeRequestNonce,
                nameof(handshakeRequestNonce),
                HandshakeNonceLength);
            ValidateExactLength(
                handshakeResponseNonce,
                nameof(handshakeResponseNonce),
                HandshakeNonceLength);
            ValidateExactLength(
                sessionId,
                nameof(sessionId),
                SessionIdLength);

            byte[] label = GetSessionLabel(purpose);
            byte[] input = CreateCommonInput(
                label,
                keyEpoch,
                localInstanceId,
                peerInstanceId,
                handshakeRequestNonce,
                handshakeResponseNonce,
                sessionId);
            try
            {
                return ComputeHmacSha256(pairRoot, input);
            }
            finally
            {
                Zero(input);
            }
        }

        private static byte[] CreateCommonInput(
            byte[] label,
            ulong keyEpoch,
            Guid localInstanceId,
            Guid peerInstanceId,
            byte[] handshakeRequestNonce,
            byte[] handshakeResponseNonce,
            byte[] sessionId)
        {
            byte[] epoch = Ascii.GetBytes(
                keyEpoch.ToString(CultureInfo.InvariantCulture));
            byte[] firstInstance;
            byte[] secondInstance;
            CreateCanonicalInstancePair(
                localInstanceId,
                peerInstanceId,
                out firstInstance,
                out secondInstance);

            bool sessionContext = handshakeRequestNonce != null;
            int length = LengthPrefixedSize(label)
                + LengthPrefixedSize(epoch)
                + LengthPrefixedSize(firstInstance)
                + LengthPrefixedSize(secondInstance);
            if (sessionContext)
            {
                length += LengthPrefixedSize(handshakeRequestNonce)
                    + LengthPrefixedSize(handshakeResponseNonce)
                    + LengthPrefixedSize(sessionId);
            }

            var input = new byte[length];
            int offset = 0;
            try
            {
                offset = WriteLengthPrefixed(input, offset, label);
                offset = WriteLengthPrefixed(input, offset, epoch);
                offset = WriteLengthPrefixed(input, offset, firstInstance);
                offset = WriteLengthPrefixed(input, offset, secondInstance);
                if (sessionContext)
                {
                    offset = WriteLengthPrefixed(
                        input,
                        offset,
                        handshakeRequestNonce);
                    offset = WriteLengthPrefixed(
                        input,
                        offset,
                        handshakeResponseNonce);
                    offset = WriteLengthPrefixed(input, offset, sessionId);
                }

                if (offset != input.Length)
                {
                    throw new CryptographicException(
                        "The Peer KDF input length is inconsistent.");
                }

                return input;
            }
            catch
            {
                Zero(input);
                throw;
            }
            finally
            {
                Zero(epoch);
                Zero(firstInstance);
                Zero(secondInstance);
            }
        }

        private static void CreateCanonicalInstancePair(
            Guid localInstanceId,
            Guid peerInstanceId,
            out byte[] firstInstance,
            out byte[] secondInstance)
        {
            string local = localInstanceId
                .ToString("D")
                .ToLowerInvariant();
            string peer = peerInstanceId
                .ToString("D")
                .ToLowerInvariant();
            if (StringComparer.Ordinal.Compare(local, peer) < 0)
            {
                firstInstance = Ascii.GetBytes(local);
                secondInstance = Ascii.GetBytes(peer);
            }
            else
            {
                firstInstance = Ascii.GetBytes(peer);
                secondInstance = Ascii.GetBytes(local);
            }
        }

        private static byte[] GetPairBoundLabel(
            PeerPairBoundKeyPurpose purpose)
        {
            switch (purpose)
            {
                case PeerPairBoundKeyPurpose.HandshakeRequest:
                    return HandshakeRequestLabel;
                case PeerPairBoundKeyPurpose.HandshakeResponse:
                    return HandshakeResponseLabel;
                case PeerPairBoundKeyPurpose.RevokeRequest:
                    return RevokeRequestLabel;
                case PeerPairBoundKeyPurpose.RevokeResponse:
                    return RevokeResponseLabel;
                default:
                    throw new ArgumentOutOfRangeException(nameof(purpose));
            }
        }

        private static byte[] GetSessionLabel(PeerSessionKeyPurpose purpose)
        {
            switch (purpose)
            {
                case PeerSessionKeyPurpose.Request:
                    return SessionRequestLabel;
                case PeerSessionKeyPurpose.Response:
                    return SessionResponseLabel;
                default:
                    throw new ArgumentOutOfRangeException(nameof(purpose));
            }
        }

        private static void ValidatePairBinding(
            byte[] pairRoot,
            ulong keyEpoch,
            Guid localInstanceId,
            Guid peerInstanceId)
        {
            ValidateExactLength(
                pairRoot,
                nameof(pairRoot),
                PairRootLength);
            if (keyEpoch == 0UL)
            {
                throw new ArgumentOutOfRangeException(nameof(keyEpoch));
            }

            if (localInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The local instance ID cannot be empty.",
                    nameof(localInstanceId));
            }

            if (peerInstanceId == Guid.Empty
                || peerInstanceId == localInstanceId)
            {
                throw new ArgumentException(
                    "The peer instance ID must be non-empty and distinct.",
                    nameof(peerInstanceId));
            }
        }

        private static int LengthPrefixedSize(byte[] value)
        {
            return checked(sizeof(uint) + value.Length);
        }

        private static int WriteLengthPrefixed(
            byte[] destination,
            int offset,
            byte[] value)
        {
            uint length = checked((uint)value.Length);
            destination[offset] = (byte)(length >> 24);
            destination[offset + 1] = (byte)(length >> 16);
            destination[offset + 2] = (byte)(length >> 8);
            destination[offset + 3] = (byte)length;
            Buffer.BlockCopy(
                value,
                0,
                destination,
                offset + sizeof(uint),
                value.Length);
            return checked(offset + sizeof(uint) + value.Length);
        }

        private static byte[] ComputeHmacSha256(byte[] key, byte[] input)
        {
            byte[] keyCopy = (byte[])key.Clone();
            try
            {
                using (var hmac = new HMACSHA256(keyCopy))
                {
                    byte[] result = hmac.ComputeHash(input);
                    if (result.Length != DerivedKeyLength)
                    {
                        Zero(result);
                        throw new CryptographicException(
                            "HMAC-SHA256 returned an unexpected Peer key length.");
                    }

                    return result;
                }
            }
            finally
            {
                Zero(keyCopy);
            }
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

        private static void Zero(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }
    }
}
