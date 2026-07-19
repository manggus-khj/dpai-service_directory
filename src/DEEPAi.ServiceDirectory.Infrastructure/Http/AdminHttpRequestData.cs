using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    public sealed class AdminHttpRequestData
    {
        private readonly IPEndPoint _localEndpoint;
        private readonly IPEndPoint _remoteEndpoint;

        public AdminHttpRequestData(
            string method,
            string absolutePath,
            string rawQuery,
            string contentType,
            string contentEncodingHeaderValue,
            long declaredContentLength,
            Stream bodyStream,
            IPEndPoint localEndpoint,
            IPEndPoint remoteEndpoint,
            IPrincipal principal)
        {
            if (declaredContentLength < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(declaredContentLength),
                    "The declared content length must be -1 or non-negative.");
            }

            Method = method;
            AbsolutePath = absolutePath;
            RawQuery = rawQuery;
            ContentType = contentType;
            ContentEncodingHeaderValue = contentEncodingHeaderValue;
            DeclaredContentLength = declaredContentLength;
            BodyStream = bodyStream;
            _localEndpoint = CopyEndpoint(localEndpoint);
            _remoteEndpoint = CopyEndpoint(remoteEndpoint);
            Principal = principal;
        }

        public string Method { get; }

        public string AbsolutePath { get; }

        public string RawQuery { get; }

        public string ContentType { get; }

        // The raw HTTP Content-Encoding header. It is intentionally distinct
        // from the text encoding selected by the Content-Type charset.
        public string ContentEncodingHeaderValue { get; }

        public long DeclaredContentLength { get; }

        public IPEndPoint LocalEndpoint => CopyEndpoint(_localEndpoint);

        public IPEndPoint RemoteEndpoint => CopyEndpoint(_remoteEndpoint);

        public IPrincipal Principal { get; }

        internal Stream BodyStream { get; }

        private static IPEndPoint CopyEndpoint(IPEndPoint endpoint)
        {
            if (endpoint == null || endpoint.Address == null)
            {
                return null;
            }

            return new IPEndPoint(
                CopyAddress(endpoint.Address),
                endpoint.Port);
        }

        private static IPAddress CopyAddress(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return new IPAddress(bytes);
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return new IPAddress(bytes, address.ScopeId);
            }

            throw new ArgumentException(
                "Only IPv4 and IPv6 endpoints are supported.",
                nameof(address));
        }
    }
}
