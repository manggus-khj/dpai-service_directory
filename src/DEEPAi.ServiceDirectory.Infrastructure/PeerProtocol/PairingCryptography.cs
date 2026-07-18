using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal enum PairingConfirmationDirection
    {
        Initiator = 1,
        Responder = 2
    }

    internal static class PairingCryptography
    {
        internal const int KeyMaterialLength = 32;
        internal const int TranscriptHashLength = 32;
        internal const int AuthenticationCodeLength = 32;

        private const uint SasRejectionLimit = 4200000000U;
        private const uint SasModulus = 100000000U;

        private static readonly byte[] K0Prepend =
            Encoding.ASCII.GetBytes("DPAI-SD-PAIR-K0-v1");
        private static readonly byte[] InitiatorConfirmationLabel =
            Encoding.ASCII.GetBytes("pair-confirm-initiator-v1");
        private static readonly byte[] ResponderConfirmationLabel =
            Encoding.ASCII.GetBytes("pair-confirm-responder-v1");
        private static readonly byte[] SasKeyLabel =
            Encoding.ASCII.GetBytes("pair-sas-v1");
        private static readonly byte[] SasDigitsLabel =
            Encoding.ASCII.GetBytes("sas-digits-v1");
        private static readonly byte[] PairRootLabel =
            Encoding.ASCII.GetBytes("pair-root-v1");

        // The returned 32-byte K0 buffer is newly allocated and owned by the caller.
        // The caller must clear it after the pairing step. Input buffers are never cleared.
        internal static byte[] DeriveK0(
            ECDiffieHellmanCng localKeyAgreement,
            byte[] peerPublicKeyBlob,
            byte[] transcriptHash)
        {
            if (localKeyAgreement == null)
            {
                throw new ArgumentNullException(nameof(localKeyAgreement));
            }

            ValidateLocalKeyAgreement(localKeyAgreement);
            ValidateExactLength(
                transcriptHash,
                nameof(transcriptHash),
                TranscriptHashLength);

            byte[] prepend = (byte[])K0Prepend.Clone();
            byte[] append = (byte[])transcriptHash.Clone();
            byte[] derivedKey = null;
            try
            {
                using (ECDiffieHellmanPublicKey peerPublicKey =
                    P256PublicKeyBlob.Import(peerPublicKeyBlob))
                {
                    derivedKey = localKeyAgreement.DeriveKeyFromHash(
                        peerPublicKey,
                        HashAlgorithmName.SHA256,
                        prepend,
                        append);
                }

                if (derivedKey == null || derivedKey.Length != KeyMaterialLength)
                {
                    Zero(derivedKey);
                    derivedKey = null;
                    throw new CryptographicException(
                        "The ECDH P-256 hash KDF returned an unexpected key length.");
                }

                return derivedKey;
            }
            finally
            {
                Zero(prepend);
                Zero(append);
            }
        }

        // The returned MAC buffer is newly allocated and owned by the caller.
        internal static byte[] CreateConfirmationMac(
            byte[] k0,
            byte[] transcriptHash,
            PairingConfirmationDirection direction)
        {
            ValidateExactLength(k0, nameof(k0), KeyMaterialLength);
            ValidateExactLength(
                transcriptHash,
                nameof(transcriptHash),
                TranscriptHashLength);

            byte[] purposeLabel = GetConfirmationLabel(direction);
            byte[] confirmationKey = null;
            try
            {
                confirmationKey = DerivePurposeKey(k0, purposeLabel, transcriptHash);
                return ComputeHmacSha256(confirmationKey, transcriptHash);
            }
            finally
            {
                Zero(confirmationKey);
            }
        }

        // The returned pair root buffer is newly allocated and owned by the caller.
        // It must be cleared after it has been transferred to protected persistence.
        internal static byte[] DerivePairRoot(byte[] k0, byte[] transcriptHash)
        {
            ValidateExactLength(k0, nameof(k0), KeyMaterialLength);
            ValidateExactLength(
                transcriptHash,
                nameof(transcriptHash),
                TranscriptHashLength);

            return DerivePurposeKey(k0, PairRootLabel, transcriptHash);
        }

        // SAS is display-only pairing data. The returned 8-character buffer is owned
        // by the caller and must be cleared after display. It must not be sent over
        // the network, persisted, or logged. Input buffers remain caller-owned.
        internal static char[] CreateSas(byte[] k0, byte[] transcriptHash)
        {
            ValidateExactLength(k0, nameof(k0), KeyMaterialLength);
            ValidateExactLength(
                transcriptHash,
                nameof(transcriptHash),
                TranscriptHashLength);

            byte[] sasKey = null;
            byte[] input = null;
            try
            {
                sasKey = DerivePurposeKey(k0, SasKeyLabel, transcriptHash);
                input = new byte[
                    SasDigitsLabel.Length
                    + TranscriptHashLength
                    + sizeof(uint)];
                Buffer.BlockCopy(
                    SasDigitsLabel,
                    0,
                    input,
                    0,
                    SasDigitsLabel.Length);
                Buffer.BlockCopy(
                    transcriptHash,
                    0,
                    input,
                    SasDigitsLabel.Length,
                    TranscriptHashLength);

                int counterOffset = SasDigitsLabel.Length + TranscriptHashLength;
                using (var hmac = new HMACSHA256(sasKey))
                {
                    uint counter = 0;
                    while (true)
                    {
                        WriteUInt32BigEndian(input, counterOffset, counter);

                        byte[] digest = null;
                        uint candidate;
                        try
                        {
                            digest = hmac.ComputeHash(input);
                            candidate = ReadUInt32BigEndian(digest);
                        }
                        finally
                        {
                            Zero(digest);
                        }

                        if (candidate < SasRejectionLimit)
                        {
                            return CreateDecimalDigits(candidate % SasModulus);
                        }

                        if (counter == uint.MaxValue)
                        {
                            throw new CryptographicException(
                                "SAS rejection sampling exhausted the counter range.");
                        }

                        counter++;
                    }
                }
            }
            finally
            {
                Zero(sasKey);
                Zero(input);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal static bool FixedTimeEquals32(byte[] left, byte[] right)
        {
            ValidateExactLength(left, nameof(left), AuthenticationCodeLength);
            ValidateExactLength(right, nameof(right), AuthenticationCodeLength);

            int difference = 0;
            for (int index = 0; index < AuthenticationCodeLength; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        private static void ValidateLocalKeyAgreement(
            ECDiffieHellmanCng localKeyAgreement)
        {
            CngKey localKey = localKeyAgreement.Key;
            if (localKeyAgreement.KeySize != 256
                || localKey == null
                || !CngAlgorithmGroup.ECDiffieHellman.Equals(localKey.AlgorithmGroup)
                || !CngAlgorithm.ECDiffieHellmanP256.Equals(localKey.Algorithm)
                || localKey.KeySize != 256)
            {
                throw new ArgumentException(
                    "The local key agreement must use an ECDH P-256 key.",
                    nameof(localKeyAgreement));
            }
        }

        private static byte[] GetConfirmationLabel(
            PairingConfirmationDirection direction)
        {
            switch (direction)
            {
                case PairingConfirmationDirection.Initiator:
                    return InitiatorConfirmationLabel;
                case PairingConfirmationDirection.Responder:
                    return ResponderConfirmationLabel;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(direction),
                        direction,
                        "The confirmation direction must be initiator or responder.");
            }
        }

        private static byte[] DerivePurposeKey(
            byte[] k0,
            byte[] purposeLabel,
            byte[] transcriptHash)
        {
            byte[] input = new byte[purposeLabel.Length + TranscriptHashLength];
            try
            {
                Buffer.BlockCopy(purposeLabel, 0, input, 0, purposeLabel.Length);
                Buffer.BlockCopy(
                    transcriptHash,
                    0,
                    input,
                    purposeLabel.Length,
                    TranscriptHashLength);
                return ComputeHmacSha256(k0, input);
            }
            finally
            {
                Zero(input);
            }
        }

        private static byte[] ComputeHmacSha256(byte[] key, byte[] input)
        {
            byte[] keyCopy = (byte[])key.Clone();
            try
            {
                using (var hmac = new HMACSHA256(keyCopy))
                {
                    byte[] result = hmac.ComputeHash(input);
                    if (result.Length != AuthenticationCodeLength)
                    {
                        Zero(result);
                        throw new CryptographicException(
                            "HMAC-SHA256 returned an unexpected authentication code length.");
                    }

                    return result;
                }
            }
            finally
            {
                Zero(keyCopy);
            }
        }

        private static uint ReadUInt32BigEndian(byte[] value)
        {
            if (value == null || value.Length < sizeof(uint))
            {
                throw new CryptographicException(
                    "The SAS digest is too short to contain a 32-bit value.");
            }

            return ((uint)value[0] << 24)
                | ((uint)value[1] << 16)
                | ((uint)value[2] << 8)
                | value[3];
        }

        private static char[] CreateDecimalDigits(uint value)
        {
            var digits = new char[8];
            for (int index = digits.Length - 1; index >= 0; index--)
            {
                digits[index] = (char)('0' + (value % 10U));
                value /= 10U;
            }

            return digits;
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

        private static void Zero(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }
    }
}
