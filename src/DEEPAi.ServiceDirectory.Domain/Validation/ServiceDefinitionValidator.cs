using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DEEPAi.ServiceDirectory.Domain.Validation
{
    internal static class ServiceDefinitionValidator
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static bool TryCreate(
            string rawName,
            string rawProductCode,
            string rawServerAddress,
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

            string serverAddress;
            if (!TryNormalizeServerAddress(rawServerAddress, out serverAddress, out error))
            {
                return false;
            }

            if (port < 1 || port > 65535)
            {
                error = ServiceDefinitionValidationError.PortOutOfRange;
                return false;
            }

            definition = new ServiceDefinition(name, productCode, serverAddress, port);
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
                char current = candidate[index];
                if (char.IsHighSurrogate(current))
                {
                    if (index + 1 >= candidate.Length || !char.IsLowSurrogate(candidate[index + 1]))
                    {
                        error = ServiceDefinitionValidationError.NameContainsInvalidCharacter;
                        return false;
                    }

                    if (CharUnicodeInfo.GetUnicodeCategory(candidate, index) == UnicodeCategory.Control)
                    {
                        error = ServiceDefinitionValidationError.NameContainsInvalidCharacter;
                        return false;
                    }

                    index++;
                }
                else
                {
                    if (char.IsLowSurrogate(current)
                        || CharUnicodeInfo.GetUnicodeCategory(current) == UnicodeCategory.Control)
                    {
                        error = ServiceDefinitionValidationError.NameContainsInvalidCharacter;
                        return false;
                    }
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

        private static bool TryNormalizeServerAddress(
            string rawServerAddress,
            out string normalizedAddress,
            out ServiceDefinitionValidationError error)
        {
            normalizedAddress = null;
            if (rawServerAddress == null)
            {
                error = ServiceDefinitionValidationError.ServerAddressRequired;
                return false;
            }

            string candidate = rawServerAddress.Trim();
            if (candidate.Length == 0)
            {
                error = ServiceDefinitionValidationError.ServerAddressRequired;
                return false;
            }

            if (candidate.IndexOf(':') >= 0)
            {
                IPAddress ipv6Address;
                int lastColonIndex = candidate.LastIndexOf(':');
                string embeddedIpv4 = candidate.Substring(lastColonIndex + 1);
                if (candidate.IndexOf('%') >= 0
                    || candidate.IndexOf('[') >= 0
                    || candidate.IndexOf(']') >= 0
                    || (candidate.IndexOf('.') >= 0 && !IsValidIpv4(embeddedIpv4))
                    || !IPAddress.TryParse(candidate, out ipv6Address)
                    || ipv6Address.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    error = ServiceDefinitionValidationError.ServerAddressInvalid;
                    return false;
                }

                normalizedAddress = candidate;
                error = ServiceDefinitionValidationError.None;
                return true;
            }

            if (LooksLikeIpv4(candidate))
            {
                if (!IsValidIpv4(candidate))
                {
                    error = ServiceDefinitionValidationError.ServerAddressInvalid;
                    return false;
                }

                normalizedAddress = candidate;
                error = ServiceDefinitionValidationError.None;
                return true;
            }

            if (!IsValidDnsName(candidate))
            {
                error = ServiceDefinitionValidationError.ServerAddressInvalid;
                return false;
            }

            normalizedAddress = candidate;
            error = ServiceDefinitionValidationError.None;
            return true;
        }

        private static bool LooksLikeIpv4(string value)
        {
            bool hasDot = false;
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (current == '.')
                {
                    hasDot = true;
                }
                else if (current < '0' || current > '9')
                {
                    return false;
                }
            }

            return hasDot;
        }

        private static bool IsValidIpv4(string value)
        {
            string[] octets = value.Split('.');
            if (octets.Length != 4)
            {
                return false;
            }

            for (int index = 0; index < octets.Length; index++)
            {
                string octet = octets[index];
                if (octet.Length == 0 || octet.Length > 3)
                {
                    return false;
                }

                if (octet.Length > 1 && octet[0] == '0')
                {
                    return false;
                }

                for (int charIndex = 0; charIndex < octet.Length; charIndex++)
                {
                    if (octet[charIndex] < '0' || octet[charIndex] > '9')
                    {
                        return false;
                    }
                }

                int parsed;
                if (!int.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
                    || parsed > 255)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidDnsName(string value)
        {
            if (value.Length > 253 || value[0] == '.' || value[value.Length - 1] == '.')
            {
                return false;
            }

            bool containsNonDigit = false;
            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] < '0' || value[index] > '9')
                {
                    containsNonDigit = true;
                    break;
                }
            }

            if (!containsNonDigit)
            {
                return false;
            }

            string[] labels = value.Split('.');
            foreach (string label in labels)
            {
                if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-')
                {
                    return false;
                }

                foreach (char current in label)
                {
                    bool isAsciiLetter = (current >= 'A' && current <= 'Z')
                        || (current >= 'a' && current <= 'z');
                    bool isAsciiDigit = current >= '0' && current <= '9';
                    if (!isAsciiLetter && !isAsciiDigit && current != '-')
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
