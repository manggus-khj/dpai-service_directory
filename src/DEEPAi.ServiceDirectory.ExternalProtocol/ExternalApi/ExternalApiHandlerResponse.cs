using System;

namespace DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi
{
    public sealed class ExternalApiHandlerResponse
    {
        private readonly byte[] _body;

        private ExternalApiHandlerResponse(
            int statusCode,
            byte[] body,
            string contentType,
            int? retryAfterSeconds,
            bool requiresInvalidApiKeyAudit)
        {
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

            if (requiresInvalidApiKeyAudit && statusCode != 401)
            {
                throw new ArgumentException(
                    "Only an invalid API key response can request the fixed authentication audit event.",
                    nameof(requiresInvalidApiKeyAudit));
            }

            StatusCode = statusCode;
            _body = (byte[])body.Clone();
            if ((_body.Length == 0) != (contentType == null))
            {
                throw new ArgumentException(
                    "Content-Type must exist exactly when the response has a body.",
                    nameof(contentType));
            }

            ContentType = contentType;
            RetryAfterSeconds = retryAfterSeconds;
            RequiresInvalidApiKeyAudit = requiresInvalidApiKeyAudit;
        }

        public int StatusCode { get; }

        public bool HasBody => _body.Length != 0;

        public string ContentType { get; }

        public int? RetryAfterSeconds { get; }

        // The host uses this boolean only to emit the fixed 4101 audit event.
        // It intentionally carries no rejected key, ProductCode, or reason.
        public bool RequiresInvalidApiKeyAudit { get; }

        public byte[] GetBody()
        {
            return (byte[])_body.Clone();
        }

        internal static ExternalApiHandlerResponse Xml(
            int statusCode,
            byte[] body,
            int? retryAfterSeconds = null,
            bool requiresInvalidApiKeyAudit = false)
        {
            if (body == null || body.Length == 0)
            {
                throw new ArgumentException(
                    "An XML handler response requires a non-empty body.",
                    nameof(body));
            }

            return new ExternalApiHandlerResponse(
                statusCode,
                body,
                ExternalApiContract.XmlContentType,
                retryAfterSeconds,
                requiresInvalidApiKeyAudit);
        }

        internal static ExternalApiHandlerResponse Binary(
            int statusCode,
            byte[] body,
            string contentType)
        {
            if (body == null || body.Length == 0)
            {
                throw new ArgumentException(
                    "A binary handler response requires a non-empty body.",
                    nameof(body));
            }

            if (string.IsNullOrWhiteSpace(contentType))
            {
                throw new ArgumentException(
                    "A binary handler response requires a content type.",
                    nameof(contentType));
            }

            return new ExternalApiHandlerResponse(
                statusCode,
                body,
                contentType,
                null,
                false);
        }

        internal static ExternalApiHandlerResponse UndefinedRoute()
        {
            return new ExternalApiHandlerResponse(
                404,
                new byte[0],
                null,
                null,
                false);
        }

        internal static ExternalApiHandlerResponse Bodyless(int statusCode)
        {
            if (statusCode != 401)
            {
                throw new ArgumentOutOfRangeException(nameof(statusCode));
            }

            return new ExternalApiHandlerResponse(
                statusCode,
                new byte[0],
                null,
                null,
                false);
        }
    }
}
