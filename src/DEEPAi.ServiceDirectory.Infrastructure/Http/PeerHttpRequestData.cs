using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    public sealed class PeerHttpRequestData
    {
        private readonly IReadOnlyDictionary<string,
            IReadOnlyList<string>> _headers;
        private readonly IPEndPoint _localEndpoint;
        private readonly IPEndPoint _remoteEndpoint;

        public PeerHttpRequestData(
            string method,
            string absolutePath,
            string rawQuery,
            string contentType,
            string contentEncodingHeaderValue,
            long declaredContentLength,
            Stream bodyStream,
            IPEndPoint localEndpoint,
            IPEndPoint remoteEndpoint,
            IDictionary<string, IReadOnlyList<string>> headers)
        {
            if (declaredContentLength < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(declaredContentLength));
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
            _headers = CopyHeaders(headers);
        }

        public string Method { get; }

        public string AbsolutePath { get; }

        public string RawQuery { get; }

        public string ContentType { get; }

        public string ContentEncodingHeaderValue { get; }

        public long DeclaredContentLength { get; }

        public IPEndPoint LocalEndpoint => CopyEndpoint(_localEndpoint);

        public IPEndPoint RemoteEndpoint => CopyEndpoint(_remoteEndpoint);

        internal Stream BodyStream { get; }

        internal IReadOnlyList<string> GetHeaderValues(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(
                    "A Peer header name is required.",
                    nameof(name));
            }

            IReadOnlyList<string> values;
            return _headers.TryGetValue(name, out values)
                ? values
                : EmptyValues;
        }

        private static readonly IReadOnlyList<string> EmptyValues =
            Array.AsReadOnly(new string[0]);

        private static IReadOnlyDictionary<string, IReadOnlyList<string>>
            CopyHeaders(
            IDictionary<string, IReadOnlyList<string>> headers)
        {
            var copy = new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase);
            if (headers != null)
            {
                foreach (KeyValuePair<string, IReadOnlyList<string>> entry
                    in headers)
                {
                    if (string.IsNullOrEmpty(entry.Key))
                    {
                        throw new ArgumentException(
                            "Peer request headers cannot contain an empty name.",
                            nameof(headers));
                    }

                    IReadOnlyList<string> values = entry.Value;
                    var valueCopy = new string[
                        values == null ? 0 : values.Count];
                    if (values != null)
                    {
                        for (int index = 0;
                            index < values.Count;
                            index++)
                        {
                            valueCopy[index] = values[index];
                        }
                    }

                    copy.Add(
                        entry.Key,
                        Array.AsReadOnly(valueCopy));
                }
            }

            return new ReadOnlyDictionary<string, IReadOnlyList<string>>(
                copy);
        }

        private static IPEndPoint CopyEndpoint(IPEndPoint endpoint)
        {
            if (endpoint == null || endpoint.Address == null)
            {
                return null;
            }

            IPAddress address = endpoint.Address;
            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return new IPEndPoint(new IPAddress(bytes), endpoint.Port);
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return new IPEndPoint(
                    new IPAddress(bytes, address.ScopeId),
                    endpoint.Port);
            }

            throw new ArgumentException(
                "Only IPv4 and IPv6 endpoints are supported.",
                nameof(endpoint));
        }
    }

    public sealed class PeerHttpHandlerRequest
    {
        private readonly byte[] _body;
        private readonly PeerHttpRequestData _transport;

        internal PeerHttpHandlerRequest(
            PeerHttpRequestData transport,
            byte[] body,
            Guid requestId,
            DateTimeOffset receivedAtUtc)
        {
            if (transport == null)
            {
                throw new ArgumentNullException(nameof(transport));
            }

            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (requestId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The Peer request ID must not be empty.",
                    nameof(requestId));
            }

            _transport = transport;
            _body = (byte[])body.Clone();
            RequestId = requestId;
            ReceivedAtUtc = receivedAtUtc.ToUniversalTime();
        }

        public string Method => _transport.Method;

        public string AbsolutePath => _transport.AbsolutePath;

        public string RawQuery => _transport.RawQuery;

        public string ContentType => _transport.ContentType;

        public IPEndPoint RemoteEndpoint => _transport.RemoteEndpoint;

        public Guid RequestId { get; }

        public DateTimeOffset ReceivedAtUtc { get; }

        public byte[] GetBody()
        {
            return (byte[])_body.Clone();
        }

        internal IReadOnlyList<string> GetHeaderValues(string name)
        {
            return _transport.GetHeaderValues(name);
        }
    }

    public interface IPeerHttpRequestHandler
    {
        PeerHttpResponseData Process(PeerHttpHandlerRequest request);
    }
}
