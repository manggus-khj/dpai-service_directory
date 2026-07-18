using System;

namespace DEEPAi.ServiceDirectory.Domain
{
    public readonly struct ProductCode : IEquatable<ProductCode>
    {
        private readonly string _value;

        private ProductCode(string value)
        {
            _value = value;
        }

        public string Value => _value ?? string.Empty;

        public bool IsValid => _value != null;

        public static bool TryCreate(string rawValue, out ProductCode productCode)
        {
            productCode = default(ProductCode);
            if (rawValue == null)
            {
                return false;
            }

            string normalized = rawValue.Trim().ToUpperInvariant();
            if (normalized.Length != 4)
            {
                return false;
            }

            for (int index = 0; index < normalized.Length; index++)
            {
                char value = normalized[index];
                bool isUpperAsciiLetter = value >= 'A' && value <= 'Z';
                bool isAsciiDigit = value >= '0' && value <= '9';
                if (!isUpperAsciiLetter && !isAsciiDigit)
                {
                    return false;
                }
            }

            productCode = new ProductCode(normalized);
            return true;
        }

        public bool Equals(ProductCode other)
        {
            return StringComparer.OrdinalIgnoreCase.Equals(_value, other._value);
        }

        public override bool Equals(object obj)
        {
            return obj is ProductCode other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _value == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(_value);
        }

        public override string ToString()
        {
            return Value;
        }

        public static bool operator ==(ProductCode left, ProductCode right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ProductCode left, ProductCode right)
        {
            return !left.Equals(right);
        }
    }
}
