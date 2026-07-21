using System;
using DEEPAi.ServiceDirectory.Application.Queries;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.ExternalProtocol.Authentication;

namespace DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi
{
    // HttpListener remains a host concern. The transport-neutral HTTP adapter
    // enforces endpoint, body, media-type, and admission controls before it
    // calls HandleAuthenticated. Handle remains useful for protocol-level
    // callers and authenticates exactly once before delegating to the same
    // endpoint logic.
    public sealed class ExternalApiHandler
    {
        private const string HealthPath = "/api/health";
        private const string ServicesPath = "/api/services";
        private const string RegistrationPath = "/api/registration";
        private const string RenewalPath = "/api/certificates/renew";
        private const string ProductCodeQueryName = "productCode";

        private readonly ApprovedServiceLookup _approvedServiceLookup;
        private readonly Func<DateTimeOffset> _localNowProvider;
        private readonly IExternalCertificateService _certificateService;

        public ExternalApiHandler(StateMutationCoordinator coordinator)
            : this(
                coordinator,
                () => DateTimeOffset.Now,
                UnavailableExternalCertificateService.Instance)
        {
        }

        internal ExternalApiHandler(
            StateMutationCoordinator coordinator,
            IExternalCertificateService certificateService)
            : this(
                coordinator,
                () => DateTimeOffset.Now,
                certificateService)
        {
        }

        internal ExternalApiHandler(
            StateMutationCoordinator coordinator,
            Func<DateTimeOffset> localNowProvider)
            : this(
                coordinator,
                localNowProvider,
                UnavailableExternalCertificateService.Instance)
        {
        }

        internal ExternalApiHandler(
            StateMutationCoordinator coordinator,
            Func<DateTimeOffset> localNowProvider,
            IExternalCertificateService certificateService)
        {
            if (coordinator == null)
            {
                throw new ArgumentNullException(nameof(coordinator));
            }

            _approvedServiceLookup = new ApprovedServiceLookup(coordinator);
            _localNowProvider = localNowProvider
                ?? throw new ArgumentNullException(nameof(localNowProvider));
            _certificateService = certificateService
                ?? throw new ArgumentNullException(
                    nameof(certificateService));
        }

        public ExternalApiHandlerResponse Handle(
            ExternalApiHandlerRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            DateTimeOffset localNow;
            try
            {
                ExternalEndpoint endpoint = ResolveEndpoint(
                    request.Method,
                    request.AbsolutePath);
                if (endpoint == ExternalEndpoint.CertificateAuthority
                    || endpoint == ExternalEndpoint.CertificateRevocationList)
                {
                    return HandlePublicPki(request, endpoint);
                }

                localNow = _localNowProvider();
                ProductCode authenticatedProductCode;
                if (!DailyApiKeyAuthenticator.TryAuthenticate(
                        request.ApiKeyHeaderValues,
                        localNow,
                        out authenticatedProductCode))
                {
                    return Error(
                        401,
                        ExternalResponseCode.InvalidApiKey,
                        true);
                }

                return HandleAuthenticated(
                    request,
                    authenticatedProductCode,
                    localNow);
            }
            catch (Exception)
            {
                return Error(500, ExternalResponseCode.Internal);
            }
        }

        internal ExternalApiHandlerResponse HandleAuthenticated(
            ExternalApiHandlerRequest request,
            ProductCode authenticatedProductCode,
            DateTimeOffset authenticatedAt)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!authenticatedProductCode.IsValid)
            {
                throw new ArgumentException(
                    "A valid authenticated ProductCode is required.",
                    nameof(authenticatedProductCode));
            }

            try
            {
                ExternalEndpoint endpoint = ResolveEndpoint(
                    request.Method,
                    request.AbsolutePath);
                switch (endpoint)
                {
                    case ExternalEndpoint.Health:
                        return HandleAuthenticatedHealth(
                            request,
                            authenticatedAt);
                    case ExternalEndpoint.Services:
                        return HandleServiceLookup(
                            request,
                            authenticatedProductCode);
                    case ExternalEndpoint.Registration:
                        return HandleRegistration(
                            request,
                            authenticatedProductCode,
                            authenticatedAt.UtcDateTime);
                    case ExternalEndpoint.Renewal:
                        return HandleRenewal(
                            request,
                            authenticatedProductCode,
                            authenticatedAt.UtcDateTime);
                    case ExternalEndpoint.CertificateAuthority:
                    case ExternalEndpoint.CertificateRevocationList:
                        return HandlePublicPki(request, endpoint);
                    case ExternalEndpoint.Undefined:
                        return ExternalApiHandlerResponse.UndefinedRoute();
                    default:
                        return Error(500, ExternalResponseCode.Internal);
                }
            }
            catch (Exception)
            {
                return Error(500, ExternalResponseCode.Internal);
            }
        }

        // The transport boundary has already authenticated exactly one daily
        // key and validated the fixed health route before calling this
        // internal entry point. ProductCode is deliberately absent: health
        // validates key format/date but does not require the WDOG value.
        internal static ExternalApiHandlerResponse HandleAuthenticatedHealth(
            ExternalApiHandlerRequest request,
            DateTimeOffset localNow)
        {
            if (request.QueryParameters.Count != 0
                || request.BodyLength != 0)
            {
                return Error(400, ExternalResponseCode.BadRequest);
            }

            ExternalResponse response =
                ExternalResponse.CreateHealthSuccess(localNow.UtcDateTime);
            return ExternalApiHandlerResponse.Xml(
                200,
                ExternalXmlCodec.SerializeHealthResponse(response));
        }

        private ExternalApiHandlerResponse HandleServiceLookup(
            ExternalApiHandlerRequest request,
            ProductCode authenticatedProductCode)
        {
            if (request.BodyLength != 0)
            {
                return Error(400, ExternalResponseCode.BadRequest);
            }

            string rawProductCode;
            if (!TryReadOnlyProductCodeQuery(request, out rawProductCode))
            {
                return Error(400, ExternalResponseCode.BadRequest);
            }

            ProductCode requestedProductCode;
            if (!ProductCode.TryCreate(
                    rawProductCode,
                    out requestedProductCode))
            {
                return Error(400, ExternalResponseCode.BadRequest);
            }

            if (requestedProductCode != authenticatedProductCode)
            {
                return Error(
                    401,
                    ExternalResponseCode.InvalidApiKey,
                    true);
            }

            ApprovedServiceLookupResult lookup =
                _approvedServiceLookup.Find(requestedProductCode);
            switch (lookup.Status)
            {
                case ApprovedServiceLookupStatus.Found:
                    ApprovedServiceView service = lookup.Service;
                    var externalService = new ExternalServiceItem(
                        service.Name,
                        service.ProductCode,
                        service.ServiceHostName,
                        service.ServiceIpv4Address,
                        service.Port,
                        service.LastModifiedUtc);
                    return ExternalApiHandlerResponse.Xml(
                        200,
                        ExternalXmlCodec.SerializeServiceResponse(
                            ExternalResponse.CreateServiceSuccess(
                                externalService)));
                case ApprovedServiceLookupStatus.NotFound:
                    return Error(404, ExternalResponseCode.NotFound);
                case ApprovedServiceLookupStatus.Unavailable:
                    return Error(500, ExternalResponseCode.Internal);
                default:
                    return Error(500, ExternalResponseCode.Internal);
            }
        }

        private ExternalApiHandlerResponse HandleRegistration(
            ExternalApiHandlerRequest request,
            ProductCode authenticatedProductCode,
            DateTime utcNow)
        {
            if (request.QueryParameters.Count != 0)
            {
                return Error(400, ExternalResponseCode.BadRequest);
            }

            ExternalRegistrationRequest registrationRequest;
            try
            {
                registrationRequest =
                    ExternalXmlCodec.ParseRegistrationRequest(
                        request.CopyBody());
            }
            catch (ExternalProtocolException)
            {
                return Error(400, ExternalResponseCode.BadRequest);
            }

            ProductCode requestProductCode;
            if (!ProductCode.TryCreate(
                    registrationRequest.ProductCode,
                    out requestProductCode)
                || requestProductCode != authenticatedProductCode)
            {
                return Error(
                    401,
                    ExternalResponseCode.InvalidApiKey,
                    true);
            }

            ExternalRegistrationServiceResult result =
                _certificateService.Register(
                    registrationRequest,
                    utcNow);
            switch (result.Status)
            {
                case ExternalRegistrationServiceStatus.Registered:
                case ExternalRegistrationServiceStatus.Reregistered:
                case ExternalRegistrationServiceStatus.Replayed:
                    return ExternalApiHandlerResponse.Xml(
                        200,
                        ExternalXmlCodec.SerializeRegistrationResponse(
                            ExternalResponse.CreateRegistrationSuccess(
                                ToIssuanceStatus(result.Status),
                                registrationRequest.RegistrationRequestId,
                                result.Service,
                                result.Certificate)));
                case ExternalRegistrationServiceStatus.RegistrationModeClosed:
                    return Error(
                        409,
                        ExternalResponseCode.RegistrationModeClosed);
                case ExternalRegistrationServiceStatus.Conflict:
                    return Error(409, ExternalResponseCode.Conflict);
                case ExternalRegistrationServiceStatus.CertificateRequestInvalid:
                    return Error(
                        400,
                        ExternalResponseCode.CertificateRequestInvalid);
                case ExternalRegistrationServiceStatus.LimitExceeded:
                    return Error(
                        429,
                        ExternalResponseCode.LimitExceeded);
                default:
                    return Error(500, ExternalResponseCode.Internal);
            }
        }

        private ExternalApiHandlerResponse HandlePublicPki(
            ExternalApiHandlerRequest request,
            ExternalEndpoint endpoint)
        {
            if (request.QueryParameters.Count != 0
                || request.BodyLength != 0)
            {
                return Error(400, ExternalResponseCode.BadRequest);
            }

            if (endpoint == ExternalEndpoint.CertificateAuthority)
            {
                ExternalTrustSnapshot trust =
                    _certificateService.GetTrustSnapshot();
                return ExternalApiHandlerResponse.Xml(
                    200,
                    ExternalXmlCodec.SerializeTrustInfoResponse(
                        ExternalResponse.CreateTrustInfoSuccess(
                            trust.TrustInfo,
                            trust.TrustBundle)));
            }

            if (endpoint == ExternalEndpoint.CertificateRevocationList)
            {
                return ExternalApiHandlerResponse.Binary(
                    200,
                    _certificateService.GetCertificateRevocationList(),
                    ExternalApiContract.CrlContentType);
            }

            if (endpoint
                == ExternalEndpoint.IssuerCertificateRevocationList)
            {
                string caSerialNumber;
                if (!ExternalApiContract.TryParseIssuerCrlPath(
                        request.AbsolutePath,
                        out caSerialNumber))
                {
                    return ExternalApiHandlerResponse.UndefinedRoute();
                }

                byte[] crl = _certificateService
                    .GetCertificateRevocationList(caSerialNumber);
                return crl == null
                    ? Error(404, ExternalResponseCode.NotFound)
                    : ExternalApiHandlerResponse.Binary(
                        200,
                        crl,
                        ExternalApiContract.CrlContentType);
            }

            return ExternalApiHandlerResponse.UndefinedRoute();
        }

        private ExternalApiHandlerResponse HandleRenewal(
            ExternalApiHandlerRequest request,
            ProductCode authenticatedProductCode,
            DateTime utcNow)
        {
            if (request.QueryParameters.Count != 0)
            {
                return Error(400, ExternalResponseCode.BadRequest);
            }

            ExternalCertificateRenewalRequest renewalRequest;
            try
            {
                renewalRequest =
                    ExternalXmlCodec.ParseCertificateRenewalRequest(
                        request.CopyBody());
            }
            catch (ExternalProtocolException)
            {
                return Error(400, ExternalResponseCode.BadRequest);
            }

            ProductCode requestProductCode;
            if (!ProductCode.TryCreate(
                    renewalRequest.ProductCode,
                    out requestProductCode)
                || requestProductCode != authenticatedProductCode)
            {
                return Error(
                    401,
                    ExternalResponseCode.InvalidApiKey,
                    true);
            }

            ExternalRegistrationServiceResult result =
                _certificateService.Renew(renewalRequest, utcNow);
            switch (result.Status)
            {
                case ExternalRegistrationServiceStatus.Renewed:
                case ExternalRegistrationServiceStatus.Replayed:
                    return ExternalApiHandlerResponse.Xml(
                        200,
                        ExternalXmlCodec
                            .SerializeCertificateRenewalResponse(
                                ExternalResponse.CreateRenewalSuccess(
                                    renewalRequest.RenewalRequestId,
                                    result.Service,
                                    result.Certificate)));
                case ExternalRegistrationServiceStatus.Conflict:
                    return Error(409, ExternalResponseCode.Conflict);
                case ExternalRegistrationServiceStatus.CertificateRequestInvalid:
                    return Error(
                        400,
                        ExternalResponseCode.CertificateRequestInvalid);
                case ExternalRegistrationServiceStatus.CertificateNotRenewable:
                    return Error(
                        409,
                        ExternalResponseCode.CertificateNotRenewable);
                case ExternalRegistrationServiceStatus.InvalidCertificateProof:
                    return ExternalApiHandlerResponse.Bodyless(401);
                case ExternalRegistrationServiceStatus.LimitExceeded:
                    return Error(
                        429,
                        ExternalResponseCode.LimitExceeded);
                default:
                    return Error(500, ExternalResponseCode.Internal);
            }
        }

        private static ExternalCertificateIssuanceStatus ToIssuanceStatus(
            ExternalRegistrationServiceStatus status)
        {
            switch (status)
            {
                case ExternalRegistrationServiceStatus.Registered:
                    return ExternalCertificateIssuanceStatus.Registered;
                case ExternalRegistrationServiceStatus.Reregistered:
                    return ExternalCertificateIssuanceStatus.Reregistered;
                case ExternalRegistrationServiceStatus.Replayed:
                    return ExternalCertificateIssuanceStatus.Replayed;
                default:
                    throw new ArgumentOutOfRangeException(nameof(status));
            }
        }

        private static bool TryReadOnlyProductCodeQuery(
            ExternalApiHandlerRequest request,
            out string rawProductCode)
        {
            rawProductCode = null;
            if (request.QueryParameters.Count != 1)
            {
                return false;
            }

            ExternalApiQueryParameter parameter = request.QueryParameters[0];
            if (!StringComparer.Ordinal.Equals(
                    parameter.Name,
                    ProductCodeQueryName)
                || parameter.Value == null)
            {
                return false;
            }

            rawProductCode = parameter.Value;
            return true;
        }

        private static ExternalEndpoint ResolveEndpoint(
            string method,
            string absolutePath)
        {
            if (StringComparer.Ordinal.Equals(method, "GET")
                && StringComparer.Ordinal.Equals(absolutePath, HealthPath))
            {
                return ExternalEndpoint.Health;
            }

            if (StringComparer.Ordinal.Equals(method, "GET")
                && StringComparer.Ordinal.Equals(absolutePath, ServicesPath))
            {
                return ExternalEndpoint.Services;
            }

            if (StringComparer.Ordinal.Equals(method, "POST")
                && StringComparer.Ordinal.Equals(
                    absolutePath,
                    RegistrationPath))
            {
                return ExternalEndpoint.Registration;
            }

            if (StringComparer.Ordinal.Equals(method, "POST")
                && StringComparer.Ordinal.Equals(
                    absolutePath,
                    RenewalPath))
            {
                return ExternalEndpoint.Renewal;
            }

            if (StringComparer.Ordinal.Equals(method, "GET")
                && StringComparer.Ordinal.Equals(
                    absolutePath,
                    ExternalApiContract.CaPath))
            {
                return ExternalEndpoint.CertificateAuthority;
            }

            if (StringComparer.Ordinal.Equals(method, "GET")
                && StringComparer.Ordinal.Equals(
                    absolutePath,
                    ExternalApiContract.CrlPath))
            {
                return ExternalEndpoint.CertificateRevocationList;
            }

            string ignoredCaSerialNumber;
            if (StringComparer.Ordinal.Equals(method, "GET")
                && ExternalApiContract.TryParseIssuerCrlPath(
                    absolutePath,
                    out ignoredCaSerialNumber))
            {
                return ExternalEndpoint.IssuerCertificateRevocationList;
            }

            return ExternalEndpoint.Undefined;
        }

        private static ExternalApiHandlerResponse Error(
            int statusCode,
            ExternalResponseCode responseCode,
            bool requiresInvalidApiKeyAudit = false)
        {
            return ExternalApiHandlerResponse.Xml(
                statusCode,
                ExternalXmlCodec.SerializeErrorResponse(
                    ExternalResponse.CreateError(responseCode)),
                null,
                requiresInvalidApiKeyAudit);
        }

        private enum ExternalEndpoint
        {
            Undefined = 0,
            Health = 1,
            Services = 2,
            Registration = 3,
            CertificateAuthority = 4,
            CertificateRevocationList = 5,
            Renewal = 6,
            IssuerCertificateRevocationList = 7
        }

        private sealed class UnavailableExternalCertificateService
            : IExternalCertificateService
        {
            internal static readonly IExternalCertificateService Instance =
                new UnavailableExternalCertificateService();

            public ExternalTrustInfo GetTrustInfo()
            {
                throw new InvalidOperationException(
                    "The External certificate service is unavailable.");
            }

            public ExternalTrustSnapshot GetTrustSnapshot()
            {
                throw new InvalidOperationException(
                    "The External certificate service is unavailable.");
            }

            public byte[] GetCertificateRevocationList()
            {
                throw new InvalidOperationException(
                    "The External certificate service is unavailable.");
            }

            public byte[] GetCertificateRevocationList(
                string caSerialNumber)
            {
                throw new InvalidOperationException(
                    "The External certificate service is unavailable.");
            }

            public ExternalRegistrationServiceResult Register(
                ExternalRegistrationRequest request,
                DateTime utcNow)
            {
                return ExternalRegistrationServiceResult.Failure(
                    ExternalRegistrationServiceStatus.Conflict);
            }

            public ExternalRegistrationServiceResult Renew(
                ExternalCertificateRenewalRequest request,
                DateTime utcNow)
            {
                return ExternalRegistrationServiceResult.Failure(
                    ExternalRegistrationServiceStatus
                        .CertificateNotRenewable);
            }
        }
    }
}
