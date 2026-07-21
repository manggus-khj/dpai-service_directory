using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Admin
{
    public static class AdminPeerEndpoint
    {
        private const string SchemePrefix = "https://";
        private const string PortSuffix = ":21000";

        public static bool TryNormalize(
            string value,
            out string canonicalEndpoint)
        {
            canonicalEndpoint = null;
            if (value == null)
            {
                return false;
            }

            string candidate = value.Trim();
            if (candidate.Length == 0
                || candidate.Length > 80
                || !candidate.StartsWith(
                    SchemePrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string authority = candidate.Substring(SchemePrefix.Length);
            if (authority.Length == 0
                || authority.IndexOf('/') >= 0
                || authority.IndexOf('\\') >= 0
                || authority.IndexOf('?') >= 0
                || authority.IndexOf('#') >= 0
                || authority.IndexOf('@') >= 0)
            {
                return false;
            }

            IPAddress address;
            int portSeparator = authority.LastIndexOf(':');
            if (portSeparator <= 0
                || authority.IndexOf(':') != portSeparator
                || !StringComparer.Ordinal.Equals(
                    authority.Substring(portSeparator),
                    PortSuffix)
                || !TryParseCanonicalIpv4(
                    authority.Substring(0, portSeparator),
                    out address))
            {
                return false;
            }

            if (IsUnsupportedPeerAddress(address))
            {
                return false;
            }

            canonicalEndpoint = SchemePrefix + address + PortSuffix;
            return canonicalEndpoint.Length <= 80;
        }

        public static string Normalize(string value)
        {
            string canonicalEndpoint;
            if (!TryNormalize(value, out canonicalEndpoint))
            {
                throw new ArgumentException(
                    "Peer endpoint must be a canonical HTTPS IPv4 literal on port 21000.",
                    nameof(value));
            }

            return canonicalEndpoint;
        }

        private static bool TryParseCanonicalIpv4(
            string value,
            out IPAddress address)
        {
            address = null;
            string[] octets = value.Split('.');
            if (octets.Length != 4)
            {
                return false;
            }

            var bytes = new byte[4];
            for (int octetIndex = 0; octetIndex < octets.Length; octetIndex++)
            {
                string octet = octets[octetIndex];
                if (octet.Length == 0
                    || octet.Length > 3
                    || (octet.Length > 1 && octet[0] == '0'))
                {
                    return false;
                }

                for (int characterIndex = 0;
                    characterIndex < octet.Length;
                    characterIndex++)
                {
                    char current = octet[characterIndex];
                    if (current < '0' || current > '9')
                    {
                        return false;
                    }
                }

                byte parsed;
                if (!byte.TryParse(
                    octet,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed))
                {
                    return false;
                }

                bytes[octetIndex] = parsed;
            }

            address = new IPAddress(bytes);
            return true;
        }

        private static bool IsUnsupportedPeerAddress(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork
                || address.Equals(IPAddress.Any)
                || IPAddress.IsLoopback(address))
            {
                return true;
            }

            byte firstOctet = address.GetAddressBytes()[0];
            return firstOctet == 0 || firstOctet >= 224;
        }
    }
}
