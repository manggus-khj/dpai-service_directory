using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;

namespace DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi
{
    public sealed class ExternalApiQueryParameter
    {
        public ExternalApiQueryParameter(string name, string value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }

        public string Value { get; }
    }

    // This model is constructed only after the HTTP host has passed the
    // configured local-endpoint guard, rejected unsupported Content-Type,
    // mapped malformed query percent-encoding to BAD_REQUEST, preserved
    // individually decoded query values (including duplicates), and enforced
    // the raw body limit. The endpoint, media-type, and body-size failures
    // remain bodyless 403, 415, and 413 responses and are deliberately not
    // represented here.
    public sealed class ExternalApiHandlerRequest
    {
        private readonly byte[] _body;
        private readonly IPAddress _remoteAddress;
        private readonly IReadOnlyList<ExternalApiQueryParameter>
            _queryParameters;
        private readonly IReadOnlyList<string> _apiKeyHeaderValues;

        public ExternalApiHandlerRequest(
            string method,
            string absolutePath,
            IEnumerable<ExternalApiQueryParameter> queryParameters,
            IEnumerable<string> apiKeyHeaderValues,
            byte[] body,
            IPAddress remoteAddress)
        {
            if (method == null)
            {
                throw new ArgumentNullException(nameof(method));
            }

            if (absolutePath == null)
            {
                throw new ArgumentNullException(nameof(absolutePath));
            }

            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (body.Length > ExternalApiContract.MaximumBodyBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(body),
                    "The HTTP host must reject an oversized raw body before constructing a handler request.");
            }

            Method = method;
            AbsolutePath = absolutePath;
            _queryParameters = CopyQueryParameters(queryParameters);
            _apiKeyHeaderValues = CopyHeaderValues(apiKeyHeaderValues);
            _body = (byte[])body.Clone();
            _remoteAddress = CopyRemoteAddress(remoteAddress);
        }

        public string Method { get; }

        public string AbsolutePath { get; }

        public IPAddress RemoteAddress => CopyRemoteAddress(_remoteAddress);

        internal IReadOnlyList<ExternalApiQueryParameter> QueryParameters =>
            _queryParameters;

        internal IReadOnlyList<string> ApiKeyHeaderValues =>
            _apiKeyHeaderValues;

        internal int BodyLength => _body.Length;

        internal byte[] CopyBody()
        {
            return (byte[])_body.Clone();
        }

        private static IReadOnlyList<ExternalApiQueryParameter>
            CopyQueryParameters(
                IEnumerable<ExternalApiQueryParameter> queryParameters)
        {
            var copy = new List<ExternalApiQueryParameter>();
            if (queryParameters != null)
            {
                foreach (ExternalApiQueryParameter parameter in queryParameters)
                {
                    if (parameter == null)
                    {
                        throw new ArgumentException(
                            "Query parameters cannot contain null entries.",
                            nameof(queryParameters));
                    }

                    copy.Add(parameter);
                }
            }

            return new ReadOnlyCollection<ExternalApiQueryParameter>(copy);
        }

        private static IReadOnlyList<string> CopyHeaderValues(
            IEnumerable<string> apiKeyHeaderValues)
        {
            var copy = new List<string>();
            if (apiKeyHeaderValues != null)
            {
                foreach (string value in apiKeyHeaderValues)
                {
                    copy.Add(value);
                }
            }

            return new ReadOnlyCollection<string>(copy);
        }

        private static IPAddress CopyRemoteAddress(IPAddress remoteAddress)
        {
            if (remoteAddress == null)
            {
                throw new ArgumentNullException(nameof(remoteAddress));
            }

            if (remoteAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                return new IPAddress(remoteAddress.GetAddressBytes());
            }

            if (remoteAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return new IPAddress(
                    remoteAddress.GetAddressBytes(),
                    remoteAddress.ScopeId);
            }

            throw new ArgumentException(
                "The remote address must be an IPv4 or IPv6 address.",
                nameof(remoteAddress));
        }
    }
}
