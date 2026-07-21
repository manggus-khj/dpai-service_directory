using System;
using System.Text;

namespace DEEPAi.ServiceDirectory.Domain.Validation
{
    internal static class ServiceDefinitionValidator
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        internal static bool TryCreate(
            string rawName,
            string rawProductCode,
            ServiceEndpointIdentity serviceEndpointIdentity,
            int port,
            out ServiceDefinition definition,
            out ServiceDefinitionValidationError error)
        {
            definition = null;

            string name;
            if (!TryNormalizeName(rawName, out name, out error))
            {
                return false;
            }

            ProductCode productCode;
            if (!ProductCode.TryCreate(rawProductCode, out productCode))
            {
                error = ServiceDefinitionValidationError.ProductCodeInvalid;
                return false;
            }

            if (serviceEndpointIdentity == null)
            {
                error = ServiceDefinitionValidationError
                    .ServiceEndpointIdentityRequired;
                return false;
            }

            if (port < 1 || port > ushort.MaxValue)
            {
                error = ServiceDefinitionValidationError.PortOutOfRange;
                return false;
            }

            definition = new ServiceDefinition(
                name,
                productCode,
                serviceEndpointIdentity,
                port);
            error = ServiceDefinitionValidationError.None;
            return true;
        }

        private static bool TryNormalizeName(
            string rawName,
            out string normalizedName,
            out ServiceDefinitionValidationError error)
        {
            normalizedName = null;
            if (rawName == null)
            {
                error = ServiceDefinitionValidationError.NameRequired;
                return false;
            }

            string candidate = rawName.Trim();
            if (candidate.Length == 0)
            {
                error = ServiceDefinitionValidationError.NameRequired;
                return false;
            }

            int scalarCount = 0;
            for (int index = 0; index < candidate.Length; index++)
            {
                char value = candidate[index];
                if (char.IsControl(value)
                    || value == '\ufffe'
                    || value == '\uffff')
                {
                    error = ServiceDefinitionValidationError
                        .NameContainsInvalidCharacter;
                    return false;
                }

                if (char.IsHighSurrogate(value))
                {
                    if (index + 1 >= candidate.Length
                        || !char.IsLowSurrogate(candidate[index + 1]))
                    {
                        error = ServiceDefinitionValidationError
                            .NameContainsInvalidCharacter;
                        return false;
                    }

                    index++;
                }
                else if (char.IsLowSurrogate(value))
                {
                    error = ServiceDefinitionValidationError
                        .NameContainsInvalidCharacter;
                    return false;
                }

                scalarCount++;
                if (scalarCount > 128)
                {
                    error = ServiceDefinitionValidationError.NameTooLong;
                    return false;
                }
            }

            if (StrictUtf8.GetByteCount(candidate) > 512)
            {
                error = ServiceDefinitionValidationError.NameTooLong;
                return false;
            }

            normalizedName = candidate;
            error = ServiceDefinitionValidationError.None;
            return true;
        }
    }
}
