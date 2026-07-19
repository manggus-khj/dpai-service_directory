using System;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal sealed class HttpTransportResponseData
    {
        private readonly byte[] _body;

        private HttpTransportResponseData(
            int statusCode,
            string contentType,
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
        }

        internal int StatusCode { get; }

        internal string ContentType { get; }

        internal int ContentLength => _body.Length;

        internal int? RetryAfterSeconds { get; }

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
                response.RetryAfterSeconds);
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
                response.RetryAfterSeconds);
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
                null);
        }
    }
}
