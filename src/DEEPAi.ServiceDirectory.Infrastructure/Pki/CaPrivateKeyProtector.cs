using System;
using System.Security.Cryptography;
using System.Text;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal interface ICaPrivateKeyProtector
    {
        byte[] Protect(byte[] plaintextPkcs8);

        byte[] Unprotect(byte[] protectedBytes);
    }

    internal sealed class DpapiMachineCaPrivateKeyProtector
        : ICaPrivateKeyProtector
    {
        internal const int MaximumPlaintextBytes = 64 * 1024;
        internal const int MaximumProtectedBytes = 128 * 1024;

        private static readonly byte[] OptionalEntropy =
            Encoding.ASCII.GetBytes(
                "DEEPAi.ServiceDirectory.ca.key.v1");

        public byte[] Protect(byte[] plaintextPkcs8)
        {
            ValidatePlaintext(plaintextPkcs8);
            byte[] protectedBytes = ProtectedData.Protect(
                plaintextPkcs8,
                OptionalEntropy,
                DataProtectionScope.LocalMachine);
            if (protectedBytes == null
                || protectedBytes.Length == 0
                || protectedBytes.Length > MaximumProtectedBytes)
            {
                Clear(protectedBytes);
                throw new CryptographicException(
                    "DPAPI returned an invalid protected CA key size.");
            }

            return protectedBytes;
        }

        public byte[] Unprotect(byte[] protectedBytes)
        {
            if (protectedBytes == null
                || protectedBytes.Length == 0
                || protectedBytes.Length > MaximumProtectedBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(protectedBytes));
            }

            byte[] plaintext = ProtectedData.Unprotect(
                protectedBytes,
                OptionalEntropy,
                DataProtectionScope.LocalMachine);
            try
            {
                ValidatePlaintext(plaintext);
                return plaintext;
            }
            catch
            {
                Clear(plaintext);
                throw;
            }
        }

        private static void ValidatePlaintext(byte[] plaintextPkcs8)
        {
            if (plaintextPkcs8 == null
                || plaintextPkcs8.Length == 0
                || plaintextPkcs8.Length > MaximumPlaintextBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(plaintextPkcs8));
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
