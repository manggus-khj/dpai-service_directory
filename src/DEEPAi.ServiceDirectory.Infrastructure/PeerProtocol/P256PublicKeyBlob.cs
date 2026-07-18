using System;
using System.Security.Cryptography;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal static class P256PublicKeyBlob
    {
        internal const int EncodedLength = 72;

        private static readonly byte[] ExpectedHeader =
        {
            0x45, 0x43, 0x4B, 0x31, // BCRYPT_ECDH_PUBLIC_P256_MAGIC (ECK1)
            0x20, 0x00, 0x00, 0x00  // 32-byte X and Y coordinates
        };

        // The returned public key is owned by the caller and must be disposed.
        // The caller-owned encoded buffer is never modified or cleared.
        internal static ECDiffieHellmanPublicKey Import(byte[] encodedPublicKey)
        {
            if (encodedPublicKey == null)
            {
                throw new ArgumentNullException(nameof(encodedPublicKey));
            }

            if (encodedPublicKey.Length != EncodedLength)
            {
                throw new CryptographicException(
                    "The peer public key must be an exact 72-byte ECDH P-256 public blob.");
            }

            for (int index = 0; index < ExpectedHeader.Length; index++)
            {
                if (encodedPublicKey[index] != ExpectedHeader[index])
                {
                    throw new CryptographicException(
                        "The peer public key does not have an ECDH P-256 public blob header.");
                }
            }

            byte[] importBuffer = (byte[])encodedPublicKey.Clone();
            try
            {
                // Import validates the coordinates as a CNG ECDH key, including whether
                // the supplied point is accepted for the P-256 curve.
                using (CngKey importedKey = CngKey.Import(
                    importBuffer,
                    CngKeyBlobFormat.EccPublicBlob))
                {
                    if (!CngAlgorithmGroup.ECDiffieHellman.Equals(importedKey.AlgorithmGroup)
                        || !CngAlgorithm.ECDiffieHellmanP256.Equals(importedKey.Algorithm)
                        || importedKey.KeySize != 256)
                    {
                        throw new CryptographicException(
                            "The peer public key is not an ECDH P-256 public key.");
                    }
                }

                return ECDiffieHellmanCngPublicKey.FromByteArray(
                    importBuffer,
                    CngKeyBlobFormat.EccPublicBlob);
            }
            finally
            {
                Array.Clear(importBuffer, 0, importBuffer.Length);
            }
        }
    }
}
