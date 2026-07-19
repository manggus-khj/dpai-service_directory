using System;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    public sealed class ExternalHttpResponseData
    {
        private readonly byte[] _body;

        private ExternalHttpResponseData(
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

        public string ContentType => HasBody
            ? ExternalApiContract.XmlContentType
            : null;

        public int? RetryAfterSeconds { get; }

        public byte[] GetBody()
        {
            return (byte[])_body.Clone();
        }

        internal static ExternalHttpResponseData FromCore(
            ExternalApiHandlerResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            return new ExternalHttpResponseData(
                response.StatusCode,
                response.GetBody(),
                response.RetryAfterSeconds);
        }

        internal static ExternalHttpResponseData XmlError(
            int statusCode,
            ExternalResponseCode responseCode,
            int? retryAfterSeconds = null)
        {
            ExternalResponse response = ExternalResponse.CreateError(
                responseCode);
            byte[] body = ExternalXmlCodec.SerializeErrorResponse(response);
            return new ExternalHttpResponseData(
                statusCode,
                body,
                retryAfterSeconds);
        }

        internal static ExternalHttpResponseData Bodyless(int statusCode)
        {
            if (statusCode != 403
                && statusCode != 404
                && statusCode != 413
                && statusCode != 415)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(statusCode),
                    "This adapter defines bodyless responses only for 403, 404, 413, and 415.");
            }

            return new ExternalHttpResponseData(
                statusCode,
                new byte[0],
                null);
        }
    }
}
