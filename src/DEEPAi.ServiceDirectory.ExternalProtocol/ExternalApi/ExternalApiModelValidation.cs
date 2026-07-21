using System;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi
{
    internal static class ExternalApiModelValidation
    {
        internal static ExternalNormalizedServiceDefinition NormalizeService(
            string name,
            string productCode,
            string serviceHostName,
            string serviceIpv4Address,
            int port,
            string parameterName)
        {
            ServiceEndpointIdentity endpointIdentity;
            EndpointIdentityValidationError endpointError;
            if (!ServiceEndpointIdentity.TryCreate(
                    serviceHostName,
                    serviceIpv4Address,
                    out endpointIdentity,
                    out endpointError))
            {
                throw new ArgumentException(
                    "The external service endpoint identity is invalid: "
                    + endpointError
                    + ".",
                    parameterName);
            }

            ServiceDefinition definition;
            ServiceDefinitionValidationError definitionError;
            if (!ServiceDefinition.TryCreate(
                    name,
                    productCode,
                    endpointIdentity,
                    port,
                    out definition,
                    out definitionError))
            {
                throw new ArgumentException(
                    "The external service definition is invalid: "
                    + definitionError
                    + ".",
                    parameterName);
            }

            return new ExternalNormalizedServiceDefinition(
                definition.Name,
                definition.ProductCode.Value,
                endpointIdentity.ServiceHostName,
                endpointIdentity.ServiceIpv4Address,
                definition.Port);
        }

        internal static Guid RequireNonEmptyGuid(Guid value, string parameterName)
        {
            if (value == Guid.Empty)
            {
                throw new ArgumentException(
                    "The external GUID value must not be empty.",
                    parameterName);
            }

            return value;
        }

        internal static DateTime RequireUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "External API timestamps must use DateTimeKind.Utc.",
                    parameterName);
            }

            return value;
        }

        internal static byte[] CloneRequiredBytes(
            byte[] value,
            int exactLength,
            int maximumLength,
            string parameterName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length == 0
                || (exactLength > 0 && value.Length != exactLength)
                || (maximumLength > 0 && value.Length > maximumLength))
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "The external binary value has an invalid length.");
            }

            return (byte[])value.Clone();
        }

        internal static string RequireSerialNumber(
            string serialNumber,
            string parameterName)
        {
            if (serialNumber == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (serialNumber.Length != 32)
            {
                throw new ArgumentException(
                    "Certificate serial numbers must contain exactly 32 uppercase hexadecimal characters.",
                    parameterName);
            }

            for (int index = 0; index < serialNumber.Length; index++)
            {
                char current = serialNumber[index];
                bool isDigit = current >= '0' && current <= '9';
                bool isUpperHex = current >= 'A' && current <= 'F';
                if (!isDigit && !isUpperHex)
                {
                    throw new ArgumentException(
                        "Certificate serial numbers must contain exactly 32 uppercase hexadecimal characters.",
                        parameterName);
                }
            }

            int firstByte = ParseHexNibble(serialNumber[0]) * 16
                + ParseHexNibble(serialNumber[1]);
            if (firstByte < 1 || firstByte > 0x7f)
            {
                throw new ArgumentException(
                    "Certificate serial numbers must encode a positive 16-byte value without sign padding.",
                    parameterName);
            }

            return serialNumber;
        }

        private static int ParseHexNibble(char value)
        {
            return value <= '9'
                ? value - '0'
                : value - 'A' + 10;
        }
    }

    internal sealed class ExternalNormalizedServiceDefinition
    {
        internal ExternalNormalizedServiceDefinition(
            string name,
            string productCode,
            string serviceHostName,
            string serviceIpv4Address,
            int port)
        {
            Name = name;
            ProductCode = productCode;
            ServiceHostName = serviceHostName;
            ServiceIpv4Address = serviceIpv4Address;
            Port = port;
        }

        internal string Name { get; }

        internal string ProductCode { get; }

        internal string ServiceHostName { get; }

        internal string ServiceIpv4Address { get; }

        internal int Port { get; }
    }
}
