using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;
using DEEPAi.ServiceDirectory.Infrastructure.Protocol;
using DEEPAi.ServiceDirectory.Infrastructure.Security;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    // Transport-neutral boundary for the loopback Admin API. The
    // HttpListener host must copy the exact raw path and query from RawUrl
    // without decoding or normalizing them, and must supply the authenticated
    // request principal and actual local/remote endpoints. This adapter does
    // not own listener lifetime or the application mutation handler.
    public sealed class AdminHttpAdapter
    {
        private const string ServicesPath = "/admin/services";
        private const string RegistrationModePath =
            AdminApiContract.RegistrationModePath;
        private const string OpenRegistrationModePath =
            AdminApiContract.OpenRegistrationModePath;
        private const string CloseRegistrationModePath =
            AdminApiContract.CloseRegistrationModePath;
        private const string ServicePrefix = "/admin/services/";
        private const string SyncPath = "/admin/sync";
        private const string EnableSyncPath = "/admin/sync/enable";
        private const string ConfirmPairingPath =
            "/admin/sync/pairing/confirm";
        private const string CancelPairingPath =
            "/admin/sync/pairing/cancel";
        private const string DisableSyncPath = "/admin/sync/disable";
        private const string SyncNowPath = "/admin/sync/now";
        private const string LoggingSettingsPath =
            "/admin/settings/logging";
        private const string CaStatusPath = "/admin/ca/status";
        private const string CaBackupPath = "/admin/ca/backup";
        private const string CaRotationPath = AdminApiContract.CaRotationPath;
        private const string PrepareCaRotationPath =
            AdminApiContract.PrepareCaRotationPath;
        private const string CancelCaRotationPath =
            AdminApiContract.CancelCaRotationPath;
        private const string CertificatesPath = "/admin/certificates";
        private const string CertificatesPrefix = "/admin/certificates/";
        private const string RevokeSuffix = "/revoke";

        private readonly IAdminHttpRequestHandler _handler;
        private readonly IAdminAuthorizationEvaluator _authorizationEvaluator;
        private readonly IAdminSecurityAuditWriter _auditWriter;
        private readonly AdminRequestAdmissionController _admissionController;
        private readonly BoundedRequestBodyReader _bodyReader;

        public AdminHttpAdapter(
            IAdminHttpRequestHandler handler,
            SecurityAuditEventLogger securityAuditLogger)
            : this(
                handler,
                new SystemAdminAuthorizationEvaluator(
                    new AdminRequestAuthorizer()),
                new AdminSecurityAuditWriter(securityAuditLogger),
                new AdminRequestAdmissionController(),
                new BoundedRequestBodyReader())
        {
        }

        internal AdminHttpAdapter(
            IAdminHttpRequestHandler handler,
            IAdminAuthorizationEvaluator authorizationEvaluator,
            IAdminSecurityAuditWriter auditWriter,
            AdminRequestAdmissionController admissionController,
            BoundedRequestBodyReader bodyReader)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _authorizationEvaluator = authorizationEvaluator
                ?? throw new ArgumentNullException(
                    nameof(authorizationEvaluator));
            _auditWriter = auditWriter
                ?? throw new ArgumentNullException(nameof(auditWriter));
            _admissionController = admissionController
                ?? throw new ArgumentNullException(
                    nameof(admissionController));
            _bodyReader = bodyReader
                ?? throw new ArgumentNullException(nameof(bodyReader));
        }

        public AdminHttpResponseData Process(AdminHttpRequestData request)
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

        private AdminHttpResponseData ProcessCore(
            AdminHttpRequestData request)
        {
            Guid requestId = Guid.NewGuid();
            IPEndPoint localEndpoint = request.LocalEndpoint;
            IPEndPoint remoteEndpoint = request.RemoteEndpoint;
            IPAddress remoteAddress = remoteEndpoint == null
                ? null
                : remoteEndpoint.Address;

            AdminNetworkBoundaryFailure? boundaryFailure =
                GetBoundaryFailure(localEndpoint, remoteEndpoint);
            if (boundaryFailure.HasValue)
            {
                _auditWriter.WriteNetworkBoundaryRejected(
                    requestId,
                    boundaryFailure.Value,
                    remoteAddress);
                return AdminHttpResponseData.Bodyless(403);
            }

            AdminAuthorizationEvaluation authorization =
                _authorizationEvaluator.Evaluate(request.Principal);
            if (authorization == null)
            {
                _auditWriter.WriteAuthorizationRejected(
                    requestId,
                    SecurityAuditReason.AuthorizationCheckUnavailable,
                    null,
                    remoteAddress);
                return AdminHttpResponseData.Bodyless(403);
            }

            if (!authorization.IsAuthorized)
            {
                if (authorization.Status ==
                    AdminAuthorizationStatus.Unauthenticated)
                {
                    _auditWriter.WriteAuthenticationRejected(
                        requestId,
                        authorization.FailureReason.Value,
                        remoteAddress);
                    return AdminHttpResponseData.Bodyless(401);
                }

                _auditWriter.WriteAuthorizationRejected(
                    requestId,
                    authorization.FailureReason.Value,
                    authorization.ActorSid,
                    remoteAddress);
                return AdminHttpResponseData.Bodyless(403);
            }

            string routeValue;
            AdminHttpOperation operation = ResolveOperation(
                request.Method,
                request.AbsolutePath,
                out routeValue);
            if (operation == AdminHttpOperation.Undefined)
            {
                return AdminHttpResponseData.Bodyless(404);
            }

            AdminRequestAdmissionResult admission =
                _admissionController.TryAcquire(
                    operation,
                    authorization.ActorSid);
            if (!admission.IsGranted)
            {
                return AdminHttpResponseData.Error(
                    429,
                    AdminServerErrorCode.LimitExceeded,
                    admission.RetryAfterSeconds);
            }

            using (admission.Lease)
            {
                return ProcessAuthorized(
                    request,
                    operation,
                    routeValue);
            }
        }

        private AdminHttpResponseData ProcessAuthorized(
            AdminHttpRequestData request,
            AdminHttpOperation operation,
            string routeValue)
        {
            if (!string.IsNullOrEmpty(
                request.ContentEncodingHeaderValue))
            {
                return AdminHttpResponseData.Bodyless(415);
            }

            AdminServicesQuery servicesQuery = null;
            AdminCertificatesQuery certificatesQuery = null;
            if (operation == AdminHttpOperation.GetServices)
            {
                if (!AdminQueryStringParser.TryParseServices(
                    request.RawQuery,
                    out servicesQuery))
                {
                    return BadRequest();
                }
            }
            else if (operation == AdminHttpOperation.GetCertificates)
            {
                if (!AdminQueryStringParser.TryParseCertificates(
                    request.RawQuery,
                    out certificatesQuery))
                {
                    return BadRequest();
                }
            }
            else if (!AdminQueryStringParser.IsEmpty(request.RawQuery))
            {
                return BadRequest();
            }

            bool requiresXmlBody = RequiresXmlBody(operation);
            if (requiresXmlBody
                && !IsSupportedXmlContentType(request.ContentType))
            {
                return AdminHttpResponseData.Bodyless(415);
            }

            byte[] body;
            AdminHttpResponseData bodyFailure;
            if (!TryReadBody(request, out body, out bodyFailure))
            {
                return bodyFailure;
            }

            if (requiresXmlBody)
            {
                if (body.Length == 0)
                {
                    return BadRequest();
                }
            }
            else if (body.Length != 0)
            {
                return BadRequest();
            }

            AdminEnableSyncRequest enableSyncRequest = null;
            AdminPairingConfirmationRequest pairingConfirmationRequest = null;
            AdminPairingCancellationRequest pairingCancellationRequest = null;
            AdminDisableSyncRequest disableSyncRequest = null;
            AdminLoggingSettingsRequest loggingSettingsRequest = null;
            AdminCreateCaBackupRequest createCaBackupRequest = null;
            AdminCancelCaRotationRequest cancelCaRotationRequest = null;
            AdminRevokeCertificateRequest revokeCertificateRequest = null;
            try
            {
                switch (operation)
                {
                    case AdminHttpOperation.EnableSync:
                        enableSyncRequest =
                            AdminServerXmlCodec.ParseEnableSyncRequest(body);
                        break;
                    case AdminHttpOperation.ConfirmPairing:
                        pairingConfirmationRequest = AdminServerXmlCodec
                            .ParsePairingConfirmationRequest(body);
                        break;
                    case AdminHttpOperation.CancelPairing:
                        pairingCancellationRequest = AdminServerXmlCodec
                            .ParsePairingCancellationRequest(body);
                        break;
                    case AdminHttpOperation.DisableSync:
                        disableSyncRequest =
                            AdminServerXmlCodec.ParseDisableSyncRequest(body);
                        break;
                    case AdminHttpOperation.PutLoggingSettings:
                        loggingSettingsRequest = AdminServerXmlCodec
                            .ParseLoggingSettingsRequest(body);
                        break;
                    case AdminHttpOperation.CreateCaBackup:
                        try
                        {
                            createCaBackupRequest = AdminServerXmlCodec
                                .ParseCreateCaBackupRequest(body);
                        }
                        finally
                        {
                            Array.Clear(body, 0, body.Length);
                        }
                        break;
                    case AdminHttpOperation.RevokeCertificate:
                        revokeCertificateRequest = AdminServerXmlCodec
                            .ParseRevokeCertificateRequest(body);
                        break;
                    case AdminHttpOperation.PrepareCaRotation:
                        AdminServerXmlCodec.ParsePrepareCaRotationRequest(
                            body);
                        break;
                    case AdminHttpOperation.CancelCaRotation:
                        cancelCaRotationRequest = AdminServerXmlCodec
                            .ParseCancelCaRotationRequest(body);
                        break;
                }
            }
            catch (AdminProtocolException)
            {
                return BadRequest();
            }

            switch (operation)
            {
                case AdminHttpOperation.GetServices:
                    return Execute(
                        () => _handler.GetServices(servicesQuery),
                        AdminServerResponseXmlCodec.SerializeServicesResponse);
                case AdminHttpOperation.GetRegistrationMode:
                    return Execute(
                        _handler.GetRegistrationMode,
                        AdminRegistrationModeXmlCodec
                            .SerializeRegistrationModeResponse);
                case AdminHttpOperation.OpenRegistrationMode:
                    return Execute(
                        _handler.OpenRegistrationMode,
                        AdminRegistrationModeXmlCodec
                            .SerializeRegistrationModeResponse);
                case AdminHttpOperation.CloseRegistrationMode:
                    return Execute(
                        _handler.CloseRegistrationMode,
                        AdminRegistrationModeXmlCodec
                            .SerializeRegistrationModeResponse);
                case AdminHttpOperation.DeleteService:
                    return ProcessDelete(routeValue);
                case AdminHttpOperation.GetSyncStatus:
                    return Execute(
                        _handler.GetSyncStatus,
                        AdminServerResponseXmlCodec.SerializeSyncStatusResponse);
                case AdminHttpOperation.EnableSync:
                    return Execute(
                        () => _handler.EnableSync(enableSyncRequest),
                        AdminServerResponseXmlCodec.SerializeUnitResponse);
                case AdminHttpOperation.ConfirmPairing:
                    return Execute(
                        () => _handler.ConfirmPairing(
                            pairingConfirmationRequest),
                        AdminServerResponseXmlCodec.SerializeUnitResponse);
                case AdminHttpOperation.CancelPairing:
                    return Execute(
                        () => _handler.CancelPairing(
                            pairingCancellationRequest),
                        AdminServerResponseXmlCodec.SerializeUnitResponse);
                case AdminHttpOperation.DisableSync:
                    return Execute(
                        () => _handler.DisableSync(disableSyncRequest),
                        AdminServerResponseXmlCodec
                            .SerializeSyncDisableResponse);
                case AdminHttpOperation.SynchronizeNow:
                    return Execute(
                        _handler.SynchronizeNow,
                        AdminServerResponseXmlCodec.SerializeUnitResponse);
                case AdminHttpOperation.GetLoggingSettings:
                    return Execute(
                        _handler.GetLoggingSettings,
                        AdminServerResponseXmlCodec.SerializeLoggingResponse);
                case AdminHttpOperation.PutLoggingSettings:
                    return Execute(
                        () => _handler.PutLoggingSettings(
                            loggingSettingsRequest),
                        AdminServerResponseXmlCodec.SerializeLoggingResponse);
                case AdminHttpOperation.GetCaStatus:
                    return Execute(
                        _handler.GetCaStatus,
                        AdminServerResponseXmlCodec.SerializeCaStatusResponse);
                case AdminHttpOperation.CreateCaBackup:
                    return Execute(
                        () => _handler.CreateCaBackup(createCaBackupRequest),
                        AdminServerResponseXmlCodec.SerializeCaBackupResponse);
                case AdminHttpOperation.GetCaRotation:
                    return Execute(
                        _handler.GetCaRotation,
                        AdminServerResponseXmlCodec
                            .SerializeCaRotationResponse);
                case AdminHttpOperation.PrepareCaRotation:
                    return Execute(
                        _handler.PrepareCaRotation,
                        AdminServerResponseXmlCodec
                            .SerializeCaRotationResponse);
                case AdminHttpOperation.CancelCaRotation:
                    return Execute(
                        () => _handler.CancelCaRotation(
                            cancelCaRotationRequest),
                        AdminServerResponseXmlCodec
                            .SerializeCaRotationResponse);
                case AdminHttpOperation.GetCertificates:
                    return Execute(
                        () => _handler.GetCertificates(certificatesQuery),
                        AdminServerResponseXmlCodec
                            .SerializeCertificatesResponse);
                case AdminHttpOperation.RevokeCertificate:
                    return ProcessCertificateRevocation(
                        routeValue,
                        revokeCertificateRequest);
                default:
                    return InternalError();
            }
        }

        private AdminHttpResponseData ProcessDelete(string routeValue)
        {
            ProductCode productCode;
            if (!ProductCode.TryCreate(routeValue, out productCode)
                || !StringComparer.Ordinal.Equals(
                    routeValue,
                    productCode.Value))
            {
                return BadRequest();
            }

            return Execute(
                () => _handler.DeleteService(productCode.Value),
                AdminServerResponseXmlCodec.SerializeUnitResponse);
        }

        private AdminHttpResponseData ProcessCertificateRevocation(
            string routeValue,
            AdminRevokeCertificateRequest request)
        {
            CertificateSerialNumber serialNumber;
            if (!CertificateSerialNumber.TryCreate(
                    routeValue,
                    out serialNumber))
            {
                return BadRequest();
            }

            return Execute(
                () => _handler.RevokeCertificate(
                    serialNumber.Hex,
                    request),
                AdminServerResponseXmlCodec
                    .SerializeCertificateRevocationResponse);
        }

        private AdminHttpResponseData Execute<T>(
            Func<AdminHandlerResult<T>> action,
            Func<T, byte[]> serializer)
            where T : class
        {
            AdminHandlerResult<T> result = action();
            if (result == null)
            {
                return InternalError();
            }

            if (!result.IsSuccess)
            {
                return MapHandlerError(result.ErrorCode.Value);
            }

            return AdminHttpResponseData.Xml(
                200,
                serializer(result.Value));
        }

        private bool TryReadBody(
            AdminHttpRequestData request,
            out byte[] body,
            out AdminHttpResponseData failure)
        {
            body = null;
            failure = null;
            if (request.DeclaredContentLength >
                AdminApiContract.MaximumBodyBytes)
            {
                failure = AdminHttpResponseData.Bodyless(413);
                return false;
            }

            if (request.BodyStream == null)
            {
                if (request.DeclaredContentLength <= 0)
                {
                    body = new byte[0];
                    return true;
                }

                failure = BadRequest();
                return false;
            }

            if (!request.BodyStream.CanRead)
            {
                failure = BadRequest();
                return false;
            }

            BoundedBodyReadResult result = _bodyReader.ReadStandard(
                request.BodyStream,
                request.DeclaredContentLength);
            if (!result.IsSuccess)
            {
                failure = MapBodyFailure(result.FailureCode);
                return false;
            }

            body = CopyBody(result.Body);
            return true;
        }

        private static byte[] CopyBody(BoundedRequestBody body)
        {
            var copy = new byte[body.Length];
            using (Stream source = body.OpenRead())
            {
                int offset = 0;
                while (offset < copy.Length)
                {
                    int read = source.Read(
                        copy,
                        offset,
                        copy.Length - offset);
                    if (read == 0)
                    {
                        throw new InvalidOperationException(
                            "The bounded Admin body ended unexpectedly.");
                    }

                    offset += read;
                }
            }

            return copy;
        }

        private static AdminHttpResponseData MapBodyFailure(
            BoundedBodyReadFailureCode failureCode)
        {
            switch (failureCode)
            {
                case BoundedBodyReadFailureCode.DeclaredLengthTooLarge:
                case BoundedBodyReadFailureCode.ActualLengthTooLarge:
                    return AdminHttpResponseData.Bodyless(413);
                case BoundedBodyReadFailureCode.DeclaredLengthMismatch:
                    return BadRequest();
                case BoundedBodyReadFailureCode.IoFailure:
                case BoundedBodyReadFailureCode.None:
                default:
                    return InternalError();
            }
        }

        private static AdminHttpResponseData MapHandlerError(
            AdminServerErrorCode code)
        {
            switch (code)
            {
                case AdminServerErrorCode.BadRequest:
                    return AdminHttpResponseData.Error(400, code);
                case AdminServerErrorCode.NotFound:
                    return AdminHttpResponseData.Error(404, code);
                case AdminServerErrorCode.Conflict:
                case AdminServerErrorCode.PeerMismatch:
                case AdminServerErrorCode.SyncDisabled:
                case AdminServerErrorCode.RevisionCollision:
                case AdminServerErrorCode.DirectoryCapacity:
                case AdminServerErrorCode.LogicalClockExhausted:
                    return AdminHttpResponseData.Error(409, code);
                case AdminServerErrorCode.LimitExceeded:
                    return AdminHttpResponseData.Error(429, code);
                case AdminServerErrorCode.Internal:
                    return InternalError();
                case AdminServerErrorCode.NotPeer:
                case AdminServerErrorCode.ClockSkew:
                default:
                    // 401 and 403 Admin responses must be bodyless. A domain
                    // handler therefore cannot safely surface Peer-only codes.
                    return InternalError();
            }
        }

        private static AdminNetworkBoundaryFailure? GetBoundaryFailure(
            IPEndPoint localEndpoint,
            IPEndPoint remoteEndpoint)
        {
            if (localEndpoint == null || localEndpoint.Address == null)
            {
                return AdminNetworkBoundaryFailure
                    .LocalEndpointUnavailable;
            }

            if (localEndpoint.Port != ServiceDirectoryListenerAddress.Port
                || !localEndpoint.Address.Equals(IPAddress.Loopback))
            {
                return AdminNetworkBoundaryFailure.LocalEndpointMismatch;
            }

            if (remoteEndpoint == null
                || remoteEndpoint.Address == null
                || !IsSupportedAddress(remoteEndpoint.Address)
                || !IPAddress.IsLoopback(remoteEndpoint.Address)
                || !HttpRequestEndpointGuard.IsLoopbackScopeAllowed(
                    localEndpoint,
                    remoteEndpoint))
            {
                return AdminNetworkBoundaryFailure
                    .RemoteEndpointNotLoopback;
            }

            return null;
        }

        private static bool IsSupportedAddress(IPAddress address)
        {
            return address.AddressFamily == AddressFamily.InterNetwork
                || address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        private static AdminHttpOperation ResolveOperation(
            string method,
            string path,
            out string routeValue)
        {
            routeValue = null;
            if (StringComparer.Ordinal.Equals(method, "GET")
                && StringComparer.Ordinal.Equals(path, ServicesPath))
            {
                return AdminHttpOperation.GetServices;
            }

            if (StringComparer.Ordinal.Equals(method, "GET")
                && StringComparer.Ordinal.Equals(
                    path,
                    RegistrationModePath))
            {
                return AdminHttpOperation.GetRegistrationMode;
            }

            if (StringComparer.Ordinal.Equals(method, "POST")
                && StringComparer.Ordinal.Equals(
                    path,
                    OpenRegistrationModePath))
            {
                return AdminHttpOperation.OpenRegistrationMode;
            }

            if (StringComparer.Ordinal.Equals(method, "POST")
                && StringComparer.Ordinal.Equals(
                    path,
                    CloseRegistrationModePath))
            {
                return AdminHttpOperation.CloseRegistrationMode;
            }

            if (StringComparer.Ordinal.Equals(method, "DELETE")
                && path != null
                && path.StartsWith(
                    ServicePrefix,
                    StringComparison.Ordinal))
            {
                routeValue = path.Substring(ServicePrefix.Length);
                return AdminHttpOperation.DeleteService;
            }

            if (StringComparer.Ordinal.Equals(method, "GET")
                && StringComparer.Ordinal.Equals(path, SyncPath))
            {
                return AdminHttpOperation.GetSyncStatus;
            }

            if (StringComparer.Ordinal.Equals(method, "POST")
                && StringComparer.Ordinal.Equals(path, EnableSyncPath))
            {
                return AdminHttpOperation.EnableSync;
            }

            if (StringComparer.Ordinal.Equals(method, "POST")
                && StringComparer.Ordinal.Equals(
                    path,
                    ConfirmPairingPath))
            {
                return AdminHttpOperation.ConfirmPairing;
            }

            if (StringComparer.Ordinal.Equals(method, "POST")
                && StringComparer.Ordinal.Equals(
                    path,
                    CancelPairingPath))
            {
                return AdminHttpOperation.CancelPairing;
            }

            if (StringComparer.Ordinal.Equals(method, "POST")
                && StringComparer.Ordinal.Equals(path, DisableSyncPath))
            {
                return AdminHttpOperation.DisableSync;
            }

            if (StringComparer.Ordinal.Equals(method, "POST")
                && StringComparer.Ordinal.Equals(path, SyncNowPath))
            {
                return AdminHttpOperation.SynchronizeNow;
            }

            if (StringComparer.Ordinal.Equals(method, "GET")
                && StringComparer.Ordinal.Equals(
                    path,
                    LoggingSettingsPath))
            {
                return AdminHttpOperation.GetLoggingSettings;
            }

            if (StringComparer.Ordinal.Equals(method, "PUT")
                && StringComparer.Ordinal.Equals(
                    path,
                    LoggingSettingsPath))
            {
                return AdminHttpOperation.PutLoggingSettings;
            }

            if (StringComparer.Ordinal.Equals(method, "GET")
                && StringComparer.Ordinal.Equals(path, CaStatusPath))
            {
                return AdminHttpOperation.GetCaStatus;
            }

            if (StringComparer.Ordinal.Equals(method, "POST")
                && StringComparer.Ordinal.Equals(path, CaBackupPath))
            {
                return AdminHttpOperation.CreateCaBackup;
            }

            if (StringComparer.Ordinal.Equals(method, "GET")
                && StringComparer.Ordinal.Equals(path, CaRotationPath))
            {
                return AdminHttpOperation.GetCaRotation;
            }

            if (StringComparer.Ordinal.Equals(method, "POST")
                && StringComparer.Ordinal.Equals(
                    path,
                    PrepareCaRotationPath))
            {
                return AdminHttpOperation.PrepareCaRotation;
            }

            if (StringComparer.Ordinal.Equals(method, "POST")
                && StringComparer.Ordinal.Equals(
                    path,
                    CancelCaRotationPath))
            {
                return AdminHttpOperation.CancelCaRotation;
            }

            if (StringComparer.Ordinal.Equals(method, "GET")
                && StringComparer.Ordinal.Equals(path, CertificatesPath))
            {
                return AdminHttpOperation.GetCertificates;
            }

            if (StringComparer.Ordinal.Equals(method, "POST")
                && TryExtractBetween(
                    path,
                    CertificatesPrefix,
                    RevokeSuffix,
                    out routeValue))
            {
                return AdminHttpOperation.RevokeCertificate;
            }

            return AdminHttpOperation.Undefined;
        }

        private static bool TryExtractBetween(
            string value,
            string prefix,
            string suffix,
            out string middle)
        {
            middle = null;
            if (value == null
                || !value.StartsWith(prefix, StringComparison.Ordinal)
                || !value.EndsWith(suffix, StringComparison.Ordinal)
                || value.Length < prefix.Length + suffix.Length)
            {
                return false;
            }

            middle = value.Substring(
                prefix.Length,
                value.Length - prefix.Length - suffix.Length);
            return true;
        }

        private static bool RequiresXmlBody(AdminHttpOperation operation)
        {
            return operation == AdminHttpOperation.EnableSync
                || operation == AdminHttpOperation.ConfirmPairing
                || operation == AdminHttpOperation.CancelPairing
                || operation == AdminHttpOperation.DisableSync
                || operation == AdminHttpOperation.PutLoggingSettings
                || operation == AdminHttpOperation.CreateCaBackup
                || operation == AdminHttpOperation.RevokeCertificate
                || operation == AdminHttpOperation.PrepareCaRotation
                || operation == AdminHttpOperation.CancelCaRotation;
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

        private static AdminHttpResponseData BadRequest()
        {
            return AdminHttpResponseData.Error(
                400,
                AdminServerErrorCode.BadRequest);
        }

        private static AdminHttpResponseData InternalError()
        {
            return AdminHttpResponseData.Error(
                500,
                AdminServerErrorCode.Internal);
        }
    }
}
