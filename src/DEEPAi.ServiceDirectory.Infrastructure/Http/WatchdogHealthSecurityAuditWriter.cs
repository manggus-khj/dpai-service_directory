using System;
using System.Net;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal enum WatchdogHealthNetworkBoundaryFailure
    {
        LocalEndpointUnavailable = 1,
        LocalEndpointMismatch = 2,
        RemoteEndpointUnavailable = 3,
        RemoteEndpointNotLoopback = 4
    }

    internal interface IWatchdogHealthSecurityAuditWriter
    {
        void WriteApiKeyRejected(
            Guid requestId,
            IPAddress remoteAddress);

        void WriteNetworkBoundaryRejected(
            Guid requestId,
            WatchdogHealthNetworkBoundaryFailure failure,
            IPAddress remoteAddress);
    }

    internal sealed class WatchdogHealthSecurityAuditWriter
        : IWatchdogHealthSecurityAuditWriter
    {
        private readonly SecurityAuditEventLogger _logger;

        internal WatchdogHealthSecurityAuditWriter(
            SecurityAuditEventLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void WriteApiKeyRejected(
            Guid requestId,
            IPAddress remoteAddress)
        {
            _logger.WriteFailure(
                SecurityAuditEventId.ExternalApiKeyRejected,
                SecurityAuditBoundary.WatchdogHealth,
                SecurityAuditOperation.WatchdogHealth,
                SecurityAuditReason.InvalidApiKey,
                requestId,
                null,
                remoteAddress);
        }

        public void WriteNetworkBoundaryRejected(
            Guid requestId,
            WatchdogHealthNetworkBoundaryFailure failure,
            IPAddress remoteAddress)
        {
            SecurityAuditReason reason;
            switch (failure)
            {
                case WatchdogHealthNetworkBoundaryFailure
                    .LocalEndpointUnavailable:
                    reason = SecurityAuditReason.LocalEndpointUnavailable;
                    break;
                case WatchdogHealthNetworkBoundaryFailure
                    .LocalEndpointMismatch:
                    reason = SecurityAuditReason.LocalEndpointMismatch;
                    break;
                case WatchdogHealthNetworkBoundaryFailure
                    .RemoteEndpointUnavailable:
                    reason = SecurityAuditReason.RemoteEndpointMismatch;
                    break;
                case WatchdogHealthNetworkBoundaryFailure
                    .RemoteEndpointNotLoopback:
                    reason = SecurityAuditReason.RemoteEndpointNotLoopback;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(failure));
            }

            _logger.WriteFailure(
                SecurityAuditEventId.NetworkBoundaryRejected,
                SecurityAuditBoundary.WatchdogHealth,
                SecurityAuditOperation.WatchdogHealth,
                reason,
                requestId,
                null,
                remoteAddress);
        }
    }
}
