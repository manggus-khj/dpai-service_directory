using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal sealed class HttpTransportResponseData
    {
        private readonly byte[] _body;
        private readonly IReadOnlyDictionary<string, string> _headers;

        private HttpTransportResponseData(
            int statusCode,
            string contentType,
            byte[] body,
            int? retryAfterSeconds,
            IDictionary<string, string> headers)
        {
            if (statusCode < 100 || statusCode > 599)
            {
                throw new ArgumentOutOfRangeException(nameof(statusCode));
            }

            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (body.Length == 0 && contentType != null)
            {
                throw new ArgumentException(
                    "A bodyless response must not declare a content type.",
                    nameof(contentType));
            }

            if (body.Length != 0 && string.IsNullOrEmpty(contentType))
            {
                throw new ArgumentException(
                    "A response body requires a content type.",
                    nameof(contentType));
            }

            if (retryAfterSeconds.HasValue
                && (statusCode != 429 || retryAfterSeconds.Value < 1))
            {
                throw new ArgumentException(
                    "Retry-After is valid only for a time-based 429 response.",
                    nameof(retryAfterSeconds));
            }

            StatusCode = statusCode;
            ContentType = contentType;
            RetryAfterSeconds = retryAfterSeconds;
            _body = (byte[])body.Clone();
            _headers = CopyHeaders(headers);
        }

        internal int StatusCode { get; }

        internal string ContentType { get; }

        internal int ContentLength => _body.Length;

        internal int? RetryAfterSeconds { get; }

        internal IReadOnlyDictionary<string, string> Headers => _headers;

        internal byte[] GetBody()
        {
            return (byte[])_body.Clone();
        }

        internal static HttpTransportResponseData FromExternal(
            ExternalHttpResponseData response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            return new HttpTransportResponseData(
                response.StatusCode,
                response.ContentType,
                response.GetBody(),
                response.RetryAfterSeconds,
                null);
        }

        internal static HttpTransportResponseData FromAdmin(
            AdminHttpResponseData response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            return new HttpTransportResponseData(
                response.StatusCode,
                response.ContentType,
                response.GetBody(),
                response.RetryAfterSeconds,
                null);
        }

        internal static HttpTransportResponseData FromPeer(
            PeerHttpResponseData response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            var headers = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> header
                in response.Headers)
            {
                headers.Add(header.Key, header.Value);
            }

            return new HttpTransportResponseData(
                response.StatusCode,
                response.ContentType,
                response.GetBody(),
                response.RetryAfterSeconds,
                headers);
        }

        internal static HttpTransportResponseData Bodyless(int statusCode)
        {
            if (statusCode != 404)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(statusCode),
                    "The host creates only bodyless 404 responses.");
            }

            return new HttpTransportResponseData(
                statusCode,
                null,
                new byte[0],
                null,
                null);
        }

        private static IReadOnlyDictionary<string, string> CopyHeaders(
            IDictionary<string, string> headers)
        {
            var copy = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            if (headers != null)
            {
                foreach (KeyValuePair<string, string> header in headers)
                {
                    if (string.IsNullOrEmpty(header.Key)
                        || string.IsNullOrEmpty(header.Value)
                        || header.Value.IndexOf('\r') >= 0
                        || header.Value.IndexOf('\n') >= 0)
                    {
                        throw new ArgumentException(
                            "A transport response contains an invalid header.",
                            nameof(headers));
                    }

                    copy.Add(header.Key, header.Value);
                }
            }

            return new ReadOnlyDictionary<string, string>(copy);
        }
    }
}
