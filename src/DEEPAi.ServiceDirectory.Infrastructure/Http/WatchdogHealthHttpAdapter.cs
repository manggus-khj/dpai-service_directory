using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.ExternalProtocol.RateLimiting;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;
using DEEPAi.ServiceDirectory.Infrastructure.Protocol;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    // Transport-neutral boundary for only GET
    // http://127.0.0.1:21000/api/health. Remote External endpoints use a
    // different adapter and different rate-state maps. The host must pass the
    // same ExternalRequestConcurrencyLimiter instance to both boundaries.
    // It also copies the exact raw path/query from RawUrl without decoding or
    // normalization. The host enforces the complete five-second deadline,
    // including synchronous body reads. This adapter owns no listener
    // lifecycle or stream-timeout policy.
    public sealed class WatchdogHealthHttpAdapter
    {
        private const string HealthMethod = "GET";
        private const string HealthPath = "/api/health";

        private readonly ExternalRequestAdmissionController
            _admissionController;
        private readonly IWatchdogHealthSecurityAuditWriter _auditWriter;
        private readonly BoundedRequestBodyReader _bodyReader;
        private readonly IExternalDailyApiKeyAuthenticator _authenticator;
        private readonly Func<DateTimeOffset> _localNowProvider;
        private readonly Func<Guid> _requestIdProvider;

        public WatchdogHealthHttpAdapter(
            ExternalRequestConcurrencyLimiter sharedConcurrencyLimiter,
            SecurityAuditEventLogger securityAuditLogger)
            : this(
                new ExternalRequestAdmissionController(
                    sharedConcurrencyLimiter
                        ?? throw new ArgumentNullException(
                            nameof(sharedConcurrencyLimiter))),
                new WatchdogHealthSecurityAuditWriter(
                    securityAuditLogger
                        ?? throw new ArgumentNullException(
                            nameof(securityAuditLogger))),
                new BoundedRequestBodyReader(),
                new SystemExternalDailyApiKeyAuthenticator(),
                () => DateTimeOffset.Now,
                Guid.NewGuid)
        {
        }

        internal WatchdogHealthHttpAdapter(
            ExternalRequestAdmissionController admissionController,
            IWatchdogHealthSecurityAuditWriter auditWriter,
            BoundedRequestBodyReader bodyReader,
            IExternalDailyApiKeyAuthenticator authenticator,
            Func<DateTimeOffset> localNowProvider,
            Func<Guid> requestIdProvider)
        {
            _admissionController = admissionController
                ?? throw new ArgumentNullException(
                    nameof(admissionController));
            _auditWriter = auditWriter
                ?? throw new ArgumentNullException(nameof(auditWriter));
            _bodyReader = bodyReader
                ?? throw new ArgumentNullException(nameof(bodyReader));
            _authenticator = authenticator
                ?? throw new ArgumentNullException(nameof(authenticator));
            _localNowProvider = localNowProvider
                ?? throw new ArgumentNullException(nameof(localNowProvider));
            _requestIdProvider = requestIdProvider
                ?? throw new ArgumentNullException(nameof(requestIdProvider));
        }

        public ExternalHttpResponseData Process(
            ExternalHttpRequestData request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            try
            {
                return ProcessCore(request);
            }
            catch (SecurityAuditSourceUnavailableException)
            {
                throw;
            }
            catch (SecurityAuditWriteException)
            {
                throw;
            }
            catch (Exception)
            {
                return InternalError();
            }
        }

        private ExternalHttpResponseData ProcessCore(
            ExternalHttpRequestData request)
        {
            Guid requestId = _requestIdProvider();
            if (requestId == Guid.Empty)
            {
                return InternalError();
            }

            IPEndPoint localEndpoint = request.LocalEndpoint;
            if (localEndpoint == null || localEndpoint.Address == null)
            {
                _auditWriter.WriteNetworkBoundaryRejected(
                    requestId,
                    WatchdogHealthNetworkBoundaryFailure
                        .LocalEndpointUnavailable,
                    null);
                return ExternalHttpResponseData.Bodyless(403);
            }

            if (!IsExactLocalEndpoint(localEndpoint))
            {
                _auditWriter.WriteNetworkBoundaryRejected(
                    requestId,
                    WatchdogHealthNetworkBoundaryFailure
                        .LocalEndpointMismatch,
                    null);
                return ExternalHttpResponseData.Bodyless(403);
            }

            IPEndPoint remoteEndpoint = request.RemoteEndpoint;
            if (remoteEndpoint == null
                || remoteEndpoint.Address == null
                || !IsSupportedNetworkAddress(remoteEndpoint.Address))
            {
                _auditWriter.WriteNetworkBoundaryRejected(
                    requestId,
                    WatchdogHealthNetworkBoundaryFailure
                        .RemoteEndpointUnavailable,
                    null);
                return ExternalHttpResponseData.Bodyless(403);
            }

            IPAddress remoteAddress = CopyAddress(remoteEndpoint.Address);
            if (!HttpRequestEndpointGuard.IsLoopbackScopeAllowed(
                    localEndpoint,
                    remoteEndpoint))
            {
                _auditWriter.WriteNetworkBoundaryRejected(
                    requestId,
                    WatchdogHealthNetworkBoundaryFailure
                        .RemoteEndpointNotLoopback,
                    remoteAddress);
                return ExternalHttpResponseData.Bodyless(403);
            }

            // One local value drives exactly one key validation and the UTC
            // health response. The recovered ProductCode is used only for
            // the health combination bucket; it is not compared with WDOG.
            DateTimeOffset authenticatedAt = _localNowProvider();
            ProductCode authenticatedProductCode;
            if (!_authenticator.TryAuthenticate(
                    request.ApiKeyHeaderValues,
                    authenticatedAt,
                    out authenticatedProductCode))
            {
                _auditWriter.WriteApiKeyRejected(
                    requestId,
                    remoteAddress);
                return ExternalHttpResponseData.XmlError(
                    401,
                    ExternalResponseCode.InvalidApiKey);
            }

            bool isHealthRoute = IsHealthRoute(
                request.Method,
                request.AbsolutePath);
            ExternalRequestAdmissionResult admission =
                _admissionController.TryAcquire(
                    isHealthRoute
                        ? ExternalHttpEndpoint.Health
                        : ExternalHttpEndpoint.Undefined,
                    authenticatedProductCode,
                    remoteAddress);
            if (!admission.IsGranted)
            {
                return ExternalHttpResponseData.XmlError(
                    429,
                    ExternalResponseCode.LimitExceeded,
                    admission.RetryAfterSeconds);
            }

            using (admission.Lease)
            {
                if (!isHealthRoute)
                {
                    return ExternalHttpResponseData.Bodyless(404);
                }

                IReadOnlyList<ExternalApiQueryParameter> queryParameters;
                if (!ExternalQueryStringParser.TryParse(
                        request.RawQuery,
                        out queryParameters))
                {
                    return ExternalHttpResponseData.XmlError(
                        400,
                        ExternalResponseCode.BadRequest);
                }

                if (!string.IsNullOrEmpty(
                        request.ContentEncodingHeaderValue))
                {
                    return ExternalHttpResponseData.Bodyless(415);
                }

                byte[] body;
                ExternalHttpResponseData bodyFailure;
                if (!TryReadBody(request, out body, out bodyFailure))
                {
                    return bodyFailure;
                }

                var coreRequest = new ExternalApiHandlerRequest(
                    request.Method,
                    request.AbsolutePath,
                    queryParameters,
                    new string[0],
                    body,
                    remoteAddress);
                ExternalApiHandlerResponse coreResponse =
                    ExternalApiHandler.HandleAuthenticatedHealth(
                        coreRequest,
                        authenticatedAt);
                return ExternalHttpResponseData.FromCore(coreResponse);
            }
        }

        private bool TryReadBody(
            ExternalHttpRequestData request,
            out byte[] body,
            out ExternalHttpResponseData failure)
        {
            body = null;
            failure = null;
            if (request.DeclaredContentLength
                > ExternalApiContract.MaximumBodyBytes)
            {
                failure = ExternalHttpResponseData.Bodyless(413);
                return false;
            }

            if (request.BodyStream == null)
            {
                if (request.DeclaredContentLength <= 0)
                {
                    body = new byte[0];
                    return true;
                }

                failure = ExternalHttpResponseData.XmlError(
                    400,
                    ExternalResponseCode.BadRequest);
                return false;
            }

            if (!request.BodyStream.CanRead)
            {
                failure = ExternalHttpResponseData.XmlError(
                    400,
                    ExternalResponseCode.BadRequest);
                return false;
            }

            BoundedBodyReadResult bodyResult = _bodyReader.ReadStandard(
                request.BodyStream,
                request.DeclaredContentLength);
            if (!bodyResult.IsSuccess)
            {
                failure = MapBodyReadFailure(bodyResult.FailureCode);
                return false;
            }

            body = CopyBody(bodyResult.Body);
            return true;
        }

        private static ExternalHttpResponseData MapBodyReadFailure(
            BoundedBodyReadFailureCode failureCode)
        {
            switch (failureCode)
            {
                case BoundedBodyReadFailureCode.DeclaredLengthTooLarge:
                case BoundedBodyReadFailureCode.ActualLengthTooLarge:
                    return ExternalHttpResponseData.Bodyless(413);
                case BoundedBodyReadFailureCode.DeclaredLengthMismatch:
                    return ExternalHttpResponseData.XmlError(
                        400,
                        ExternalResponseCode.BadRequest);
                case BoundedBodyReadFailureCode.IoFailure:
                    return InternalError();
                case BoundedBodyReadFailureCode.None:
                default:
                    return InternalError();
            }
        }

        private static bool IsHealthRoute(
            string method,
            string absolutePath)
        {
            return StringComparer.Ordinal.Equals(method, HealthMethod)
                && StringComparer.Ordinal.Equals(absolutePath, HealthPath);
        }

        private static bool IsExactLocalEndpoint(IPEndPoint endpoint)
        {
            return endpoint != null
                && endpoint.Address != null
                && endpoint.Port == ServiceDirectoryListenerAddress.Port
                && endpoint.Address.Equals(IPAddress.Loopback);
        }

        private static bool IsSupportedNetworkAddress(IPAddress address)
        {
            return address != null
                && (address.AddressFamily == AddressFamily.InterNetwork
                    || address.AddressFamily ==
                        AddressFamily.InterNetworkV6);
        }

        private static IPAddress CopyAddress(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return address.AddressFamily == AddressFamily.InterNetwork
                ? new IPAddress(bytes)
                : new IPAddress(bytes, address.ScopeId);
        }

        private static byte[] CopyBody(BoundedRequestBody body)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            var contents = new byte[body.Length];
            using (Stream stream = body.OpenRead())
            {
                int offset = 0;
                while (offset < contents.Length)
                {
                    int read = stream.Read(
                        contents,
                        offset,
                        contents.Length - offset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException(
                            "The bounded request body ended unexpectedly.");
                    }

                    offset += read;
                }
            }

            return contents;
        }

        private static ExternalHttpResponseData InternalError()
        {
            return ExternalHttpResponseData.XmlError(
                500,
                ExternalResponseCode.Internal);
        }
    }
}
