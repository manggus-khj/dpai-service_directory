using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    public sealed class PeerHttpResponseData
    {
        private static readonly HashSet<string> AllowedHeaderNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "X-DPAI-Instance-Id",
                "X-DPAI-Key-Epoch",
                "X-DPAI-Session-Id",
                "X-DPAI-Timestamp",
                "X-DPAI-Nonce",
                "X-DPAI-Signature",
                "X-DPAI-Pairing-MAC"
            };

        private readonly byte[] _body;
        private readonly IReadOnlyDictionary<string, string> _headers;

        private PeerHttpResponseData(
            int statusCode,
            string contentType,
            byte[] body,
            IDictionary<string, string> headers,
            int? retryAfterSeconds)
        {
            if (statusCode < 100 || statusCode > 599)
            {
                throw new ArgumentOutOfRangeException(nameof(statusCode));
            }

            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if ((body.Length == 0) != (contentType == null))
            {
                throw new ArgumentException(
                    "A Peer response declares a content type exactly when it has a body.",
                    nameof(contentType));
            }

            if (body.Length != 0
                && !StringComparer.Ordinal.Equals(
                    contentType,
                    PeerSyncContract.XmlContentType))
            {
                throw new ArgumentException(
                    "Peer XML responses must use the fixed contract content type.",
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

        public int StatusCode { get; }

        public bool HasBody => _body.Length != 0;

        public int ContentLength => _body.Length;

        public string ContentType { get; }

        public int? RetryAfterSeconds { get; }

        public byte[] GetBody()
        {
            return (byte[])_body.Clone();
        }

        internal IReadOnlyDictionary<string, string> Headers => _headers;

        internal static PeerHttpResponseData Xml(
            int statusCode,
            byte[] body,
            IDictionary<string, string> headers,
            int? retryAfterSeconds = null)
        {
            if (body == null || body.Length == 0)
            {
                throw new ArgumentException(
                    "A Peer XML response body must not be empty.",
                    nameof(body));
            }

            return new PeerHttpResponseData(
                statusCode,
                PeerSyncContract.XmlContentType,
                body,
                headers,
                retryAfterSeconds);
        }

        internal static PeerHttpResponseData Bodyless(
            int statusCode,
            int? retryAfterSeconds = null)
        {
            if (statusCode != 400
                && statusCode != 401
                && statusCode != 403
                && statusCode != 404
                && statusCode != 413
                && statusCode != 415
                && statusCode != 429
                && statusCode != 500)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(statusCode),
                    "The Peer boundary has no bodyless contract for this status.");
            }

            return new PeerHttpResponseData(
                statusCode,
                null,
                new byte[0],
                null,
                retryAfterSeconds);
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
                    if (!AllowedHeaderNames.Contains(header.Key)
                        || string.IsNullOrEmpty(header.Value)
                        || header.Value.IndexOf('\r') >= 0
                        || header.Value.IndexOf('\n') >= 0)
                    {
                        throw new ArgumentException(
                            "A Peer response contains an unsupported or invalid header.",
                            nameof(headers));
                    }

                    copy.Add(header.Key, header.Value);
                }
            }

            return new ReadOnlyDictionary<string, string>(copy);
        }
    }
}
