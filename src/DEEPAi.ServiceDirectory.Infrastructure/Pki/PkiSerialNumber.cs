using System;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed class PkiSerialNumber : IEquatable<PkiSerialNumber>
    {
        internal const int ByteLength = 16;
        internal const int HexLength = ByteLength * 2;
        private const int MaximumGenerationAttempts = 128;

        private readonly byte[] _bytes;

        private PkiSerialNumber(byte[] bytes)
        {
            _bytes = (byte[])bytes.Clone();
            Hex = ToUpperHex(_bytes);
            Value = new BigInteger(1, _bytes);
        }

        internal string Hex { get; }

        internal BigInteger Value { get; }

        internal byte[] GetBytes()
        {
            return (byte[])_bytes.Clone();
        }

        internal CertificateSerialNumber ToLedgerSerialNumber()
        {
            CertificateSerialNumber serialNumber;
            if (!CertificateSerialNumber.TryCreate(Hex, out serialNumber))
            {
                throw new InvalidOperationException(
                    "The generated PKI serial does not satisfy the ledger contract.");
            }

            return serialNumber;
        }

        internal static PkiSerialNumber CreateRandom(
            SecureRandom random,
            Func<string, bool> isReserved)
        {
            if (random == null)
            {
                throw new ArgumentNullException(nameof(random));
            }

            var bytes = new byte[ByteLength];
            try
            {
                for (int attempt = 0; attempt < MaximumGenerationAttempts; attempt++)
                {
                    random.NextBytes(bytes);
                    if (bytes[0] == 0 || bytes[0] > 0x7f)
                    {
                        continue;
                    }

                    var candidate = new PkiSerialNumber(bytes);
                    if (isReserved == null || !isReserved(candidate.Hex))
                    {
                        return candidate;
                    }
                }
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }

            throw new InvalidOperationException(
                "A unique certificate serial could not be generated within the bounded attempt limit.");
        }

        internal static bool TryParse(string value, out PkiSerialNumber serialNumber)
        {
            serialNumber = null;
            if (value == null || value.Length != HexLength)
            {
                return false;
            }

            var bytes = new byte[ByteLength];
            try
            {
                for (int index = 0; index < bytes.Length; index++)
                {
                    int high = ParseUpperHex(value[index * 2]);
                    int low = ParseUpperHex(value[(index * 2) + 1]);
                    if (high < 0 || low < 0)
                    {
                        return false;
                    }

                    bytes[index] = (byte)((high << 4) | low);
                }

                if (bytes[0] == 0 || bytes[0] > 0x7f)
                {
                    return false;
                }

                serialNumber = new PkiSerialNumber(bytes);
                return true;
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        internal static bool TryCreate(BigInteger value, out PkiSerialNumber serialNumber)
        {
            serialNumber = null;
            if (value == null || value.SignValue <= 0)
            {
                return false;
            }

            byte[] bytes = value.ToByteArrayUnsigned();
            try
            {
                if (bytes.Length != ByteLength
                    || bytes[0] == 0
                    || bytes[0] > 0x7f)
                {
                    return false;
                }

                serialNumber = new PkiSerialNumber(bytes);
                return true;
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        public bool Equals(PkiSerialNumber other)
        {
            return !ReferenceEquals(other, null)
                && StringComparer.Ordinal.Equals(Hex, other.Hex);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as PkiSerialNumber);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(Hex);
        }

        private static int ParseUpperHex(char value)
        {
            if (value >= '0' && value <= '9')
            {
                return value - '0';
            }

            if (value >= 'A' && value <= 'F')
            {
                return value - 'A' + 10;
            }

            return -1;
        }

        private static string ToUpperHex(byte[] bytes)
        {
            const string HexDigits = "0123456789ABCDEF";
            var characters = new char[bytes.Length * 2];
            for (int index = 0; index < bytes.Length; index++)
            {
                characters[index * 2] = HexDigits[bytes[index] >> 4];
                characters[(index * 2) + 1] = HexDigits[bytes[index] & 0x0f];
            }

            return new string(characters);
        }
    }
}
