using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.ExternalProtocol.Authentication;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.ExternalProtocol.RateLimiting;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.Infrastructure.Protocol;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal interface IExternalDailyApiKeyAuthenticator
    {
        bool TryAuthenticate(
            IEnumerable<string> headerValues,
            DateTimeOffset localNow,
            out ProductCode authenticatedProductCode);
    }

    internal sealed class SystemExternalDailyApiKeyAuthenticator
        : IExternalDailyApiKeyAuthenticator
    {
        public bool TryAuthenticate(
            IEnumerable<string> headerValues,
            DateTimeOffset localNow,
            out ProductCode authenticatedProductCode)
        {
            return DailyApiKeyAuthenticator.TryAuthenticate(
                headerValues,
                localNow,
                out authenticatedProductCode);
        }
    }

    // This is the transport-neutral boundary for the remote External
    // endpoints on the configured non-loopback ListenAddress. The watchdog's
    // separate 127.0.0.1 health boundary is not handled here. An HttpListener
    // host copies the exact raw path and query from RawUrl (without decoding
    // or normalizing them), copies other metadata/body stream into
    // ExternalHttpRequestData, writes ExternalHttpResponseData, and enforces
    // the complete endpoint deadlines including synchronous
    // body reads. This adapter does not own listener lifetime, cancellation,
    // or stream-timeout policy.
    public sealed class ExternalHttpAdapter
    {
        private const string HealthMethod = "GET";
        private const string HealthPath = "/api/health";
        private const string ServicesMethod = "GET";
        private const string ServicesPath = "/api/services";
        private const string RegistrationMethod = "POST";
        private const string RegistrationPath = "/api/registration";
        private const string RenewalPath = "/api/certificates/renew";
        private const string CertificateAuthorityMethod = "GET";
        private const string CertificateRevocationListMethod = "GET";

        private readonly ExternalApiHandler _coreHandler;
        private readonly ServiceDirectoryListenerAddress _configuredAddress;
        private readonly ExternalRequestAdmissionController
            _admissionController;
        private readonly IExternalSecurityAuditWriter _auditWriter;
        private readonly BoundedRequestBodyReader _bodyReader;
        private readonly IExternalDailyApiKeyAuthenticator _authenticator;
        private readonly Func<DateTimeOffset> _localNowProvider;
        private readonly Func<Guid> _requestIdProvider;

        public ExternalHttpAdapter(
            StateMutationCoordinator coordinator,
            ServiceDirectoryListenerAddress configuredAddress,
            ExternalRequestConcurrencyLimiter concurrencyLimiter,
            SecurityAuditEventLogger securityAuditLogger)
            : this(
                new ExternalApiHandler(
                    coordinator ?? throw new ArgumentNullException(
                        nameof(coordinator))),
                configuredAddress,
                new ExternalRequestAdmissionController(
                    concurrencyLimiter ?? throw new ArgumentNullException(
                        nameof(concurrencyLimiter))),
                new ExternalSecurityAuditWriter(
                    securityAuditLogger ?? throw new ArgumentNullException(
                        nameof(securityAuditLogger))),
                new BoundedRequestBodyReader(),
                new SystemExternalDailyApiKeyAuthenticator(),
                () => DateTimeOffset.Now,
                Guid.NewGuid)
        {
        }

        public ExternalHttpAdapter(
            StateMutationCoordinator coordinator,
            ServiceDirectoryListenerAddress configuredAddress,
            ExternalRequestConcurrencyLimiter concurrencyLimiter,
            SecurityAuditEventLogger securityAuditLogger,
            CertificateAuthorityRuntimeAdministration
                certificateAuthorityAdministration,
            SystemFileLogger systemFileLogger,
            IAdminConfigurationState configurationState)
            : this(
                new ExternalApiHandler(
                    coordinator ?? throw new ArgumentNullException(
                        nameof(coordinator)),
                    new LoggingExternalCertificateService(
                        new RuntimeExternalCertificateService(
                            certificateAuthorityAdministration
                            ?? throw new ArgumentNullException(
                                nameof(
                                    certificateAuthorityAdministration))),
                        new SystemExternalRegistrationLogSink(
                            systemFileLogger
                            ?? throw new ArgumentNullException(
                                nameof(systemFileLogger)),
                            configurationState
                            ?? throw new ArgumentNullException(
                                nameof(configurationState))))),
                configuredAddress,
                new ExternalRequestAdmissionController(
                    concurrencyLimiter ?? throw new ArgumentNullException(
                        nameof(concurrencyLimiter))),
                new ExternalSecurityAuditWriter(
                    securityAuditLogger ?? throw new ArgumentNullException(
                        nameof(securityAuditLogger))),
                new BoundedRequestBodyReader(),
                new SystemExternalDailyApiKeyAuthenticator(),
                () => DateTimeOffset.Now,
                Guid.NewGuid)
        {
        }

        internal ExternalHttpAdapter(
            ExternalApiHandler coreHandler,
            ServiceDirectoryListenerAddress configuredAddress,
            ExternalRequestAdmissionController admissionController,
            IExternalSecurityAuditWriter auditWriter,
            BoundedRequestBodyReader bodyReader,
            IExternalDailyApiKeyAuthenticator authenticator,
            Func<DateTimeOffset> localNowProvider,
            Func<Guid> requestIdProvider)
        {
            _coreHandler = coreHandler
                ?? throw new ArgumentNullException(nameof(coreHandler));
            _configuredAddress = configuredAddress
                ?? throw new ArgumentNullException(nameof(configuredAddress));
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

            SecurityAuditOperation auditOperation = ResolveAuditOperation(
                request.AbsolutePath);
            IPEndPoint localEndpoint = request.LocalEndpoint;
            if (localEndpoint == null)
            {
                _auditWriter.WriteNetworkBoundaryRejected(
                    requestId,
                    auditOperation,
                    ExternalNetworkBoundaryFailure.LocalEndpointUnavailable,
                    null);
                return ExternalHttpResponseData.Bodyless(403);
            }

            if (!HttpRequestEndpointGuard.IsConfiguredLocalEndpointAllowed(
                    localEndpoint,
                    _configuredAddress))
            {
                _auditWriter.WriteNetworkBoundaryRejected(
                    requestId,
                    auditOperation,
                    ExternalNetworkBoundaryFailure.LocalEndpointMismatch,
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
                    auditOperation,
                    ExternalNetworkBoundaryFailure.RemoteEndpointUnavailable,
                    null);
                return ExternalHttpResponseData.Bodyless(403);
            }

            IPAddress remoteAddress = CopyAddress(remoteEndpoint.Address);

            ExternalHttpEndpoint endpoint = ResolveEndpoint(
                request.Method,
                request.AbsolutePath);
            if (endpoint == ExternalHttpEndpoint.CertificateAuthority
                || endpoint ==
                    ExternalHttpEndpoint.CertificateRevocationList)
            {
                return ProcessPublicPki(
                    request,
                    endpoint,
                    remoteAddress);
            }

            // One local time value is captured for one authentication attempt.
            // The same value drives health UTC and registration request time,
            // so a local-midnight transition cannot cause a second key check.
            DateTimeOffset authenticatedAt = _localNowProvider();
            ProductCode authenticatedProductCode;
            if (!_authenticator.TryAuthenticate(
                    request.ApiKeyHeaderValues,
                    authenticatedAt,
                    out authenticatedProductCode))
            {
                _auditWriter.WriteApiKeyRejected(
                    requestId,
                    auditOperation,
                    remoteAddress);
                return ExternalHttpResponseData.XmlError(
                    401,
                    ExternalResponseCode.InvalidApiKey);
            }

            ExternalRequestAdmissionResult admission =
                _admissionController.TryAcquire(
                    endpoint,
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
                if (endpoint == ExternalHttpEndpoint.Undefined)
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
                if (endpoint == ExternalHttpEndpoint.Registration
                    || endpoint == ExternalHttpEndpoint.Renewal)
                {
                    if (!IsSupportedXmlContentType(request.ContentType))
                    {
                        return ExternalHttpResponseData.Bodyless(415);
                    }

                    if (!TryReadBody(
                            request,
                            false,
                            true,
                            out body,
                            out bodyFailure))
                    {
                        return bodyFailure;
                    }
                }
                else if (!TryReadBody(
                    request,
                    true,
                    false,
                    out body,
                    out bodyFailure))
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
                    _coreHandler.HandleAuthenticated(
                        coreRequest,
                        authenticatedProductCode,
                        authenticatedAt);
                if (coreResponse.RequiresInvalidApiKeyAudit)
                {
                    _auditWriter.WriteApiKeyRejected(
                        requestId,
                        auditOperation,
                        remoteAddress);
                }

                return ExternalHttpResponseData.FromCore(coreResponse);
            }
        }

        private ExternalHttpResponseData ProcessPublicPki(
            ExternalHttpRequestData request,
            ExternalHttpEndpoint endpoint,
            IPAddress remoteAddress)
        {
            ExternalRequestAdmissionResult admission =
                _admissionController.TryAcquirePublicPki(
                    endpoint,
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
                if (!TryReadBody(
                        request,
                        true,
                        false,
                        out body,
                        out bodyFailure))
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
                return ExternalHttpResponseData.FromCore(
                    _coreHandler.Handle(coreRequest));
            }
        }

        private bool TryReadBody(
            ExternalHttpRequestData request,
            bool allowMissingEmptyStream,
            bool isCertificateRequest,
            out byte[] body,
            out ExternalHttpResponseData failure)
        {
            body = null;
            failure = null;

            int maximumBodyBytes = isCertificateRequest
                ? ExternalApiContract.MaximumCertificateRequestBodyBytes
                : ExternalApiContract.MaximumBodyBytes;
            if (request.DeclaredContentLength > maximumBodyBytes)
            {
                failure = ExternalHttpResponseData.Bodyless(413);
                return false;
            }

            if (request.BodyStream == null)
            {
                if (allowMissingEmptyStream
                    && request.DeclaredContentLength <= 0)
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

            BoundedBodyReadResult bodyResult = isCertificateRequest
                ? _bodyReader.ReadCertificateRequest(
                    request.BodyStream,
                    request.DeclaredContentLength)
                : _bodyReader.ReadStandard(
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

        private static ExternalHttpEndpoint ResolveEndpoint(
            string method,
            string absolutePath)
        {
            if (StringComparer.Ordinal.Equals(method, HealthMethod)
                && StringComparer.Ordinal.Equals(absolutePath, HealthPath))
            {
                return ExternalHttpEndpoint.Health;
            }

            if (StringComparer.Ordinal.Equals(method, ServicesMethod)
                && StringComparer.Ordinal.Equals(absolutePath, ServicesPath))
            {
                return ExternalHttpEndpoint.Services;
            }

            if (StringComparer.Ordinal.Equals(method, RegistrationMethod)
                && StringComparer.Ordinal.Equals(
                    absolutePath,
                    RegistrationPath))
            {
                return ExternalHttpEndpoint.Registration;
            }

            if (StringComparer.Ordinal.Equals(method, RegistrationMethod)
                && StringComparer.Ordinal.Equals(
                    absolutePath,
                    RenewalPath))
            {
                return ExternalHttpEndpoint.Renewal;
            }

            if (StringComparer.Ordinal.Equals(
                    method,
                    CertificateAuthorityMethod)
                && StringComparer.Ordinal.Equals(
                    absolutePath,
                    ExternalApiContract.CaPath))
            {
                return ExternalHttpEndpoint.CertificateAuthority;
            }

            if (StringComparer.Ordinal.Equals(
                    method,
                    CertificateRevocationListMethod)
                && StringComparer.Ordinal.Equals(
                    absolutePath,
                    ExternalApiContract.CrlPath))
            {
                return ExternalHttpEndpoint.CertificateRevocationList;
            }

            string ignoredCaSerialNumber;
            if (StringComparer.Ordinal.Equals(
                    method,
                    CertificateRevocationListMethod)
                && ExternalApiContract.TryParseIssuerCrlPath(
                    absolutePath,
                    out ignoredCaSerialNumber))
            {
                return ExternalHttpEndpoint.CertificateRevocationList;
            }

            return ExternalHttpEndpoint.Undefined;
        }

        private static SecurityAuditOperation ResolveAuditOperation(
            string absolutePath)
        {
            if (StringComparer.Ordinal.Equals(absolutePath, HealthPath))
            {
                return SecurityAuditOperation.ExternalHealth;
            }

            if (StringComparer.Ordinal.Equals(absolutePath, ServicesPath))
            {
                return SecurityAuditOperation.ExternalServiceLookup;
            }

            if (StringComparer.Ordinal.Equals(
                    absolutePath,
                    RegistrationPath))
            {
                return SecurityAuditOperation.ExternalRegistration;
            }

            if (StringComparer.Ordinal.Equals(
                    absolutePath,
                    RenewalPath))
            {
                return SecurityAuditOperation.ExternalRegistration;
            }

            return SecurityAuditOperation.ExternalUnknown;
        }

        private static ExternalHttpResponseData InternalError()
        {
            return ExternalHttpResponseData.XmlError(
                500,
                ExternalResponseCode.Internal);
        }

        private static bool IsSupportedXmlContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType))
            {
                return false;
            }

            string[] segments = contentType.Split(';');
            if (segments.Length != 2
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    TrimHttpOptionalWhitespace(segments[0]),
                    "application/xml"))
            {
                return false;
            }

            string parameter = TrimHttpOptionalWhitespace(segments[1]);
            int equalsIndex = parameter.IndexOf('=');
            if (equalsIndex <= 0
                || equalsIndex != parameter.LastIndexOf('='))
            {
                return false;
            }

            string name = TrimHttpOptionalWhitespace(
                parameter.Substring(0, equalsIndex));
            string value = TrimHttpOptionalWhitespace(
                parameter.Substring(equalsIndex + 1));
            return StringComparer.OrdinalIgnoreCase.Equals(name, "charset")
                && StringComparer.OrdinalIgnoreCase.Equals(value, "utf-8");
        }

        private static string TrimHttpOptionalWhitespace(string value)
        {
            int start = 0;
            while (start < value.Length
                && (value[start] == ' ' || value[start] == '\t'))
            {
                start++;
            }

            int end = value.Length - 1;
            while (end >= start
                && (value[end] == ' ' || value[end] == '\t'))
            {
                end--;
            }

            return start == 0 && end == value.Length - 1
                ? value
                : value.Substring(start, end - start + 1);
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

        private sealed class RuntimeExternalCertificateService
            : IExternalCertificateService
        {
            private readonly CertificateAuthorityRuntimeAdministration
                _administration;

            internal RuntimeExternalCertificateService(
                CertificateAuthorityRuntimeAdministration administration)
            {
                _administration = administration
                    ?? throw new ArgumentNullException(
                        nameof(administration));
            }

            public ExternalTrustInfo GetTrustInfo()
            {
                return _administration.GetExternalTrustInfo();
            }

            public ExternalTrustSnapshot GetTrustSnapshot()
            {
                return _administration.GetExternalTrustSnapshot();
            }

            public byte[] GetCertificateRevocationList()
            {
                return _administration
                    .GetExternalCertificateRevocationList();
            }

            public byte[] GetCertificateRevocationList(
                string caSerialNumber)
            {
                return _administration
                    .GetExternalCertificateRevocationList(caSerialNumber);
            }

            public ExternalRegistrationServiceResult Register(
                ExternalRegistrationRequest request,
                DateTime utcNow)
            {
                return _administration.RegisterExternalService(
                    request,
                    utcNow);
            }

            public ExternalRegistrationServiceResult Renew(
                ExternalCertificateRenewalRequest request,
                DateTime utcNow)
            {
                return _administration.RenewExternalService(
                    request,
                    utcNow);
            }
        }
    }
}
