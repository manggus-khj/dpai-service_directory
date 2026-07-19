using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal static class PairingTranscript
    {
        private static readonly Encoding Ascii = Encoding.ASCII;
        private static readonly byte[] Algorithm =
            Ascii.GetBytes(PeerSyncContract.PairingAlgorithm);

        // Returns the caller-owned SHA-256 transcript hash. Caller inputs are
        // validated and never mutated or cleared.
        internal static byte[] CreateHash(
            Guid pairingId,
            Guid initiatorInstanceId,
            Guid responderInstanceId,
            string initiatorEndpoint,
            string responderEndpoint,
            byte[] initiatorNonce,
            byte[] responderNonce,
            byte[] initiatorPublicKey,
            byte[] responderPublicKey,
            ulong initiatorLastPeerKeyEpoch,
            ulong responderLastPeerKeyEpoch,
            ulong keyEpoch)
        {
            ValidateIdentifiers(
                pairingId,
                initiatorInstanceId,
                responderInstanceId);
            ValidateEpoch(
                initiatorLastPeerKeyEpoch,
                responderLastPeerKeyEpoch,
                keyEpoch);
            ValidateExactLength(
                initiatorNonce,
                nameof(initiatorNonce),
                PeerSyncContract.PairingNonceLength);
            ValidateExactLength(
                responderNonce,
                nameof(responderNonce),
                PeerSyncContract.PairingNonceLength);
            ValidatePublicKey(
                initiatorPublicKey,
                nameof(initiatorPublicKey));
            ValidatePublicKey(
                responderPublicKey,
                nameof(responderPublicKey));

            string canonicalInitiatorEndpoint = ValidateEndpoint(
                initiatorEndpoint,
                nameof(initiatorEndpoint));
            string canonicalResponderEndpoint = ValidateEndpoint(
                responderEndpoint,
                nameof(responderEndpoint));

            byte[] pairingIdBytes = null;
            byte[] initiatorIdBytes = null;
            byte[] responderIdBytes = null;
            byte[] initiatorEndpointBytes = null;
            byte[] responderEndpointBytes = null;
            byte[] initiatorEpochBytes = null;
            byte[] responderEpochBytes = null;
            byte[] keyEpochBytes = null;
            byte[] canonical = null;
            try
            {
                pairingIdBytes = GuidBytes(pairingId);
                initiatorIdBytes = GuidBytes(initiatorInstanceId);
                responderIdBytes = GuidBytes(responderInstanceId);
                initiatorEndpointBytes = Ascii.GetBytes(
                    canonicalInitiatorEndpoint);
                responderEndpointBytes = Ascii.GetBytes(
                    canonicalResponderEndpoint);
                initiatorEpochBytes = UnsignedBytes(
                    initiatorLastPeerKeyEpoch);
                responderEpochBytes = UnsignedBytes(
                    responderLastPeerKeyEpoch);
                keyEpochBytes = UnsignedBytes(keyEpoch);

                canonical = EncodeLengthPrefixed(
                    Algorithm,
                    pairingIdBytes,
                    initiatorIdBytes,
                    responderIdBytes,
                    initiatorEndpointBytes,
                    responderEndpointBytes,
                    initiatorNonce,
                    responderNonce,
                    initiatorPublicKey,
                    responderPublicKey,
                    initiatorEpochBytes,
                    responderEpochBytes,
                    keyEpochBytes);
                using (SHA256 sha256 = SHA256.Create())
                {
                    return sha256.ComputeHash(canonical);
                }
            }
            finally
            {
                Clear(pairingIdBytes);
                Clear(initiatorIdBytes);
                Clear(responderIdBytes);
                Clear(initiatorEndpointBytes);
                Clear(responderEndpointBytes);
                Clear(initiatorEpochBytes);
                Clear(responderEpochBytes);
                Clear(keyEpochBytes);
                Clear(canonical);
            }
        }

        private static void ValidateIdentifiers(
            Guid pairingId,
            Guid initiatorInstanceId,
            Guid responderInstanceId)
        {
            if (pairingId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The pairing ID must not be empty.",
                    nameof(pairingId));
            }

            if (initiatorInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The initiator instance ID must not be empty.",
                    nameof(initiatorInstanceId));
            }

            if (responderInstanceId == Guid.Empty
                || responderInstanceId == initiatorInstanceId)
            {
                throw new ArgumentException(
                    "The responder instance ID must be non-empty and different from the initiator.",
                    nameof(responderInstanceId));
            }
        }

        private static void ValidateEpoch(
            ulong initiatorLastPeerKeyEpoch,
            ulong responderLastPeerKeyEpoch,
            ulong keyEpoch)
        {
            if (initiatorLastPeerKeyEpoch == ulong.MaxValue
                || responderLastPeerKeyEpoch == ulong.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keyEpoch),
                    "A new key epoch cannot be issued after UInt64.MaxValue.");
            }

            ulong expected = Math.Max(
                initiatorLastPeerKeyEpoch,
                responderLastPeerKeyEpoch) + 1UL;
            if (keyEpoch != expected)
            {
                throw new ArgumentException(
                    "The key epoch must equal max(last peer epochs) plus one.",
                    nameof(keyEpoch));
            }
        }

        private static string ValidateEndpoint(
            string endpoint,
            string parameterName)
        {
            string canonical;
            if (!AdminPeerEndpoint.TryNormalize(endpoint, out canonical)
                || !StringComparer.Ordinal.Equals(endpoint, canonical))
            {
                throw new ArgumentException(
                    "The pairing endpoint must be canonical.",
                    parameterName);
            }

            return canonical;
        }

        private static void ValidatePublicKey(
            byte[] publicKey,
            string parameterName)
        {
            if (publicKey == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            try
            {
                using (ECDiffieHellmanPublicKey imported =
                    P256PublicKeyBlob.Import(publicKey))
                {
                }
            }
            catch (CryptographicException exception)
            {
                throw new ArgumentException(
                    "The pairing public key must be a valid ECDH P-256 public blob.",
                    parameterName,
                    exception);
            }
        }

        private static byte[] EncodeLengthPrefixed(params byte[][] fields)
        {
            long totalLength = 0;
            foreach (byte[] field in fields)
            {
                if (field == null || field.Length == 0)
                {
                    throw new ArgumentException(
                        "Pairing transcript fields must not be empty.",
                        nameof(fields));
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
