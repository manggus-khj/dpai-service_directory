using System;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal sealed class PeerInboundRequestData
    {
        private readonly byte[] _body;

        public PeerInboundRequestData(
            PeerInboundOperation operation,
            PeerAuthenticationHeaderValues authenticationHeaders,
            string method,
            PeerCanonicalRequestTarget requestTarget,
            string contentType,
            byte[] body,
            DateTimeOffset receivedAt)
        {
            if (!Enum.IsDefined(typeof(PeerInboundOperation), operation))
            {
                throw new ArgumentOutOfRangeException(nameof(operation));
            }

            AuthenticationHeaders = authenticationHeaders
                ?? throw new ArgumentNullException(
                    nameof(authenticationHeaders));
            RequestTarget = requestTarget
                ?? throw new ArgumentNullException(nameof(requestTarget));
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            Operation = operation;
            Method = method;
            ContentType = contentType;
            _body = (byte[])body.Clone();
            ReceivedAtUtc = receivedAt.ToUniversalTime();
        }

        public PeerInboundOperation Operation { get; }

        public PeerAuthenticationHeaderValues AuthenticationHeaders { get; }

        public string Method { get; }

        public PeerCanonicalRequestTarget RequestTarget { get; }

        public string ContentType { get; }

        public DateTimeOffset ReceivedAtUtc { get; }

        internal byte[] CopyBody()
        {
            return (byte[])_body.Clone();
        }
    }

    // This is the complete security-audit payload produced by the coordinator.
    // It deliberately carries only closed enums. Request IDs and remote
    // addresses are transport metadata supplied later by the host; HMACs,
    // signatures, nonces, sessions, keys, and request bodies cannot enter it.
    internal sealed class PeerAuthenticationAuditInput
    {
        internal PeerAuthenticationAuditInput(
            SecurityAuditOperation operation,
            SecurityAuditReason reason)
        {
            SecurityAuditContract.Validate(
                SecurityAuditEventId.PeerAuthenticationRejected,
                SecurityAuditBoundary.Peer,
                operation,
                reason);

            Operation = operation;
            Reason = reason;
        }

        public SecurityAuditEventId EventId =>
            SecurityAuditEventId.PeerAuthenticationRejected;

        public SecurityAuditBoundary Boundary => SecurityAuditBoundary.Peer;

        public SecurityAuditOperation Operation { get; }

        public SecurityAuditReason Reason { get; }
    }

    internal enum PeerInboundRequestDecisionStatus
    {
        Admitted = 0,
        AuthenticationRejected = 1,
        ClockSkew = 2,
        ReplayDetected = 3,
        ReplayCacheCapacityExceeded = 4,
        RateLimited = 5
    }

    internal sealed class PeerInboundRequestDecision
    {
        private PeerInboundRequestDecision(
            PeerInboundRequestDecisionStatus status,
            PeerRequestAuthenticationData verifiedRequest,
            PeerAuthenticationAuditInput auditInput,
            int? retryAfterSeconds,
            bool canSignErrorResponse)
        {
            bool isAdmitted =
                status == PeerInboundRequestDecisionStatus.Admitted;
            bool hasVerifiedSignature = verifiedRequest != null;
            bool needsAudit =
                status
                    == PeerInboundRequestDecisionStatus
                        .AuthenticationRejected
                || status == PeerInboundRequestDecisionStatus.ClockSkew
                || status == PeerInboundRequestDecisionStatus.ReplayDetected;

            if (isAdmitted && !hasVerifiedSignature)
            {
                throw new ArgumentException(
                    "An admitted Peer request needs a verified request.");
            }

            if (needsAudit != (auditInput != null))
            {
                throw new ArgumentException(
                    "The Peer decision audit shape is invalid.");
            }

            if (status
                    == PeerInboundRequestDecisionStatus
                        .AuthenticationRejected
                && hasVerifiedSignature)
            {
                throw new ArgumentException(
                    "A pre-authentication rejection cannot expose a verified request.");
            }

            if (status
                    != PeerInboundRequestDecisionStatus
                        .AuthenticationRejected
                && !hasVerifiedSignature)
            {
                throw new ArgumentException(
                    "A post-HMAC Peer decision needs its verified request.");
            }

            bool rateLimited =
                status == PeerInboundRequestDecisionStatus.RateLimited;
            if (rateLimited != retryAfterSeconds.HasValue)
            {
                throw new ArgumentException(
                    "Only a time-based Peer rate limit has Retry-After.");
            }

            if (retryAfterSeconds.HasValue
                && retryAfterSeconds.Value < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(retryAfterSeconds));
            }

            bool eligibleForSignedError =
                status == PeerInboundRequestDecisionStatus.ClockSkew
                || status
                    == PeerInboundRequestDecisionStatus
                        .ReplayCacheCapacityExceeded
                || rateLimited;
            if (canSignErrorResponse != eligibleForSignedError)
            {
                throw new ArgumentException(
                    "The Peer signed-error eligibility is invalid.");
            }

            Status = status;
            VerifiedRequest = verifiedRequest;
            AuditInput = auditInput;
            RetryAfterSeconds = retryAfterSeconds;
            CanSignErrorResponse = canSignErrorResponse;
        }

        public PeerInboundRequestDecisionStatus Status { get; }

        public bool IsAdmitted =>
            Status == PeerInboundRequestDecisionStatus.Admitted;

        public bool HasVerifiedSignature => VerifiedRequest != null;

        public PeerRequestAuthenticationData VerifiedRequest { get; }

        public PeerAuthenticationAuditInput AuditInput { get; }

        public int? RetryAfterSeconds { get; }

        public bool CanSignErrorResponse { get; }

        internal static PeerInboundRequestDecision Admitted(
            PeerRequestAuthenticationData request)
        {
            return new PeerInboundRequestDecision(
                PeerInboundRequestDecisionStatus.Admitted,
                request,
                null,
                null,
                false);
        }

        internal static PeerInboundRequestDecision AuthenticationRejected(
            PeerAuthenticationAuditInput auditInput)
        {
            return new PeerInboundRequestDecision(
                PeerInboundRequestDecisionStatus.AuthenticationRejected,
                null,
                auditInput,
                null,
                false);
        }

        internal static PeerInboundRequestDecision ClockSkew(
            PeerRequestAuthenticationData request,
            PeerAuthenticationAuditInput auditInput)
        {
            return new PeerInboundRequestDecision(
                PeerInboundRequestDecisionStatus.ClockSkew,
                request,
                auditInput,
                null,
                true);
        }

        internal static PeerInboundRequestDecision ReplayDetected(
            PeerRequestAuthenticationData request,
            PeerAuthenticationAuditInput auditInput)
        {
            return new PeerInboundRequestDecision(
                PeerInboundRequestDecisionStatus.ReplayDetected,
                request,
                auditInput,
                null,
                false);
        }

        internal static PeerInboundRequestDecision ReplayCapacityExceeded(
            PeerRequestAuthenticationData request)
        {
            return new PeerInboundRequestDecision(
                PeerInboundRequestDecisionStatus
                    .ReplayCacheCapacityExceeded,
                request,
                null,
                null,
                true);
        }

        internal static PeerInboundRequestDecision RateLimited(
            PeerRequestAuthenticationData request,
            int retryAfterSeconds)
        {
            return new PeerInboundRequestDecision(
                PeerInboundRequestDecisionStatus.RateLimited,
                request,
                null,
                retryAfterSeconds,
                true);
        }
    }

    internal sealed class PeerInboundRequestCoordinator
    {
        // This cache is shared by every production coordinator in the process.
        // It is intentionally in-memory and bounded. A process restart loses
        // its replay history, so session orchestration must invalidate any
        // pre-restart session instead of reconstructing and reusing it.
        private static readonly PeerNonceReplayCache ProcessWideReplayCache =
            new PeerNonceReplayCache();

        private readonly PeerNonceReplayCache _replayCache;
        private readonly PeerRequestRateLimiter _rateLimiter;

        public PeerInboundRequestCoordinator(
            PeerRequestRateLimiter rateLimiter)
            : this(rateLimiter, ProcessWideReplayCache)
        {
        }

        internal PeerInboundRequestCoordinator(
            PeerRequestRateLimiter rateLimiter,
            PeerNonceReplayCache replayCache)
        {
            _rateLimiter = rateLimiter
                ?? throw new ArgumentNullException(nameof(rateLimiter));
            _replayCache = replayCache
                ?? throw new ArgumentNullException(nameof(replayCache));
        }

        public PeerInboundRequestDecision AuthenticatePairBoundRequest(
            PeerInboundRequestData input,
            PeerPairAuthenticationContext authenticationContext)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (authenticationContext == null)
            {
                throw new ArgumentNullException(
                    nameof(authenticationContext));
            }

            if (input.Operation != PeerInboundOperation.Handshake
                && input.Operation != PeerInboundOperation.Revoke)
            {
                throw new ArgumentException(
                    "Pair-bound authentication accepts only handshake or revoke requests.",
                    nameof(input));
            }

            EnsureRateBinding(authenticationContext.PeerInstanceId);
            PeerParsedAuthenticationHeaders headers;
            if (!PeerAuthenticationHeaderCodec.TryParseRequest(
                input.AuthenticationHeaders,
                PeerSessionHeaderRequirement.Forbidden,
                out headers))
            {
                return RejectMalformed(input.Operation);
            }

            using (headers)
            {
                PeerRequestAuthenticationData request;
                if (!TryCreateRequest(
                    input,
                    headers,
                    authenticationContext.LocalInstanceId,
                    out request))
                {
                    return RejectMalformed(input.Operation);
                }

                byte[] signature = headers.CopySignature();
                try
                {
                    PeerRequestAuthenticationResult result =
                        input.Operation == PeerInboundOperation.Handshake
                        ? PeerMessageAuthenticator.AuthenticateHandshakeRequest(
                            authenticationContext,
                            request,
                            signature,
                            input.ReceivedAtUtc,
                            _replayCache)
                        : PeerMessageAuthenticator.AuthenticateRevokeRequest(
                            authenticationContext,
                            request,
                            signature,
                            input.ReceivedAtUtc,
                            _replayCache);

                    return MapAuthenticationResult(
                        input.Operation,
                        result,
                        request,
                        authenticationContext.LocalInstanceId,
                        authenticationContext.PeerInstanceId,
                        authenticationContext.KeyEpoch);
                }
                finally
                {
                    Array.Clear(signature, 0, signature.Length);
                }
            }
        }

        public PeerInboundRequestDecision AuthenticateSessionBoundRequest(
            PeerInboundRequestData input,
            ActivePeerSession activeSession)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (activeSession == null)
            {
                throw new ArgumentNullException(nameof(activeSession));
            }

            if (input.Operation != PeerInboundOperation.Exchange
                && input.Operation != PeerInboundOperation.PkiState
                && input.Operation != PeerInboundOperation.Release)
            {
                throw new ArgumentException(
                    "Session-bound authentication accepts only exchange or release requests.",
                    nameof(input));
            }

            EnsureRateBinding(activeSession.PeerInstanceId);
            PeerParsedAuthenticationHeaders headers;
            if (!PeerAuthenticationHeaderCodec.TryParseRequest(
                input.AuthenticationHeaders,
                PeerSessionHeaderRequirement.Required,
                out headers))
            {
                return RejectMalformed(input.Operation);
            }

            using (headers)
            {
                PeerRequestAuthenticationData request;
                if (!TryCreateRequest(
                    input,
                    headers,
                    activeSession.LocalInstanceId,
                    out request))
                {
                    return RejectMalformed(input.Operation);
                }

                byte[] signature = headers.CopySignature();
                try
                {
                    PeerRequestAuthenticationResult result =
                        PeerMessageAuthenticator.AuthenticateSessionRequest(
                            activeSession,
                            request,
                            signature,
                            input.ReceivedAtUtc,
                            _replayCache);
                    return MapAuthenticationResult(
                        input.Operation,
                        result,
                        request,
                        activeSession.LocalInstanceId,
                        activeSession.PeerInstanceId,
                        activeSession.KeyEpoch);
                }
                finally
                {
                    Array.Clear(signature, 0, signature.Length);
                }
            }
        }

        private PeerInboundRequestDecision MapAuthenticationResult(
            PeerInboundOperation operation,
            PeerRequestAuthenticationResult result,
            PeerRequestAuthenticationData request,
            Guid localInstanceId,
            Guid peerInstanceId,
            ulong keyEpoch)
        {
            switch (result)
            {
                case PeerRequestAuthenticationResult.Authenticated:
                    PeerRateLimitDecision rateDecision =
                        _rateLimiter.TryAcquire(operation);
                    return rateDecision.IsAllowed
                        ? PeerInboundRequestDecision.Admitted(request)
                        : PeerInboundRequestDecision.RateLimited(
                            request,
                            rateDecision.RetryAfterSeconds.Value);

                case PeerRequestAuthenticationResult.InvalidSignature:
                    return Reject(
                        operation,
                        SecurityAuditReason.SignatureInvalid);

                case PeerRequestAuthenticationResult.ClockSkew:
                    return PeerInboundRequestDecision.ClockSkew(
                        request,
                        CreateAudit(
                            operation,
                            SecurityAuditReason
                                .TimestampOutsideAllowedWindow));

                case PeerRequestAuthenticationResult.InvalidSession:
                    return Reject(
                        operation,
                        ClassifyBindingFailure(
                            request,
                            localInstanceId,
                            peerInstanceId,
                            keyEpoch));

                case PeerRequestAuthenticationResult.ReplayDetected:
                    return PeerInboundRequestDecision.ReplayDetected(
                        request,
                        CreateAudit(
                            operation,
                            SecurityAuditReason.NonceReplay));

                case PeerRequestAuthenticationResult
                    .ReplayCacheCapacityExceeded:
                    return PeerInboundRequestDecision
                        .ReplayCapacityExceeded(request);

                default:
                    throw new InvalidOperationException(
                        "The Peer authenticator returned an unknown result.");
            }
        }

        private static bool TryCreateRequest(
            PeerInboundRequestData input,
            PeerParsedAuthenticationHeaders headers,
            Guid receiverInstanceId,
            out PeerRequestAuthenticationData request)
        {
            request = null;
            byte[] sessionId = null;
            byte[] body = null;
            byte[] nonce = null;
            try
            {
                sessionId = headers.CopySessionId();
                body = input.CopyBody();
                nonce = headers.CopyNonce();
                request = new PeerRequestAuthenticationData(
                    headers.SenderInstanceId,
                    receiverInstanceId,
                    headers.KeyEpoch,
                    sessionId,
                    input.Method,
                    input.RequestTarget,
                    input.ContentType,
                    body,
                    headers.Timestamp,
                    nonce);
                return true;
            }
            catch (ArgumentException)
            {
                request = null;
                return false;
            }
            finally
            {
                Clear(sessionId);
                Clear(body);
                Clear(nonce);
            }
        }

        private void EnsureRateBinding(Guid peerInstanceId)
        {
            if (_rateLimiter.PeerInstanceId != peerInstanceId)
            {
                throw new InvalidOperationException(
                    "The Peer rate limiter is bound to a different trusted peer.");
            }
        }

        private static SecurityAuditReason ClassifyBindingFailure(
            PeerRequestAuthenticationData request,
            Guid localInstanceId,
            Guid peerInstanceId,
            ulong keyEpoch)
        {
            if (request.SenderInstanceId != peerInstanceId
                || request.ReceiverInstanceId != localInstanceId)
            {
                return SecurityAuditReason.PeerBindingMismatch;
            }

            if (request.KeyEpoch != keyEpoch)
            {
                return SecurityAuditReason.KeyEpochMismatch;
            }

            return SecurityAuditReason.SessionInvalid;
        }

        private static PeerInboundRequestDecision RejectMalformed(
            PeerInboundOperation operation)
        {
            return Reject(
                operation,
                SecurityAuditReason.AuthenticationDataMissingOrMalformed);
        }

        private static PeerInboundRequestDecision Reject(
            PeerInboundOperation operation,
            SecurityAuditReason reason)
        {
            return PeerInboundRequestDecision.AuthenticationRejected(
                CreateAudit(operation, reason));
        }

        private static PeerAuthenticationAuditInput CreateAudit(
            PeerInboundOperation operation,
            SecurityAuditReason reason)
        {
            SecurityAuditOperation auditOperation;
            switch (operation)
            {
                case PeerInboundOperation.Handshake:
                    auditOperation = SecurityAuditOperation.PeerHandshake;
                    break;
                case PeerInboundOperation.Exchange:
                case PeerInboundOperation.PkiState:
                    auditOperation = SecurityAuditOperation.PeerExchange;
                    break;
                case PeerInboundOperation.Release:
                    auditOperation = SecurityAuditOperation.PeerRelease;
                    break;
                case PeerInboundOperation.Revoke:
                    auditOperation = SecurityAuditOperation.PeerRevoke;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }

            return new PeerAuthenticationAuditInput(
                auditOperation,
                reason);
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }
    }
}
