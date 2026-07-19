using System;
using System.Security.Cryptography;
using System.Text;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal interface IPeerCredentialProtector
    {
        byte[] Protect(byte[] plaintext);

        byte[] Unprotect(byte[] protectedBytes);
    }

    internal sealed class DpapiMachinePeerCredentialProtector
        : IPeerCredentialProtector
    {
        // This is public domain-separation entropy, not a secret or a shared
        // application credential. DPAPI LocalMachine owns the protection key.
        private static readonly byte[] OptionalEntropy =
            Encoding.ASCII.GetBytes(
                "DEEPAi.ServiceDirectory.peer.dat.v1");

        public byte[] Protect(byte[] plaintext)
        {
            if (plaintext == null)
            {
                throw new ArgumentNullException(nameof(plaintext));
            }

            if (plaintext.Length == 0
                || plaintext.Length
                    > PeerCredentialBinaryCodec.MaximumPlaintextBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(plaintext),
                    plaintext.Length,
                    "The peer credential plaintext has an invalid size.");
            }

            return ProtectedData.Protect(
                plaintext,
                OptionalEntropy,
                DataProtectionScope.LocalMachine);
        }

        public byte[] Unprotect(byte[] protectedBytes)
        {
            if (protectedBytes == null)
            {
                throw new ArgumentNullException(nameof(protectedBytes));
            }

            if (protectedBytes.Length == 0
                || protectedBytes.Length
                    > PeerCredentialFile.MaximumProtectedBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(protectedBytes),
                    protectedBytes.Length,
                    "The protected peer credential has an invalid size.");
            }

            byte[] plaintext = ProtectedData.Unprotect(
                protectedBytes,
                OptionalEntropy,
                DataProtectionScope.LocalMachine);
            if (plaintext == null
                || plaintext.Length == 0
                || plaintext.Length
                    > PeerCredentialBinaryCodec.MaximumPlaintextBytes)
            {
                Clear(plaintext);
                throw new CryptographicException(
                    "DPAPI returned an invalid peer credential plaintext size.");
            }

            return plaintext;
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
