using System;

namespace DEEPAi.ServiceDirectory.Infrastructure.Logging
{
    public enum SecurityAuditEventId
    {
        ExternalApiKeyRejected = 4101,
        AdminAuthenticationRejected = 4102,
        AdminAuthorizationRejected = 4103,
        PeerAuthenticationRejected = 4104,
        PipeAuthorizationRejected = 4105,
        NetworkBoundaryRejected = 4106
    }

    public enum SecurityAuditBoundary
    {
        External = 1,
        WatchdogHealth = 2,
        Admin = 3,
        Peer = 4,
        NamedPipe = 5
    }

    public enum SecurityAuditOperation
    {
        ExternalHealth = 1,
        ExternalServiceLookup = 2,
        ExternalRegistration = 3,
        WatchdogHealth = 4,
        AdminRequest = 5,
        PeerPairingHello = 6,
        PeerPairingKeyConfirm = 7,
        PeerPairingDecision = 8,
        PeerPairingCommit = 9,
        PeerHandshake = 10,
        PeerExchange = 11,
        PeerRelease = 12,
        PeerRevoke = 13,
        PipeConnect = 14,
        ExternalUnknown = 15
    }

    public enum SecurityAuditReason
    {
        InvalidApiKey = 1,
        Unauthenticated = 2,
        InvalidWindowsIdentity = 3,
        NotInOperatorsGroup = 4,
        AuthorizationCheckUnavailable = 5,
        AuthenticationDataMissingOrMalformed = 6,
        PeerBindingMismatch = 7,
        KeyEpochMismatch = 8,
        SignatureInvalid = 9,
        TimestampOutsideAllowedWindow = 10,
        NonceReplay = 11,
        SessionInvalid = 12,
        ClientTokenUnavailable = 13,
        ClientNotAuthorized = 14,
        LocalEndpointUnavailable = 15,
        LocalEndpointMismatch = 16,
        RemoteEndpointNotLoopback = 17,
        RemoteEndpointMismatch = 18
    }

    internal static class SecurityAuditContract
    {
        public static void Validate(
            SecurityAuditEventId eventId,
            SecurityAuditBoundary boundary,
            SecurityAuditOperation operation,
            SecurityAuditReason reason)
        {
            if (!IsValidCombination(eventId, boundary, operation, reason))
            {
                throw new ArgumentException(
                    "The security audit event, boundary, operation, and reason combination is invalid.");
            }
        }

        public static string FormatEvent(SecurityAuditEventId eventId)
        {
            switch (eventId)
            {
                case SecurityAuditEventId.ExternalApiKeyRejected:
                    return "EXTERNAL_API_KEY_REJECTED";
                case SecurityAuditEventId.AdminAuthenticationRejected:
                    return "ADMIN_AUTHENTICATION_REJECTED";
                case SecurityAuditEventId.AdminAuthorizationRejected:
                    return "ADMIN_AUTHORIZATION_REJECTED";
                case SecurityAuditEventId.PeerAuthenticationRejected:
                    return "PEER_AUTHENTICATION_REJECTED";
                case SecurityAuditEventId.PipeAuthorizationRejected:
                    return "PIPE_AUTHORIZATION_REJECTED";
                case SecurityAuditEventId.NetworkBoundaryRejected:
                    return "NETWORK_BOUNDARY_REJECTED";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(eventId),
                        eventId,
                        "A defined security audit event is required.");
            }
        }

        public static string FormatBoundary(SecurityAuditBoundary boundary)
        {
            switch (boundary)
            {
                case SecurityAuditBoundary.External:
                    return "EXTERNAL";
                case SecurityAuditBoundary.WatchdogHealth:
                    return "WATCHDOG_HEALTH";
                case SecurityAuditBoundary.Admin:
                    return "ADMIN";
                case SecurityAuditBoundary.Peer:
                    return "PEER";
                case SecurityAuditBoundary.NamedPipe:
                    return "NAMED_PIPE";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(boundary),
                        boundary,
                        "A defined security audit boundary is required.");
            }
        }

        public static string FormatOperation(SecurityAuditOperation operation)
        {
            switch (operation)
            {
                case SecurityAuditOperation.ExternalHealth:
                    return "EXTERNAL_HEALTH";
                case SecurityAuditOperation.ExternalServiceLookup:
                    return "EXTERNAL_SERVICE_LOOKUP";
                case SecurityAuditOperation.ExternalRegistration:
                    return "EXTERNAL_REGISTRATION";
                case SecurityAuditOperation.WatchdogHealth:
                    return "WATCHDOG_HEALTH";
                case SecurityAuditOperation.AdminRequest:
                    return "ADMIN_REQUEST";
                case SecurityAuditOperation.PeerPairingHello:
                    return "PEER_PAIRING_HELLO";
                case SecurityAuditOperation.PeerPairingKeyConfirm:
                    return "PEER_PAIRING_KEY_CONFIRM";
                case SecurityAuditOperation.PeerPairingDecision:
                    return "PEER_PAIRING_DECISION";
                case SecurityAuditOperation.PeerPairingCommit:
                    return "PEER_PAIRING_COMMIT";
                case SecurityAuditOperation.PeerHandshake:
                    return "PEER_HANDSHAKE";
                case SecurityAuditOperation.PeerExchange:
                    return "PEER_EXCHANGE";
                case SecurityAuditOperation.PeerRelease:
                    return "PEER_RELEASE";
                case SecurityAuditOperation.PeerRevoke:
                    return "PEER_REVOKE";
                case SecurityAuditOperation.PipeConnect:
                    return "PIPE_CONNECT";
                case SecurityAuditOperation.ExternalUnknown:
                    return "EXTERNAL_UNKNOWN";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(operation),
                        operation,
                        "A defined security audit operation is required.");
            }
        }

        public static string FormatReason(SecurityAuditReason reason)
        {
            switch (reason)
            {
                case SecurityAuditReason.InvalidApiKey:
                    return "INVALID_API_KEY";
                case SecurityAuditReason.Unauthenticated:
                    return "UNAUTHENTICATED";
                case SecurityAuditReason.InvalidWindowsIdentity:
                    return "INVALID_WINDOWS_IDENTITY";
                case SecurityAuditReason.NotInOperatorsGroup:
                    return "NOT_IN_OPERATORS_GROUP";
                case SecurityAuditReason.AuthorizationCheckUnavailable:
                    return "AUTHORIZATION_CHECK_UNAVAILABLE";
                case SecurityAuditReason.AuthenticationDataMissingOrMalformed:
                    return "AUTHENTICATION_DATA_MISSING_OR_MALFORMED";
                case SecurityAuditReason.PeerBindingMismatch:
                    return "PEER_BINDING_MISMATCH";
                case SecurityAuditReason.KeyEpochMismatch:
                    return "KEY_EPOCH_MISMATCH";
                case SecurityAuditReason.SignatureInvalid:
                    return "SIGNATURE_INVALID";
                case SecurityAuditReason.TimestampOutsideAllowedWindow:
                    return "TIMESTAMP_OUTSIDE_ALLOWED_WINDOW";
                case SecurityAuditReason.NonceReplay:
                    return "NONCE_REPLAY";
                case SecurityAuditReason.SessionInvalid:
                    return "SESSION_INVALID";
                case SecurityAuditReason.ClientTokenUnavailable:
                    return "CLIENT_TOKEN_UNAVAILABLE";
                case SecurityAuditReason.ClientNotAuthorized:
                    return "CLIENT_NOT_AUTHORIZED";
                case SecurityAuditReason.LocalEndpointUnavailable:
                    return "LOCAL_ENDPOINT_UNAVAILABLE";
                case SecurityAuditReason.LocalEndpointMismatch:
                    return "LOCAL_ENDPOINT_MISMATCH";
                case SecurityAuditReason.RemoteEndpointNotLoopback:
                    return "REMOTE_ENDPOINT_NOT_LOOPBACK";
                case SecurityAuditReason.RemoteEndpointMismatch:
                    return "REMOTE_ENDPOINT_MISMATCH";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(reason),
                        reason,
                        "A defined security audit reason is required.");
            }
        }

        private static bool IsValidCombination(
            SecurityAuditEventId eventId,
            SecurityAuditBoundary boundary,
            SecurityAuditOperation operation,
            SecurityAuditReason reason)
        {
            switch (eventId)
            {
                case SecurityAuditEventId.ExternalApiKeyRejected:
                    return IsExternalBoundaryOperation(boundary, operation)
                        && reason == SecurityAuditReason.InvalidApiKey;

                case SecurityAuditEventId.AdminAuthenticationRejected:
                    return boundary == SecurityAuditBoundary.Admin
                        && operation == SecurityAuditOperation.AdminRequest
                        && (reason == SecurityAuditReason.Unauthenticated
                            || reason == SecurityAuditReason.InvalidWindowsIdentity);

                case SecurityAuditEventId.AdminAuthorizationRejected:
                    return boundary == SecurityAuditBoundary.Admin
                        && operation == SecurityAuditOperation.AdminRequest
                        && (reason == SecurityAuditReason.NotInOperatorsGroup
                            || reason == SecurityAuditReason.AuthorizationCheckUnavailable);

                case SecurityAuditEventId.PeerAuthenticationRejected:
                    return boundary == SecurityAuditBoundary.Peer
                        && IsAuthenticatedPeerOperation(operation)
                        && IsPeerAuthenticationReason(reason);

                case SecurityAuditEventId.PipeAuthorizationRejected:
                    return boundary == SecurityAuditBoundary.NamedPipe
                        && operation == SecurityAuditOperation.PipeConnect
                        && (reason == SecurityAuditReason.ClientTokenUnavailable
                            || reason == SecurityAuditReason.ClientNotAuthorized);

                case SecurityAuditEventId.NetworkBoundaryRejected:
                    return IsHttpBoundaryOperation(boundary, operation)
                        && IsNetworkBoundaryReason(reason);

                default:
                    return false;
            }
        }

        private static bool IsExternalBoundaryOperation(
            SecurityAuditBoundary boundary,
            SecurityAuditOperation operation)
        {
            if (boundary == SecurityAuditBoundary.WatchdogHealth)
            {
                return operation == SecurityAuditOperation.WatchdogHealth;
            }

            return boundary == SecurityAuditBoundary.External
                && (operation == SecurityAuditOperation.ExternalHealth
                    || operation == SecurityAuditOperation.ExternalServiceLookup
                    || operation == SecurityAuditOperation.ExternalRegistration
                    || operation == SecurityAuditOperation.ExternalUnknown);
        }

        private static bool IsHttpBoundaryOperation(
            SecurityAuditBoundary boundary,
            SecurityAuditOperation operation)
        {
            switch (boundary)
            {
                case SecurityAuditBoundary.External:
                case SecurityAuditBoundary.WatchdogHealth:
                    return IsExternalBoundaryOperation(boundary, operation);
                case SecurityAuditBoundary.Admin:
                    return operation == SecurityAuditOperation.AdminRequest;
                case SecurityAuditBoundary.Peer:
                    return operation == SecurityAuditOperation.PeerPairingHello
                        || IsAuthenticatedPeerOperation(operation);
                default:
                    return false;
            }
        }

        private static bool IsAuthenticatedPeerOperation(
            SecurityAuditOperation operation)
        {
            return operation == SecurityAuditOperation.PeerPairingKeyConfirm
                || operation == SecurityAuditOperation.PeerPairingDecision
                || operation == SecurityAuditOperation.PeerPairingCommit
                || operation == SecurityAuditOperation.PeerHandshake
                || operation == SecurityAuditOperation.PeerExchange
                || operation == SecurityAuditOperation.PeerRelease
                || operation == SecurityAuditOperation.PeerRevoke;
        }

        private static bool IsPeerAuthenticationReason(SecurityAuditReason reason)
        {
            return reason == SecurityAuditReason.AuthenticationDataMissingOrMalformed
                || reason == SecurityAuditReason.PeerBindingMismatch
                || reason == SecurityAuditReason.KeyEpochMismatch
                || reason == SecurityAuditReason.SignatureInvalid
                || reason == SecurityAuditReason.TimestampOutsideAllowedWindow
                || reason == SecurityAuditReason.NonceReplay
                || reason == SecurityAuditReason.SessionInvalid;
        }

        private static bool IsNetworkBoundaryReason(SecurityAuditReason reason)
        {
            return reason == SecurityAuditReason.LocalEndpointUnavailable
                || reason == SecurityAuditReason.LocalEndpointMismatch
                || reason == SecurityAuditReason.RemoteEndpointNotLoopback
                || reason == SecurityAuditReason.RemoteEndpointMismatch;
        }
    }
}
