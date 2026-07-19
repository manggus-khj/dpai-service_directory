using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    public sealed class ExternalHttpRequestData
    {
        private readonly IReadOnlyList<string> _apiKeyHeaderValues;
        private readonly IPEndPoint _localEndpoint;
        private readonly IPEndPoint _remoteEndpoint;

        public ExternalHttpRequestData(
            string method,
            string absolutePath,
            string rawQuery,
            IEnumerable<string> apiKeyHeaderValues,
            string contentType,
            string contentEncodingHeaderValue,
            long declaredContentLength,
            Stream bodyStream,
            IPEndPoint localEndpoint,
            IPEndPoint remoteEndpoint)
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
            _apiKeyHeaderValues = CopyHeaderValues(apiKeyHeaderValues);
            _localEndpoint = CopyEndpoint(localEndpoint);
            _remoteEndpoint = CopyEndpoint(remoteEndpoint);
        }

        public string Method { get; }

        // The host must copy the exact raw path from RawUrl's request-target,
        // before the first '?'. It must not pass Url.AbsolutePath or any
        // decoded, canonicalized, or normalized path here.
        public string AbsolutePath { get; }

        // The host must copy the raw ASCII query from the first '?' onward.
        // The optional leading '?' is part of this transport handoff value.
        public string RawQuery { get; }

        public string ContentType { get; }

        // This is the raw HTTP Content-Encoding header value. It is not
        // HttpListenerRequest.ContentEncoding, which describes the decoded
        // character encoding inferred from Content-Type.
        public string ContentEncodingHeaderValue { get; }

        public long DeclaredContentLength { get; }

        public IPEndPoint LocalEndpoint => CopyEndpoint(_localEndpoint);

        public IPEndPoint RemoteEndpoint => CopyEndpoint(_remoteEndpoint);

        internal IReadOnlyList<string> ApiKeyHeaderValues =>
            _apiKeyHeaderValues;

        internal Stream BodyStream { get; }

        private static IReadOnlyList<string> CopyHeaderValues(
            IEnumerable<string> values)
        {
            var copy = new List<string>();
            if (values != null)
            {
                foreach (string value in values)
                {
                    copy.Add(value);
                }
            }

            return new ReadOnlyCollection<string>(copy);
        }

        private static IPEndPoint CopyEndpoint(IPEndPoint endpoint)
        {
            if (endpoint == null || endpoint.Address == null)
            {
                return null;
            }

            IPAddress address = CopyAddress(endpoint.Address);
            return new IPEndPoint(address, endpoint.Port);
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
