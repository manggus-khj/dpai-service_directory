using System;
using System.Net;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public sealed partial class PeerSynchronizationController
    {
        private const string NormalDispatchPairingPathPrefix =
            "/api/sync/pairing/";

        public PeerHttpResponseData Process(PeerHttpHandlerRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.AbsolutePath.StartsWith(
                    NormalDispatchPairingPathPrefix,
                    StringComparison.Ordinal))
            {
                return ProcessPairingRequest(request);
            }

            PeerInboundOperation operation;
            if (!TryResolveNormalOperation(
                    request.AbsolutePath,
                    out operation))
            {
                return PeerHttpResponseData.Bodyless(404);
            }

            lock (_gate)
            {
                ThrowIfDisposed();
                return ProcessNormalRequestLocked(request, operation);
            }
        }

        private PeerHttpResponseData ProcessNormalRequestLocked(
            PeerHttpHandlerRequest request,
            PeerInboundOperation operation)
        {
            if (!HasAuthenticationContextLocked(operation))
            {
                WriteAuthenticationFailure(
                    request,
                    operation,
                    SecurityAuditReason.SessionInvalid);
                return PeerHttpResponseData.Bodyless(401);
            }

            byte[] body = null;
            try
            {
                body = request.GetBody();
                var input = new PeerInboundRequestData(
                    operation,
                    CreateAuthenticationHeaders(request),
                    request.Method,
                    PeerCanonicalRequestTarget.Create(
                        request.AbsolutePath,
                        null),
                    request.ContentType,
                    body,
                    request.ReceivedAtUtc);
                PeerInboundRequestDecision decision =
                    operation == PeerInboundOperation.Handshake
                        || operation == PeerInboundOperation.Revoke
                    ? _inboundAuthentication
                        .AuthenticatePairBoundRequest(
                            input,
                            _pairAuthentication)
                    : _inboundAuthentication
                        .AuthenticateSessionBoundRequest(
                            input,
                            _activeSession);

                if (decision.AuditInput != null)
                {
                    WriteAuthenticationFailure(
                        request,
                        decision.AuditInput);
                }

                if (!decision.IsAdmitted)
                {
                    return CreateAdmissionFailureLocked(
                        operation,
                        decision);
                }

                if (!IsConfiguredRemoteAddressLocked(
                        request.RemoteEndpoint))
                {
                    WriteRemoteEndpointFailure(request, operation);
                    return CreateSignedErrorLocked(
                        operation,
                        decision.VerifiedRequest,
                        403,
                        PeerSyncResponseCode.NotPeer);
                }

                try
                {
                    return ProcessAdmittedNormalRequestLocked(
                        request,
                        operation,
                        decision.VerifiedRequest,
                        body);
                }
                catch (SecurityAuditSourceUnavailableException)
                {
                    throw;
                }
                catch (SecurityAuditWriteException)
                {
                    throw;
                }
                catch (PeerSyncProtocolException exception)
                {
                    return CreateProtocolFailureLocked(
                        operation,
                        decision.VerifiedRequest,
                        exception);
                }
                catch (Exception)
                {
                    return CreateSignedErrorLocked(
                        operation,
                        decision.VerifiedRequest,
                        500,
                        PeerSyncResponseCode.Internal);
                }
            }
            finally
            {
                Clear(body);
            }
        }

        private PeerHttpResponseData CreateAdmissionFailureLocked(
            PeerInboundOperation operation,
            PeerInboundRequestDecision decision)
        {
            switch (decision.Status)
            {
                case PeerInboundRequestDecisionStatus
                    .AuthenticationRejected:
                case PeerInboundRequestDecisionStatus.ReplayDetected:
                    return PeerHttpResponseData.Bodyless(401);

                case PeerInboundRequestDecisionStatus.ClockSkew:
                    return CreateSignedErrorLocked(
                        operation,
                        decision.VerifiedRequest,
                        401,
                        PeerSyncResponseCode.ClockSkew);

                case PeerInboundRequestDecisionStatus
                    .ReplayCacheCapacityExceeded:
                    return CreateSignedErrorLocked(
                        operation,
                        decision.VerifiedRequest,
                        429,
                        PeerSyncResponseCode.LimitExceeded);

                case PeerInboundRequestDecisionStatus.RateLimited:
                    return CreateSignedErrorLocked(
                        operation,
                        decision.VerifiedRequest,
                        429,
                        PeerSyncResponseCode.LimitExceeded,
                        decision.RetryAfterSeconds);

                default:
                    throw new InvalidOperationException(
                        "An admitted Peer request cannot be mapped as an admission failure.");
            }
        }

        private PeerHttpResponseData CreateProtocolFailureLocked(
            PeerInboundOperation operation,
            PeerRequestAuthenticationData verifiedRequest,
            PeerSyncProtocolException exception)
        {
            switch (exception.Failure)
            {
                case PeerSyncProtocolFailure.ItemLimitExceeded:
                    return CreateSignedErrorLocked(
                        operation,
                        verifiedRequest,
                        413,
                        PeerSyncResponseCode.LimitExceeded);

                case PeerSyncProtocolFailure.InvalidRequest:
                    return CreateSignedErrorLocked(
                        operation,
                        verifiedRequest,
                        400,
                        PeerSyncResponseCode.BadRequest);

                case PeerSyncProtocolFailure.BodyTooLarge:
                    // Raw byte limits are enforced before authentication and
                    // deliberately have no XML envelope.
                    return PeerHttpResponseData.Bodyless(413);

                default:
                    return CreateSignedErrorLocked(
                        operation,
                        verifiedRequest,
                        500,
                        PeerSyncResponseCode.Internal);
            }
        }

        private PeerHttpResponseData CreateSignedErrorLocked(
            PeerInboundOperation operation,
            PeerRequestAuthenticationData verifiedRequest,
            int statusCode,
            PeerSyncResponseCode responseCode,
            int? retryAfterSeconds = null)
        {
            DateTimeOffset now = new DateTimeOffset(GetUtcNow());
            if (operation == PeerInboundOperation.Exchange)
            {
                return PeerAuthenticatedResponseFactory.CreateExchange(
                    verifiedRequest,
                    _activeSession,
                    statusCode,
                    PeerExchangeResponse.CreateError(responseCode),
                    now,
                    retryAfterSeconds);
            }

            if (operation == PeerInboundOperation.PkiState)
            {
                return PeerAuthenticatedResponseFactory.CreatePkiState(
                    verifiedRequest,
                    _activeSession,
                    statusCode,
                    PeerPkiStateResponse.CreateError(responseCode),
                    now,
                    retryAfterSeconds);
            }

            PeerResponseKeySource keySource =
                operation == PeerInboundOperation.Handshake
                ? PeerResponseKeySource.Handshake
                : operation == PeerInboundOperation.Revoke
                    ? PeerResponseKeySource.Revoke
                    : PeerResponseKeySource.Session;
            return PeerAuthenticatedResponseFactory.CreateControl(
                verifiedRequest,
                _pairAuthentication,
                _activeSession,
                keySource,
                statusCode,
                PeerControlResponse.CreateError(responseCode),
                operation == PeerInboundOperation.Release
                    ? _activeSession.CopySessionId()
                    : null,
                now,
                retryAfterSeconds);
        }

        private bool HasAuthenticationContextLocked(
            PeerInboundOperation operation)
        {
            if (_inboundAuthentication == null)
            {
                return false;
            }

            return operation == PeerInboundOperation.Handshake
                    || operation == PeerInboundOperation.Revoke
                ? _pairAuthentication != null
                : _activeSession != null;
        }

        private bool IsConfiguredRemoteAddressLocked(
            IPEndPoint remoteEndpoint)
        {
            if (remoteEndpoint == null || remoteEndpoint.Address == null)
            {
                return false;
            }

            string endpoint = _configurationState.GetCurrent()
                .Synchronization.PeerEndpoint;
            IPAddress expectedAddress;
            return TryGetNormalPeerEndpointAddress(
                    endpoint,
                    out expectedAddress)
                && NormalPeerAddressesEqual(
                    expectedAddress,
                    remoteEndpoint.Address);
        }

        private void WriteAuthenticationFailure(
            PeerHttpHandlerRequest request,
            PeerAuthenticationAuditInput audit)
        {
            _securityAuditLogger.WriteFailure(
                audit.EventId,
                audit.Boundary,
                audit.Operation,
                audit.Reason,
                request.RequestId,
                null,
                GetNormalRemoteAddress(request));
        }

        private void WriteAuthenticationFailure(
            PeerHttpHandlerRequest request,
            PeerInboundOperation operation,
            SecurityAuditReason reason)
        {
            _securityAuditLogger.WriteFailure(
                SecurityAuditEventId.PeerAuthenticationRejected,
                SecurityAuditBoundary.Peer,
                MapNormalSecurityOperation(operation),
                reason,
                request.RequestId,
                null,
                GetNormalRemoteAddress(request));
        }

        private void WriteRemoteEndpointFailure(
            PeerHttpHandlerRequest request,
            PeerInboundOperation operation)
        {
            _securityAuditLogger.WriteFailure(
                SecurityAuditEventId.NetworkBoundaryRejected,
                SecurityAuditBoundary.Peer,
                MapNormalSecurityOperation(operation),
                SecurityAuditReason.RemoteEndpointMismatch,
                request.RequestId,
                null,
                GetNormalRemoteAddress(request));
        }

        private static PeerAuthenticationHeaderValues
            CreateAuthenticationHeaders(PeerHttpHandlerRequest request)
        {
            return new PeerAuthenticationHeaderValues(
                request.GetHeaderValues(
                    PeerAuthenticationContract.InstanceIdHeaderName),
                request.GetHeaderValues(
                    PeerAuthenticationContract.KeyEpochHeaderName),
                request.GetHeaderValues(
                    PeerAuthenticationContract.SessionIdHeaderName),
                request.GetHeaderValues(
                    PeerAuthenticationContract.TimestampHeaderName),
                request.GetHeaderValues(
                    PeerAuthenticationContract.NonceHeaderName),
                request.GetHeaderValues(
                    PeerAuthenticationContract.SignatureHeaderName));
        }

        private static bool TryResolveNormalOperation(
            string absolutePath,
            out PeerInboundOperation operation)
        {
            switch (absolutePath)
            {
                case PeerAuthenticationContract.HandshakePath:
                    operation = PeerInboundOperation.Handshake;
                    return true;
                case PeerAuthenticationContract.ExchangePath:
                    operation = PeerInboundOperation.Exchange;
                    return true;
                case PeerAuthenticationContract.PkiStatePath:
                    operation = PeerInboundOperation.PkiState;
                    return true;
                case PeerAuthenticationContract.ReleasePath:
                    operation = PeerInboundOperation.Release;
                    return true;
                case PeerAuthenticationContract.RevokePath:
                    operation = PeerInboundOperation.Revoke;
                    return true;
                default:
                    operation = default(PeerInboundOperation);
                    return false;
            }
        }

        private static SecurityAuditOperation MapNormalSecurityOperation(
            PeerInboundOperation operation)
        {
            switch (operation)
            {
                case PeerInboundOperation.Handshake:
                    return SecurityAuditOperation.PeerHandshake;
                case PeerInboundOperation.Exchange:
                case PeerInboundOperation.PkiState:
                    return SecurityAuditOperation.PeerExchange;
                case PeerInboundOperation.Release:
                    return SecurityAuditOperation.PeerRelease;
                case PeerInboundOperation.Revoke:
                    return SecurityAuditOperation.PeerRevoke;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        private static IPAddress GetNormalRemoteAddress(
            PeerHttpHandlerRequest request)
        {
            IPEndPoint endpoint = request.RemoteEndpoint;
            return endpoint == null ? null : endpoint.Address;
        }

        private static bool TryGetNormalPeerEndpointAddress(
            string endpoint,
            out IPAddress address)
        {
            address = null;
            if (string.IsNullOrEmpty(endpoint)
                || !endpoint.StartsWith(
                    "https://",
                    StringComparison.Ordinal))
            {
                return false;
            }

            string authority = endpoint.Substring("https://".Length);
            if (authority.IndexOf('[') >= 0
                || authority.IndexOf(']') >= 0)
            {
                return false;
            }

            int portSeparator = authority.LastIndexOf(':');
            if (portSeparator <= 0)
            {
                return false;
            }

            string literal = authority.Substring(0, portSeparator);
            return IPAddress.TryParse(literal, out address)
                && address.AddressFamily
                    == System.Net.Sockets.AddressFamily.InterNetwork;
        }

        private static bool NormalPeerAddressesEqual(
            IPAddress expected,
            IPAddress actual)
        {
            if (expected == null
                || actual == null
                || expected.AddressFamily != actual.AddressFamily)
            {
                return false;
            }

            byte[] left = expected.GetAddressBytes();
            byte[] right = actual.GetAddressBytes();
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
