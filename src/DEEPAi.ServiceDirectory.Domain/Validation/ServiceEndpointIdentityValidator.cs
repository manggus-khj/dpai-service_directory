using System;
using System.Globalization;

namespace DEEPAi.ServiceDirectory.Domain.Validation
{
    internal static class ServiceEndpointIdentityValidator
    {
        internal static bool TryCreate(
            string rawServiceHostName,
            string rawServiceIpv4Address,
            out ServiceEndpointIdentity identity,
            out EndpointIdentityValidationError error)
        {
            identity = null;

            string serviceHostName;
            if (!TryNormalizeHostName(rawServiceHostName, out serviceHostName, out error))
            {
                return false;
            }

            string serviceIpv4Address;
            if (!TryNormalizeIpv4Address(
                    rawServiceIpv4Address,
                    out serviceIpv4Address,
                    out error))
            {
                return false;
            }

            identity = new ServiceEndpointIdentity(
                serviceHostName,
                serviceIpv4Address);
            error = EndpointIdentityValidationError.None;
            return true;
        }

        private static bool TryNormalizeHostName(
            string rawValue,
            out string normalizedValue,
            out EndpointIdentityValidationError error)
        {
            normalizedValue = null;
            if (rawValue == null)
            {
                error = EndpointIdentityValidationError.ServiceHostNameRequired;
                return false;
            }

            string candidate = rawValue.Trim();
            if (candidate.Length == 0)
            {
                error = EndpointIdentityValidationError.ServiceHostNameRequired;
                return false;
            }

            if (candidate.Length > 253
                || candidate[0] == '.'
                || candidate[candidate.Length - 1] == '.'
                || candidate.IndexOf('*') >= 0)
            {
                error = EndpointIdentityValidationError.ServiceHostNameInvalid;
                return false;
            }

            bool containsAsciiLetter = false;
            string[] labels = candidate.Split('.');
            foreach (string label in labels)
            {
                if (label.Length == 0
                    || label.Length > 63
                    || label[0] == '-'
                    || label[label.Length - 1] == '-')
                {
                    error = EndpointIdentityValidationError.ServiceHostNameInvalid;
                    return false;
                }

                foreach (char current in label)
                {
                    bool isAsciiUpper = current >= 'A' && current <= 'Z';
                    bool isAsciiLower = current >= 'a' && current <= 'z';
                    bool isAsciiDigit = current >= '0' && current <= '9';
                    if (!isAsciiUpper
                        && !isAsciiLower
                        && !isAsciiDigit
                        && current != '-')
                    {
                        error = EndpointIdentityValidationError.ServiceHostNameInvalid;
                        return false;
                    }

                    containsAsciiLetter |= isAsciiUpper || isAsciiLower;
                }
            }

            if (!containsAsciiLetter)
            {
                error = EndpointIdentityValidationError.ServiceHostNameInvalid;
                return false;
            }

            normalizedValue = candidate.ToLowerInvariant();
            error = EndpointIdentityValidationError.None;
            return true;
        }

        private static bool TryNormalizeIpv4Address(
            string rawValue,
            out string normalizedValue,
            out EndpointIdentityValidationError error)
        {
            normalizedValue = null;
            if (rawValue == null)
            {
                error = EndpointIdentityValidationError.ServiceIpv4AddressRequired;
                return false;
            }

            string candidate = rawValue.Trim();
            if (candidate.Length == 0)
            {
                error = EndpointIdentityValidationError.ServiceIpv4AddressRequired;
                return false;
            }

            string[] octets = candidate.Split('.');
            if (octets.Length != 4)
            {
                error = EndpointIdentityValidationError.ServiceIpv4AddressInvalid;
                return false;
            }

            var parsedOctets = new int[4];
            for (int index = 0; index < octets.Length; index++)
            {
                string octet = octets[index];
                if (octet.Length == 0
                    || octet.Length > 3
                    || (octet.Length > 1 && octet[0] == '0'))
                {
                    error = EndpointIdentityValidationError.ServiceIpv4AddressInvalid;
                    return false;
                }

                for (int charIndex = 0; charIndex < octet.Length; charIndex++)
                {
                    if (octet[charIndex] < '0' || octet[charIndex] > '9')
                    {
                        error = EndpointIdentityValidationError.ServiceIpv4AddressInvalid;
                        return false;
                    }
                }

                int parsed;
                if (!int.TryParse(
                        octet,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out parsed)
                    || parsed > 255)
                {
                    error = EndpointIdentityValidationError.ServiceIpv4AddressInvalid;
                    return false;
                }

                parsedOctets[index] = parsed;
            }

            bool isWildcard = parsedOctets[0] == 0
                && parsedOctets[1] == 0
                && parsedOctets[2] == 0
                && parsedOctets[3] == 0;
            bool isLoopback = parsedOctets[0] == 127;
            bool isApipa = parsedOctets[0] == 169 && parsedOctets[1] == 254;
            bool isMulticast = parsedOctets[0] >= 224 && parsedOctets[0] <= 239;
            bool isLimitedBroadcast = parsedOctets[0] == 255
                && parsedOctets[1] == 255
                && parsedOctets[2] == 255
                && parsedOctets[3] == 255;
            if (isWildcard
                || isLoopback
                || isApipa
                || isMulticast
                || isLimitedBroadcast)
            {
                error = EndpointIdentityValidationError.ServiceIpv4AddressInvalid;
                return false;
            }

            normalizedValue = string.Join(
                ".",
                parsedOctets[0].ToString(CultureInfo.InvariantCulture),
                parsedOctets[1].ToString(CultureInfo.InvariantCulture),
                parsedOctets[2].ToString(CultureInfo.InvariantCulture),
                parsedOctets[3].ToString(CultureInfo.InvariantCulture));
            error = EndpointIdentityValidationError.None;
            return true;
        }
    }
}
