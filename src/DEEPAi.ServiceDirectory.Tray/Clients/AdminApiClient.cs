using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.Tray.Clients
{
    public sealed class AdminApiException : Exception
    {
        internal AdminApiException(
            string message,
            HttpStatusCode? statusCode,
            int? logicalCode,
            TimeSpan? retryAfter,
            Exception innerException = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            LogicalCode = logicalCode;
            RetryAfter = retryAfter;
        }

        public HttpStatusCode? StatusCode { get; }

        public int? LogicalCode { get; }

        public TimeSpan? RetryAfter { get; }

        public bool IsConflict => LogicalCode == 1002;
    }

    public sealed class AdminApiClient : IDisposable
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private readonly HttpClient _client;
        private bool _disposed;

        public AdminApiClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                PreAuthenticate = true,
                UseCookies = false,
                UseDefaultCredentials = true,
                UseProxy = false
            };

            _client = new HttpClient(handler, true)
            {
                BaseAddress = AdminApiContract.BaseAddress,
                Timeout = Timeout.InfiniteTimeSpan
            };
            _client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/xml"));
            _client.DefaultRequestHeaders.AcceptCharset.Add(
                new StringWithQualityHeaderValue("utf-8"));
        }

        public Task<AdminPage<AdminServiceItem>> GetServicesAsync(
            string cursor,
            CancellationToken cancellationToken)
        {
            string path = "admin/services?includeDeleted=false&pageSize="
                + AdminApiContract.PageSize.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(cursor))
            {
                path += "&cursor=" + Uri.EscapeDataString(cursor);
            }

            return SendAsync(
                HttpMethod.Get,
                path,
                null,
                AdminXmlCodec.ParseServicesResponse,
                cancellationToken);
        }

        public Task<AdminServerRegistrationModeResponse>
            GetRegistrationModeAsync(
            CancellationToken cancellationToken)
        {
            return SendAsync(
                HttpMethod.Get,
                AdminApiContract.RegistrationModePath.TrimStart('/'),
                null,
                AdminRegistrationModeXmlCodec.ParseRegistrationModeResponse,
                cancellationToken);
        }

        public Task<AdminServerRegistrationModeResponse>
            OpenRegistrationModeAsync(
            CancellationToken cancellationToken)
        {
            return SendAsync(
                HttpMethod.Post,
                AdminApiContract.OpenRegistrationModePath.TrimStart('/'),
                null,
                AdminRegistrationModeXmlCodec.ParseRegistrationModeResponse,
                cancellationToken);
        }

        public Task<AdminServerRegistrationModeResponse>
            CloseRegistrationModeAsync(
            CancellationToken cancellationToken)
        {
            return SendAsync(
                HttpMethod.Post,
                AdminApiContract.CloseRegistrationModePath.TrimStart('/'),
                null,
                AdminRegistrationModeXmlCodec.ParseRegistrationModeResponse,
                cancellationToken);
        }

        public Task<AdminSyncStatus> GetSyncStatusAsync(
            CancellationToken cancellationToken)
        {
            return SendAsync(
                HttpMethod.Get,
                "admin/sync",
                null,
                AdminXmlCodec.ParseSyncResponse,
                cancellationToken);
        }

        public Task<AdminLoggingSettings> GetLoggingSettingsAsync(
            CancellationToken cancellationToken)
        {
            return SendAsync(
                HttpMethod.Get,
                "admin/settings/logging",
                null,
                AdminXmlCodec.ParseLoggingResponse,
                cancellationToken);
        }

        public Task<AdminServerCaStatusResponse> GetCaStatusAsync(
            CancellationToken cancellationToken)
        {
            return SendAsync(
                HttpMethod.Get,
                "admin/ca/status",
                null,
                AdminXmlCodec.ParseCaStatusResponse,
                cancellationToken);
        }

        public async Task<AdminServerCaBackupResponse> CreateCaBackupAsync(
            string password,
            CancellationToken cancellationToken)
        {
            byte[] body = AdminXmlCodec.SerializeCreateCaBackup(password);
            try
            {
                return await SendAsync(
                    HttpMethod.Post,
                    "admin/ca/backup",
                    body,
                    AdminXmlCodec.ParseCaBackupResponse,
                    cancellationToken);
            }
            finally
            {
                Array.Clear(body, 0, body.Length);
            }
        }

        public Task<AdminServerCertificatesResponse> GetCertificatesAsync(
            string cursor,
            CancellationToken cancellationToken)
        {
            string path = "admin/certificates?pageSize="
                + AdminApiContract.PageSize.ToString(
                    CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(cursor))
            {
                path += "&cursor=" + Uri.EscapeDataString(cursor);
            }

            return SendAsync(
                HttpMethod.Get,
                path,
                null,
                AdminXmlCodec.ParseCertificatesResponse,
                cancellationToken);
        }

        public Task<AdminServerCertificateRevocationResponse>
            RevokeCertificateAsync(
                string serialNumber,
                AdminCertificateRevocationReason reason,
                CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                throw new ArgumentException(
                    "Certificate serial number is required.",
                    nameof(serialNumber));
            }

            return SendAsync(
                HttpMethod.Post,
                "admin/certificates/"
                    + Uri.EscapeDataString(serialNumber)
                    + "/revoke",
                AdminXmlCodec.SerializeRevokeCertificate(reason),
                AdminXmlCodec.ParseCertificateRevocationResponse,
                cancellationToken);
        }

        public Task DeleteServiceAsync(
            string productCode,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(productCode))
            {
                throw new ArgumentException("Product code is required.", nameof(productCode));
            }

            return SendUnitAsync(
                HttpMethod.Delete,
                "admin/services/" + Uri.EscapeDataString(productCode),
                null,
                cancellationToken);
        }

        public Task EnableSyncAsync(
            string peerEndpoint,
            bool rePair,
            CancellationToken cancellationToken)
        {
            return SendUnitAsync(
                HttpMethod.Post,
                "admin/sync/enable",
                AdminXmlCodec.SerializeEnableSync(peerEndpoint, rePair),
                cancellationToken);
        }

        public Task ConfirmPairingAsync(
            Guid pairingId,
            CancellationToken cancellationToken)
        {
            return SendUnitAsync(
                HttpMethod.Post,
                "admin/sync/pairing/confirm",
                AdminXmlCodec.SerializePairingConfirmation(pairingId),
                cancellationToken);
        }

        public Task CancelPairingAsync(
            Guid pairingId,
            CancellationToken cancellationToken)
        {
            return SendUnitAsync(
                HttpMethod.Post,
                "admin/sync/pairing/cancel",
                AdminXmlCodec.SerializePairingCancellation(pairingId),
                cancellationToken);
        }

        public Task<AdminSyncDisableResult> DisableSyncAsync(
            bool forgetPeer,
            CancellationToken cancellationToken)
        {
            return SendAsync(
                HttpMethod.Post,
                "admin/sync/disable",
                AdminXmlCodec.SerializeDisableSync(forgetPeer),
                AdminXmlCodec.ParseSyncDisableResponse,
                cancellationToken);
        }

        public Task SyncNowAsync(CancellationToken cancellationToken)
        {
            return SendUnitAsync(
                HttpMethod.Post,
                "admin/sync/now",
                null,
                cancellationToken);
        }

        public Task<AdminLoggingSettings> UpdateLoggingSettingsAsync(
            int logRetentionDays,
            CancellationToken cancellationToken)
        {
            return SendAsync(
                HttpMethod.Put,
                "admin/settings/logging",
                AdminXmlCodec.SerializeLoggingSettings(logRetentionDays),
                AdminXmlCodec.ParseLoggingResponse,
                cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _client.Dispose();
        }

        private async Task SendUnitAsync(
            HttpMethod method,
            string path,
            byte[] body,
            CancellationToken cancellationToken)
        {
            await SendAsync(
                method,
                path,
                body,
                AdminXmlCodec.ParseUnitResponse,
                cancellationToken);
        }

        private async Task<T> SendAsync<T>(
            HttpMethod method,
            string path,
            byte[] body,
            Func<byte[], AdminResponse<T>> parser,
            CancellationToken cancellationToken)
            where T : class
        {
            ThrowIfDisposed();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken))
            {
                timeout.CancelAfter(RequestTimeout);
                try
                {
                    using (var request = new HttpRequestMessage(method, path))
                    {
                        request.Version = HttpVersion.Version11;
                        if (body != null)
                        {
                            request.Content = new ByteArrayContent(body);
                            request.Content.Headers.ContentType =
                                new MediaTypeHeaderValue("application/xml")
                                {
                                    CharSet = "utf-8"
                                };
                        }

                        using (HttpResponseMessage response = await _client.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeout.Token))
                        {
                            if (response.StatusCode == (HttpStatusCode)429)
                            {
                                TimeSpan? retryAfter = ReadRetryAfter(response.Headers.RetryAfter);
                                AdminApiException throttled =
                                    await ReadErrorEnvelopeAsync(
                                        response,
                                        timeout.Token,
                                        retryAfter);
                                if (throttled.LogicalCode != 1004)
                                {
                                    throw new AdminApiException(
                                        "메인 서비스의 제한 응답 형식이 올바르지 않습니다.",
                                        response.StatusCode,
                                        null,
                                        retryAfter,
                                        throttled);
                                }

                                throw throttled;
                            }

                            if (response.StatusCode != HttpStatusCode.OK)
                            {
                                if (HasRequiredErrorEnvelope(response.StatusCode))
                                {
                                    throw await ReadErrorEnvelopeAsync(
                                        response,
                                        timeout.Token);
                                }

                                throw CreateHttpFailure(response.StatusCode);
                            }

                            ValidateContentType(response.Content.Headers.ContentType);
                            byte[] responseBody = await ReadBoundedBodyAsync(
                                response.Content,
                                timeout.Token);
                            AdminResponse<T> parsed;
                            try
                            {
                                parsed = parser(responseBody);
                            }
                            catch (AdminProtocolException exception)
                            {
                                throw new AdminApiException(
                                    "메인 서비스의 응답 형식이 올바르지 않습니다.",
                                    response.StatusCode,
                                    null,
                                    null,
                                    exception);
                            }

                            if (!parsed.IsSuccess)
                            {
                                string message = string.IsNullOrWhiteSpace(parsed.Message)
                                    ? "관리 요청을 처리하지 못했습니다."
                                    : parsed.Message;
                                throw new AdminApiException(
                                    message,
                                    response.StatusCode,
                                    parsed.Code,
                                    null);
                            }

                            if (parsed.Payload == null)
                            {
                                throw new AdminApiException(
                                    "메인 서비스가 성공 응답 데이터를 반환하지 않았습니다.",
                                    response.StatusCode,
                                    null,
                                    null);
                            }

                            return parsed.Payload;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException exception)
                {
                    throw new AdminApiException(
                        "메인 서비스 응답 시간이 초과되었습니다.",
                        null,
                        null,
                        null,
                        exception);
                }
                catch (HttpRequestException exception)
                {
                    throw new AdminApiException(
                        "메인 서비스에 연결할 수 없습니다.",
                        null,
                        null,
                        null,
                        exception);
                }
            }
        }

        private static async Task<byte[]> ReadBoundedBodyAsync(
            HttpContent content,
            CancellationToken cancellationToken)
        {
            long? declaredLength = content.Headers.ContentLength;
            if (declaredLength.HasValue
                && declaredLength.Value > AdminApiContract.MaximumBodyBytes)
            {
                throw new AdminApiException(
                    "메인 서비스 응답이 허용 크기를 초과했습니다.",
                    HttpStatusCode.OK,
                    null,
                    null);
            }

            using (Stream input = await content.ReadAsStreamAsync())
            using (var output = new MemoryStream())
            {
                var buffer = new byte[4096];
                while (true)
                {
                    int read = await input.ReadAsync(
                        buffer,
                        0,
                        buffer.Length,
                        cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    if (output.Length + read > AdminApiContract.MaximumBodyBytes)
                    {
                        throw new AdminApiException(
                            "메인 서비스 응답이 허용 크기를 초과했습니다.",
                            HttpStatusCode.OK,
                            null,
                            null);
                    }

                    output.Write(buffer, 0, read);
                }

                if (declaredLength.HasValue && output.Length != declaredLength.Value)
                {
                    throw new AdminApiException(
                        "메인 서비스 응답 길이가 올바르지 않습니다.",
                        HttpStatusCode.OK,
                        null,
                        null);
                }

                return output.ToArray();
            }
        }

        private static async Task<AdminApiException> ReadErrorEnvelopeAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken,
            TimeSpan? retryAfter = null)
        {
            try
            {
                if (response.Content == null)
                {
                    return CreateHttpFailure(response.StatusCode);
                }

                byte[] responseBody = await ReadBoundedBodyAsync(
                    response.Content,
                    cancellationToken);
                if (responseBody.Length == 0)
                {
                    return CreateHttpFailure(response.StatusCode);
                }

                ValidateContentType(response.Content.Headers.ContentType);
                AdminResponse<AdminUnit> parsed =
                    AdminXmlCodec.ParseUnitResponse(responseBody);
                if (parsed.IsSuccess)
                {
                    return new AdminApiException(
                        "메인 서비스의 HTTP 상태와 XML 응답이 일치하지 않습니다.",
                        response.StatusCode,
                        null,
                        null);
                }

                string message = string.IsNullOrWhiteSpace(parsed.Message)
                    ? "관리 요청을 처리하지 못했습니다."
                    : parsed.Message;
                return new AdminApiException(
                    message,
                    response.StatusCode,
                    parsed.Code,
                    retryAfter);
            }
            catch (AdminApiException exception)
            {
                return exception;
            }
            catch (AdminProtocolException exception)
            {
                return new AdminApiException(
                    "메인 서비스의 오류 응답 형식이 올바르지 않습니다.",
                    response.StatusCode,
                    null,
                    null,
                    exception);
            }
        }

        private static bool HasRequiredErrorEnvelope(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.BadRequest
                || statusCode == HttpStatusCode.NotFound
                || statusCode == HttpStatusCode.Conflict
                || statusCode == HttpStatusCode.InternalServerError;
        }

        private static void ValidateContentType(MediaTypeHeaderValue contentType)
        {
            if (contentType == null
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    contentType.MediaType,
                    "application/xml")
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    contentType.CharSet,
                    "utf-8"))
            {
                throw new AdminApiException(
                    "메인 서비스 응답의 Content-Type이 올바르지 않습니다.",
                    HttpStatusCode.OK,
                    null,
                    null);
            }
        }

        private static TimeSpan? ReadRetryAfter(RetryConditionHeaderValue header)
        {
            if (header == null)
            {
                return null;
            }

            if (header.Delta.HasValue)
            {
                return header.Delta.Value < TimeSpan.Zero
                    ? TimeSpan.Zero
                    : header.Delta.Value;
            }

            if (header.Date.HasValue)
            {
                TimeSpan remaining = header.Date.Value - DateTimeOffset.Now;
                return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            }

            return null;
        }

        private static AdminApiException CreateHttpFailure(HttpStatusCode statusCode)
        {
            string message;
            switch (statusCode)
            {
                case HttpStatusCode.Unauthorized:
                    message = "Windows 인증에 실패했습니다.";
                    break;
                case HttpStatusCode.Forbidden:
                    message = "로컬 서비스 디렉토리 운영자 권한이 없습니다.";
                    break;
                case HttpStatusCode.NotFound:
                    message = "메인 서비스가 요청한 관리 API를 제공하지 않습니다.";
                    break;
                case HttpStatusCode.RequestEntityTooLarge:
                    message = "관리 요청 또는 응답 크기 제한을 초과했습니다.";
                    break;
                default:
                    message = "메인 서비스가 HTTP 오류를 반환했습니다 ("
                        + ((int)statusCode).ToString(CultureInfo.InvariantCulture)
                        + ").";
                    break;
            }

            return new AdminApiException(message, statusCode, null, null);
        }

        private static string FormatGuid(Guid value)
        {
            if (value == Guid.Empty)
            {
                throw new ArgumentException("ID cannot be empty.", nameof(value));
            }

            return value.ToString("D");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AdminApiClient));
            }
        }
    }
}
