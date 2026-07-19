using System;
using System.Net;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    public enum ExternalNetworkBoundaryFailure
    {
        LocalEndpointUnavailable = 1,
        LocalEndpointMismatch = 2,
        RemoteEndpointUnavailable = 3
    }

    public interface IExternalSecurityAuditWriter
    {
        void WriteApiKeyRejected(
            Guid requestId,
            SecurityAuditOperation operation,
            IPAddress remoteAddress);

        void WriteNetworkBoundaryRejected(
            Guid requestId,
            SecurityAuditOperation operation,
            ExternalNetworkBoundaryFailure failure,
            IPAddress remoteAddress);
    }

    public sealed class ExternalSecurityAuditWriter
        : IExternalSecurityAuditWriter
    {
        private readonly SecurityAuditEventLogger _logger;

        public ExternalSecurityAuditWriter(SecurityAuditEventLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void WriteApiKeyRejected(
            Guid requestId,
            SecurityAuditOperation operation,
            IPAddress remoteAddress)
        {
            _logger.WriteFailure(
                SecurityAuditEventId.ExternalApiKeyRejected,
                SecurityAuditBoundary.External,
                operation,
                SecurityAuditReason.InvalidApiKey,
                requestId,
                null,
                remoteAddress);
        }

        public void WriteNetworkBoundaryRejected(
            Guid requestId,
            SecurityAuditOperation operation,
            ExternalNetworkBoundaryFailure failure,
            IPAddress remoteAddress)
        {
            SecurityAuditReason reason;
            switch (failure)
            {
                case ExternalNetworkBoundaryFailure.LocalEndpointUnavailable:
                    reason = SecurityAuditReason.LocalEndpointUnavailable;
                    break;
                case ExternalNetworkBoundaryFailure.LocalEndpointMismatch:
                    reason = SecurityAuditReason.LocalEndpointMismatch;
                    break;
                case ExternalNetworkBoundaryFailure.RemoteEndpointUnavailable:
                    reason = SecurityAuditReason.RemoteEndpointMismatch;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(failure));
            }

            _logger.WriteFailure(
                SecurityAuditEventId.NetworkBoundaryRejected,
                SecurityAuditBoundary.External,
                operation,
                reason,
                requestId,
                null,
                remoteAddress);
        }
    }
}
