using System;
using System.Net;
using System.Security;
using System.Security.Principal;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.Security;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal enum AdminNetworkBoundaryFailure
    {
        LocalEndpointUnavailable = 1,
        LocalEndpointMismatch = 2,
        RemoteEndpointNotLoopback = 3
    }

    internal interface IAdminSecurityAuditWriter
    {
        void WriteNetworkBoundaryRejected(
            Guid requestId,
            AdminNetworkBoundaryFailure failure,
            IPAddress remoteAddress);

        void WriteAuthenticationRejected(
            Guid requestId,
            SecurityAuditReason reason,
            IPAddress remoteAddress);

        void WriteAuthorizationRejected(
            Guid requestId,
            SecurityAuditReason reason,
            SecurityIdentifier actorSid,
            IPAddress remoteAddress);
    }

    internal sealed class AdminSecurityAuditWriter
        : IAdminSecurityAuditWriter
    {
        private readonly SecurityAuditEventLogger _logger;

        public AdminSecurityAuditWriter(SecurityAuditEventLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void WriteNetworkBoundaryRejected(
            Guid requestId,
            AdminNetworkBoundaryFailure failure,
            IPAddress remoteAddress)
        {
            SecurityAuditReason reason;
            switch (failure)
            {
                case AdminNetworkBoundaryFailure.LocalEndpointUnavailable:
                    reason = SecurityAuditReason.LocalEndpointUnavailable;
                    break;
                case AdminNetworkBoundaryFailure.LocalEndpointMismatch:
                    reason = SecurityAuditReason.LocalEndpointMismatch;
                    break;
                case AdminNetworkBoundaryFailure.RemoteEndpointNotLoopback:
                    reason = SecurityAuditReason.RemoteEndpointNotLoopback;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(failure));
            }

            _logger.WriteFailure(
                SecurityAuditEventId.NetworkBoundaryRejected,
                SecurityAuditBoundary.Admin,
                SecurityAuditOperation.AdminRequest,
                reason,
                requestId,
                null,
                remoteAddress);
        }

        public void WriteAuthenticationRejected(
            Guid requestId,
            SecurityAuditReason reason,
            IPAddress remoteAddress)
        {
            _logger.WriteFailure(
                SecurityAuditEventId.AdminAuthenticationRejected,
                SecurityAuditBoundary.Admin,
                SecurityAuditOperation.AdminRequest,
                reason,
                requestId,
                null,
                remoteAddress);
        }

        public void WriteAuthorizationRejected(
            Guid requestId,
            SecurityAuditReason reason,
            SecurityIdentifier actorSid,
            IPAddress remoteAddress)
        {
            _logger.WriteFailure(
                SecurityAuditEventId.AdminAuthorizationRejected,
                SecurityAuditBoundary.Admin,
                SecurityAuditOperation.AdminRequest,
                reason,
                requestId,
                actorSid,
                remoteAddress);
        }
    }

    internal sealed class AdminAuthorizationEvaluation
    {
        private AdminAuthorizationEvaluation(
            AdminAuthorizationStatus status,
            SecurityAuditReason? failureReason,
            SecurityIdentifier actorSid)
        {
            bool authorized = status == AdminAuthorizationStatus.Authorized;
            if (authorized != (!failureReason.HasValue && actorSid != null))
            {
                throw new ArgumentException(
                    "The Admin authorization evaluation shape is invalid.");
            }

            if (!authorized && !failureReason.HasValue)
            {
                throw new ArgumentException(
                    "A rejected Admin authorization evaluation needs a reason.");
            }

            Status = status;
            FailureReason = failureReason;
            ActorSid = actorSid;
        }

        public AdminAuthorizationStatus Status { get; }

        public SecurityAuditReason? FailureReason { get; }

        public SecurityIdentifier ActorSid { get; }

        public bool IsAuthorized =>
            Status == AdminAuthorizationStatus.Authorized;

        public static AdminAuthorizationEvaluation Authorized(
            SecurityIdentifier actorSid)
        {
            if (actorSid == null)
            {
                throw new ArgumentNullException(nameof(actorSid));
            }

            return new AdminAuthorizationEvaluation(
                AdminAuthorizationStatus.Authorized,
                null,
                actorSid);
        }

        public static AdminAuthorizationEvaluation Unauthenticated(
            SecurityAuditReason reason)
        {
            if (reason != SecurityAuditReason.Unauthenticated
                && reason != SecurityAuditReason.InvalidWindowsIdentity)
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }

            return new AdminAuthorizationEvaluation(
                AdminAuthorizationStatus.Unauthenticated,
                reason,
                null);
        }

        public static AdminAuthorizationEvaluation Forbidden(
            SecurityIdentifier actorSid)
        {
            if (actorSid == null)
            {
                throw new ArgumentNullException(nameof(actorSid));
            }

            return new AdminAuthorizationEvaluation(
                AdminAuthorizationStatus.Forbidden,
                SecurityAuditReason.NotInOperatorsGroup,
                actorSid);
        }

        public static AdminAuthorizationEvaluation Unavailable()
        {
            return new AdminAuthorizationEvaluation(
                AdminAuthorizationStatus.Unavailable,
                SecurityAuditReason.AuthorizationCheckUnavailable,
                null);
        }
    }

    internal interface IAdminAuthorizationEvaluator
    {
        AdminAuthorizationEvaluation Evaluate(IPrincipal principal);
    }

    internal sealed class SystemAdminAuthorizationEvaluator
        : IAdminAuthorizationEvaluator
    {
        private readonly AdminRequestAuthorizer _authorizer;

        public SystemAdminAuthorizationEvaluator(
            AdminRequestAuthorizer authorizer)
        {
            _authorizer = authorizer
                ?? throw new ArgumentNullException(nameof(authorizer));
        }

        public AdminAuthorizationEvaluation Evaluate(IPrincipal principal)
        {
            if (principal == null
                || principal.Identity == null
                || !principal.Identity.IsAuthenticated)
            {
                return AdminAuthorizationEvaluation.Unauthenticated(
                    SecurityAuditReason.Unauthenticated);
            }

            var windowsIdentity = principal.Identity as WindowsIdentity;
            if (windowsIdentity == null)
            {
                return AdminAuthorizationEvaluation.Unauthenticated(
                    SecurityAuditReason.InvalidWindowsIdentity);
            }

            AdminAuthorizationResult result = _authorizer.Authorize(principal);
            if (result.Status == AdminAuthorizationStatus.Unauthenticated)
            {
                return AdminAuthorizationEvaluation.Unauthenticated(
                    SecurityAuditReason.InvalidWindowsIdentity);
            }

            if (result.Status == AdminAuthorizationStatus.Unavailable)
            {
                return AdminAuthorizationEvaluation.Unavailable();
            }

            SecurityIdentifier actorSid;
            try
            {
                actorSid = windowsIdentity.User;
            }
            catch (SecurityException)
            {
                return AdminAuthorizationEvaluation.Unavailable();
            }

            if (actorSid == null)
            {
                return AdminAuthorizationEvaluation.Unavailable();
            }

            return result.IsAuthorized
                ? AdminAuthorizationEvaluation.Authorized(actorSid)
                : AdminAuthorizationEvaluation.Forbidden(actorSid);
        }
    }
}
