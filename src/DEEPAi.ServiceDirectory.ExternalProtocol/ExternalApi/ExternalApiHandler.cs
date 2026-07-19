using System;
using DEEPAi.ServiceDirectory.Application.Queries;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Registration;
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
        private const string ProductCodeQueryName = "productCode";

        private readonly StateMutationCoordinator _coordinator;
        private readonly ApprovedServiceLookup _approvedServiceLookup;
        private readonly Func<DateTimeOffset> _localNowProvider;
        private readonly Func<Guid> _pendingIdProvider;

        public ExternalApiHandler(StateMutationCoordinator coordinator)
            : this(
                coordinator,
                () => DateTimeOffset.Now,
                () => Guid.NewGuid())
        {
        }

        internal ExternalApiHandler(
            StateMutationCoordinator coordinator,
            Func<DateTimeOffset> localNowProvider,
            Func<Guid> pendingIdProvider)
        {
            _coordinator = coordinator
                ?? throw new ArgumentNullException(nameof(coordinator));
            _approvedServiceLookup = new ApprovedServiceLookup(coordinator);
            _localNowProvider = localNowProvider
                ?? throw new ArgumentNullException(nameof(localNowProvider));
            _pendingIdProvider = pendingIdProvider
                ?? throw new ArgumentNullException(nameof(pendingIdProvider));
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
                            authenticatedAt);
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
                        service.ServerAddress,
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
            DateTimeOffset localNow)
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

            if (registrationRequest.Definition.ProductCode !=
                authenticatedProductCode)
            {
                return Error(
                    401,
                    ExternalResponseCode.InvalidApiKey,
                    true);
            }

            StateMutationResult<SubmissionResult> mutation =
                _coordinator.Submit(
                    registrationRequest.Definition,
                    request.RemoteAddress,
                    _pendingIdProvider(),
                    localNow.UtcDateTime);
            if (mutation.Status != StateMutationStatus.Completed
                || !mutation.HasDomainTransition)
            {
                return Error(500, ExternalResponseCode.Internal);
            }

            SubmissionResult submission = mutation.DomainTransition;
            if (!submission.IsSuccess)
            {
                return MapDomainFailure(submission.ErrorCode);
            }

            ExternalRegistrationStatus status;
            switch (submission.Status)
            {
                case SubmissionStatus.PendingNew:
                    status = ExternalRegistrationStatus.PendingNew;
                    break;
                case SubmissionStatus.PendingModify:
                    status = ExternalRegistrationStatus.PendingModify;
                    break;
                case SubmissionStatus.PendingExists:
                    status = ExternalRegistrationStatus.PendingExists;
                    break;
                case SubmissionStatus.AlreadyRegistered:
                    status = ExternalRegistrationStatus.AlreadyRegistered;
                    break;
                default:
                    return Error(500, ExternalResponseCode.Internal);
            }

            return ExternalApiHandlerResponse.Xml(
                200,
                ExternalXmlCodec.SerializeRegistrationResponse(
                    ExternalResponse.CreateRegistrationSuccess(
                        status,
                        submission.PendingId)));
        }

        private static ExternalApiHandlerResponse MapDomainFailure(
            DomainErrorCode? errorCode)
        {
            if (!errorCode.HasValue)
            {
                return Error(500, ExternalResponseCode.Internal);
            }

            switch (errorCode.Value)
            {
                case DomainErrorCode.BadRequest:
                    return Error(400, ExternalResponseCode.BadRequest);
                case DomainErrorCode.NotFound:
                    return Error(404, ExternalResponseCode.NotFound);
                case DomainErrorCode.Conflict:
                    return Error(409, ExternalResponseCode.Conflict);
                case DomainErrorCode.LimitExceeded:
                    // The pending-cap release time is unknowable, so this 429
                    // intentionally has no Retry-After value.
                    return Error(429, ExternalResponseCode.LimitExceeded);
                case DomainErrorCode.Internal:
                case DomainErrorCode.RevisionCollision:
                case DomainErrorCode.DirectoryCapacity:
                default:
                    return Error(500, ExternalResponseCode.Internal);
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
            Registration = 3
        }
    }
}
