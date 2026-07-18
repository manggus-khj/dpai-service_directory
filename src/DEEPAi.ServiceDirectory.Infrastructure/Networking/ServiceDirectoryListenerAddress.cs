using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DEEPAi.ServiceDirectory.Infrastructure.Networking
{
    public sealed class ServiceDirectoryListenerAddress
    {
        public const int Port = 21000;

        private readonly AddressFamily _addressFamily;
        private readonly byte[] _addressBytes;
        private readonly long _scopeId;

        private ServiceDirectoryListenerAddress(IPAddress address)
        {
            _addressFamily = address.AddressFamily;
            _addressBytes = address.GetAddressBytes();
            _scopeId = _addressFamily == AddressFamily.InterNetworkV6
                ? address.ScopeId
                : 0L;

            CanonicalAddress = address.ToString();
            HttpPrefix = string.Format(
                CultureInfo.InvariantCulture,
                _addressFamily == AddressFamily.InterNetworkV6
                    ? "http://[{0}]:{1}/"
                    : "http://{0}:{1}/",
                CanonicalAddress,
                Port);
        }

        public string CanonicalAddress { get; }

        public string HttpPrefix { get; }

        public static bool TryCreate(
            string rawAddress,
            out ServiceDirectoryListenerAddress listenerAddress)
        {
            listenerAddress = null;
            if (string.IsNullOrEmpty(rawAddress)
                || !StringComparer.Ordinal.Equals(rawAddress, rawAddress.Trim())
                || rawAddress.IndexOf('%') >= 0
                || rawAddress.IndexOf('[') >= 0
                || rawAddress.IndexOf(']') >= 0)
            {
                return false;
            }

            IPAddress parsedAddress;
            if (rawAddress.IndexOf(':') >= 0)
            {
                if (rawAddress.IndexOf('.') >= 0)
                {
                    int finalColon = rawAddress.LastIndexOf(':');
                    IPAddress embeddedIpv4;
                    if (finalColon < 0
                        || !TryParseCanonicalIpv4(
                            rawAddress.Substring(finalColon + 1),
                            out embeddedIpv4))
                    {
                        return false;
                    }
                }

                if (!IPAddress.TryParse(rawAddress, out parsedAddress)
                    || parsedAddress.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    return false;
                }
            }
            else if (!TryParseCanonicalIpv4(rawAddress, out parsedAddress))
            {
                return false;
            }

            if (IsUnsupportedListenerAddress(parsedAddress))
            {
                return false;
            }

            listenerAddress = new ServiceDirectoryListenerAddress(parsedAddress);
            return true;
        }

        internal bool Matches(IPEndPoint endpoint)
        {
            if (endpoint == null
                || endpoint.Port != Port
                || endpoint.Address == null
                || endpoint.Address.AddressFamily != _addressFamily)
            {
                return false;
            }

            if (_addressFamily == AddressFamily.InterNetworkV6
                && endpoint.Address.ScopeId != _scopeId)
            {
                return false;
            }

            byte[] candidateBytes = endpoint.Address.GetAddressBytes();
            if (candidateBytes.Length != _addressBytes.Length)
            {
                return false;
            }

            for (int index = 0; index < _addressBytes.Length; index++)
            {
                if (candidateBytes[index] != _addressBytes[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryParseCanonicalIpv4(
            string value,
            out IPAddress address)
        {
            address = null;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            string[] octets = value.Split('.');
            if (octets.Length != 4)
            {
                return false;
            }

            var addressBytes = new byte[4];
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

                byte parsedOctet;
                if (!byte.TryParse(
                    octet,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsedOctet))
                {
                    return false;
                }

                addressBytes[octetIndex] = parsedOctet;
            }

            address = new IPAddress(addressBytes);
            return true;
        }

        private static bool IsUnsupportedListenerAddress(IPAddress address)
        {
            if (address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.IPv6Any)
                || IPAddress.IsLoopback(address))
            {
                return true;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6
                && (address.IsIPv6LinkLocal
                    || address.IsIPv6Multicast
                    || address.IsIPv4MappedToIPv6))
            {
                return true;
            }

            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            byte firstOctet = address.GetAddressBytes()[0];
            return address.Equals(IPAddress.Any)
                || IPAddress.IsLoopback(address)
                || firstOctet == 0
                || firstOctet >= 224;
        }
    }
}
