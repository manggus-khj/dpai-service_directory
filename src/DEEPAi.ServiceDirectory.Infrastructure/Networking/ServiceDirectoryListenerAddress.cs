using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DEEPAi.ServiceDirectory.Infrastructure.Networking
{
    public sealed class ServiceDirectoryListenerAddress
    {
        public const int Port = 21000;

        private readonly byte[] _addressBytes;

        private ServiceDirectoryListenerAddress(IPAddress address)
        {
            _addressBytes = address.GetAddressBytes();

            CanonicalAddress = address.ToString();
            HttpsPrefix = string.Format(
                CultureInfo.InvariantCulture,
                "https://{0}:{1}/",
                CanonicalAddress,
                Port);
        }

        public string CanonicalAddress { get; }

        public string HttpsPrefix { get; }

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
            if (!TryParseCanonicalIpv4(rawAddress, out parsedAddress))
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
                || endpoint.Address.AddressFamily
                    != AddressFamily.InterNetwork)
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
            if (address.AddressFamily != AddressFamily.InterNetwork
                || address.Equals(IPAddress.Any)
                || IPAddress.IsLoopback(address))
            {
                return true;
            }

            byte firstOctet = address.GetAddressBytes()[0];
            return firstOctet == 0
                || firstOctet >= 224;
        }
    }
}
