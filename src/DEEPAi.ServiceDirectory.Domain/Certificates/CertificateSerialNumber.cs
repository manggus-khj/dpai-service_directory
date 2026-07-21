using System;

namespace DEEPAi.ServiceDirectory.Domain.Certificates
{
    public readonly struct CertificateSerialNumber : IEquatable<CertificateSerialNumber>
    {
        public const int HexLength = 32;

        private readonly string _hex;

        private CertificateSerialNumber(string hex)
        {
            _hex = hex;
        }

        public string Hex => _hex ?? string.Empty;

        public bool IsValid => _hex != null;

        public static bool TryCreate(
            string value,
            out CertificateSerialNumber serialNumber)
        {
            serialNumber = default(CertificateSerialNumber);
            if (value == null || value.Length != HexLength)
            {
                return false;
            }

            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                bool isDigit = current >= '0' && current <= '9';
                bool isUpperHex = current >= 'A' && current <= 'F';
                if (!isDigit && !isUpperHex)
                {
                    return false;
                }
            }

            int firstByte = (ParseUpperHex(value[0]) << 4)
                | ParseUpperHex(value[1]);
            if (firstByte < 0x01 || firstByte > 0x7f)
            {
                return false;
            }

            serialNumber = new CertificateSerialNumber(value);
            return true;
        }

        public bool Equals(CertificateSerialNumber other)
        {
            return StringComparer.Ordinal.Equals(_hex, other._hex);
        }

        public override bool Equals(object obj)
        {
            return obj is CertificateSerialNumber other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _hex == null ? 0 : StringComparer.Ordinal.GetHashCode(_hex);
        }

        public override string ToString()
        {
            return Hex;
        }

        public byte[] ToByteArray()
        {
            if (!IsValid)
            {
                throw new InvalidOperationException(
                    "Certificate serial number is not initialized.");
            }

            var bytes = new byte[HexLength / 2];
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] = (byte)((ParseUpperHex(_hex[index * 2]) << 4)
                    | ParseUpperHex(_hex[(index * 2) + 1]));
            }

            return bytes;
        }

        public static bool operator ==(
            CertificateSerialNumber left,
            CertificateSerialNumber right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(
            CertificateSerialNumber left,
            CertificateSerialNumber right)
        {
            return !left.Equals(right);
        }

        private static int ParseUpperHex(char value)
        {
            return value <= '9'
                ? value - '0'
                : value - 'A' + 10;
        }
    }
}
