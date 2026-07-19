using System;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    public sealed class AdminHttpResponseData
    {
        private const string XmlContentType =
            "application/xml; charset=utf-8";

        private readonly byte[] _body;

        private AdminHttpResponseData(
            int statusCode,
            byte[] body,
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

            if (retryAfterSeconds.HasValue
                && (statusCode != 429 || retryAfterSeconds.Value < 1))
            {
                throw new ArgumentException(
                    "Retry-After is valid only for a time-based 429 response.",
                    nameof(retryAfterSeconds));
            }

            StatusCode = statusCode;
            _body = (byte[])body.Clone();
            RetryAfterSeconds = retryAfterSeconds;
        }

        public int StatusCode { get; }

        public bool HasBody => _body.Length != 0;

        public int ContentLength => _body.Length;

        public string ContentType => HasBody ? XmlContentType : null;

        public int? RetryAfterSeconds { get; }

        public byte[] GetBody()
        {
            return (byte[])_body.Clone();
        }

        internal static AdminHttpResponseData Xml(
            int statusCode,
            byte[] body,
            int? retryAfterSeconds = null)
        {
            if (body == null || body.Length == 0)
            {
                throw new ArgumentException(
                    "An Admin XML response body must not be empty.",
                    nameof(body));
            }

            return new AdminHttpResponseData(
                statusCode,
                body,
                retryAfterSeconds);
        }

        internal static AdminHttpResponseData Error(
            int statusCode,
            AdminServerErrorCode code,
            int? retryAfterSeconds = null)
        {
            byte[] body = AdminServerResponseXmlCodec.SerializeErrorResponse(
                new AdminServerErrorResponse(code));
            return Xml(statusCode, body, retryAfterSeconds);
        }

        internal static AdminHttpResponseData Bodyless(int statusCode)
        {
            if (statusCode != 401
                && statusCode != 403
                && statusCode != 404
                && statusCode != 413
                && statusCode != 415)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(statusCode),
                    "The Admin boundary has no bodyless contract for this status.");
            }

            return new AdminHttpResponseData(
                statusCode,
                new byte[0],
                null);
        }
    }
}
