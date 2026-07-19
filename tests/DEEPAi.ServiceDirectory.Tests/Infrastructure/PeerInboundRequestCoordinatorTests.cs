using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PeerInboundRequestCoordinatorTests
    {
        private static readonly Guid LocalInstanceId = Guid.Parse(
            "11111111-1111-1111-1111-111111111111");
        private static readonly Guid PeerInstanceId = Guid.Parse(
            "22222222-2222-2222-2222-222222222222");
        private static readonly Guid OtherInstanceId = Guid.Parse(
            "33333333-3333-3333-3333-333333333333");
        private static readonly DateTimeOffset Timestamp =
            new DateTimeOffset(2026, 7, 18, 3, 0, 0, TimeSpan.Zero);

        private const ulong KeyEpoch = 7;
        private const string PeerEndpoint = "http://10.0.0.2:21000";
        private const string ContentType =
            "application/xml; charset=utf-8";

        [TestMethod]
        public void HeaderCodecRequiresSingleCanonicalValuesAndSessionShape()
        {
            byte[] nonce = Bytes(0, 16);
            byte[] signature = Bytes(16, 32);
            byte[] sessionId = Bytes(48, 16);
            PeerAuthenticationHeaderValues pairHeaders = CreateHeaders(
                PeerInstanceId,
                KeyEpoch,
                null,
                Timestamp,
                nonce,
                signature);

            PeerParsedAuthenticationHeaders parsed;
            Assert.IsTrue(PeerAuthenticationHeaderCodec.TryParseRequest(
                pairHeaders,
                PeerSessionHeaderRequirement.Forbidden,
                out parsed));
            using (parsed)
            {
                Assert.AreEqual(PeerInstanceId, parsed.SenderInstanceId);
                Assert.AreEqual(KeyEpoch, parsed.KeyEpoch);
                Assert.IsFalse(parsed.HasSession);
            }

            var duplicateIdentity = new PeerAuthenticationHeaderValues(
                new[]
                {
                    PeerInstanceId.ToString("D"),
                    PeerInstanceId.ToString("D")
                },
                new[] { KeyEpoch.ToString(CultureInfo.InvariantCulture) },
                new string[0],
                new[] { PeerAuthenticationContract.FormatTimestamp(Timestamp) },
                new[] { Convert.ToBase64String(nonce) },
                new[] { Convert.ToBase64String(signature) });
            Assert.IsFalse(PeerAuthenticationHeaderCodec.TryParseRequest(
                duplicateIdentity,
                PeerSessionHeaderRequirement.Forbidden,
                out parsed));
            Assert.IsNull(parsed);

            Assert.IsFalse(PeerAuthenticationHeaderCodec.TryParseRequest(
                pairHeaders,
                PeerSessionHeaderRequirement.Required,
                out parsed));
            Assert.IsNull(parsed);

            PeerAuthenticationHeaderValues sessionHeaders = CreateHeaders(
                PeerInstanceId,
                KeyEpoch,
                sessionId,
                Timestamp,
                nonce,
                signature);
            Assert.IsFalse(PeerAuthenticationHeaderCodec.TryParseRequest(
                sessionHeaders,
                PeerSessionHeaderRequirement.Forbidden,
                out parsed));
            Assert.IsTrue(PeerAuthenticationHeaderCodec.TryParseRequest(
                sessionHeaders,
                PeerSessionHeaderRequirement.Required,
                out parsed));
            parsed.Dispose();
        }

        [TestMethod]
        public void MalformedHeadersProduceSecretFreeAuditWithoutUsingCache()
        {
            long monotonic = 0;
            var cache = new PeerNonceReplayCache(16, () => monotonic, 1);
            PeerInboundRequestCoordinator coordinator = CreateCoordinator(
                () => monotonic,
                cache);
            PeerAuthenticationHeaderValues missingSignature =
                new PeerAuthenticationHeaderValues(
                    new[] { PeerInstanceId.ToString("D") },
                    new[] { KeyEpoch.ToString(CultureInfo.InvariantCulture) },
                    new string[0],
                    new[]
                    {
                        PeerAuthenticationContract.FormatTimestamp(Timestamp)
                    },
                    new[] { Convert.ToBase64String(Bytes(0, 16)) },
                    new string[0]);
            var input = new PeerInboundRequestData(
                PeerInboundOperation.Handshake,
                missingSignature,
                "POST",
                CreateTarget(PeerInboundOperation.Handshake),
                ContentType,
                CreateBody(),
                Timestamp);

            using (PeerPairAuthenticationContext context = CreatePairContext())
            {
                PeerInboundRequestDecision decision = coordinator
                    .AuthenticatePairBoundRequest(input, context);
                AssertAuthenticationRejection(
                    decision,
                    SecurityAuditOperation.PeerHandshake,
                    SecurityAuditReason
                        .AuthenticationDataMissingOrMalformed);
            }

            Assert.AreEqual(0, cache.Count);
            PropertyInfo[] auditProperties =
                typeof(PeerAuthenticationAuditInput).GetProperties(
                    BindingFlags.Instance | BindingFlags.Public);
            Assert.AreEqual(4, auditProperties.Length);
            foreach (PropertyInfo property in auditProperties)
            {
                Assert.IsTrue(
                    property.PropertyType.IsEnum,
                    property.Name + " must be a closed enum.");
            }
        }

        [TestMethod]
        public void HmacIsVerifiedBeforeFreshnessAndInvalidHmacUsesNoNonce()
        {
            long monotonic = 0;
            var cache = new PeerNonceReplayCache(16, () => monotonic, 1);
            PeerInboundRequestCoordinator coordinator = CreateCoordinator(
                () => monotonic,
                cache);
            DateTimeOffset staleTimestamp = Timestamp.AddSeconds(-61);
            byte[] nonce = Bytes(0, 16);
            PeerInboundRequestData invalidSignature = CreateSignedInput(
                PeerInboundOperation.Handshake,
                PeerInstanceId,
                KeyEpoch,
                null,
                staleTimestamp,
                Timestamp,
                nonce,
                Bytes(224, 32));
            PeerInboundRequestData validSignature = CreateSignedInput(
                PeerInboundOperation.Handshake,
                PeerInstanceId,
                KeyEpoch,
                null,
                staleTimestamp,
                Timestamp,
                nonce,
                HandshakeRequestKey());

            using (PeerPairAuthenticationContext context = CreatePairContext())
            {
                PeerInboundRequestDecision invalid = coordinator
                    .AuthenticatePairBoundRequest(invalidSignature, context);
                AssertAuthenticationRejection(
                    invalid,
                    SecurityAuditOperation.PeerHandshake,
                    SecurityAuditReason.SignatureInvalid);
                Assert.IsFalse(invalid.CanSignErrorResponse);
                Assert.AreEqual(0, cache.Count);

                PeerInboundRequestDecision stale = coordinator
                    .AuthenticatePairBoundRequest(validSignature, context);
                Assert.AreEqual(
                    PeerInboundRequestDecisionStatus.ClockSkew,
                    stale.Status);
                Assert.IsTrue(stale.HasVerifiedSignature);
                Assert.IsTrue(stale.CanSignErrorResponse);
                Assert.AreEqual(
                    SecurityAuditReason.TimestampOutsideAllowedWindow,
                    stale.AuditInput.Reason);
                Assert.AreEqual(0, cache.Count);
            }
        }

        [TestMethod]
        public void PairBindingEpochAndReplayAreClassifiedInOrder()
        {
            long monotonic = 0;
            var cache = new PeerNonceReplayCache(16, () => monotonic, 1);
            PeerInboundRequestCoordinator coordinator = CreateCoordinator(
                () => monotonic,
                cache);

            using (PeerPairAuthenticationContext context = CreatePairContext())
            {
                PeerInboundRequestDecision wrongPeer = coordinator
                    .AuthenticatePairBoundRequest(
                        CreateSignedInput(
                            PeerInboundOperation.Handshake,
                            OtherInstanceId,
                            KeyEpoch,
                            null,
                            Timestamp,
                            Timestamp,
                            Bytes(0, 16),
                            HandshakeRequestKey()),
                        context);
                AssertAuthenticationRejection(
                    wrongPeer,
                    SecurityAuditOperation.PeerHandshake,
                    SecurityAuditReason.PeerBindingMismatch);

                PeerInboundRequestDecision wrongEpoch = coordinator
                    .AuthenticatePairBoundRequest(
                        CreateSignedInput(
                            PeerInboundOperation.Handshake,
                            PeerInstanceId,
                            KeyEpoch + 1,
                            null,
                            Timestamp,
                            Timestamp,
                            Bytes(16, 16),
                            HandshakeRequestKey()),
                        context);
                AssertAuthenticationRejection(
                    wrongEpoch,
                    SecurityAuditOperation.PeerHandshake,
                    SecurityAuditReason.KeyEpochMismatch);
                Assert.AreEqual(0, cache.Count);

                PeerInboundRequestData valid = CreateSignedInput(
                    PeerInboundOperation.Handshake,
                    PeerInstanceId,
                    KeyEpoch,
                    null,
                    Timestamp,
                    Timestamp,
                    Bytes(32, 16),
                    HandshakeRequestKey());
                PeerInboundRequestDecision accepted = coordinator
                    .AuthenticatePairBoundRequest(valid, context);
                Assert.IsTrue(accepted.IsAdmitted);
                Assert.IsTrue(accepted.HasVerifiedSignature);
                Assert.AreEqual(1, cache.Count);

                PeerInboundRequestDecision replay = coordinator
                    .AuthenticatePairBoundRequest(valid, context);
                Assert.AreEqual(
                    PeerInboundRequestDecisionStatus.ReplayDetected,
                    replay.Status);
                Assert.AreEqual(
                    SecurityAuditReason.NonceReplay,
                    replay.AuditInput.Reason);
                Assert.IsFalse(replay.CanSignErrorResponse);
                Assert.AreEqual(1, cache.Count);
            }
        }

        [TestMethod]
        public void SessionRequestsUseOnlyStoredSessionBinding()
        {
            long monotonic = 0;
            var cache = new PeerNonceReplayCache(16, () => monotonic, 1);
            PeerInboundRequestCoordinator coordinator = CreateCoordinator(
                () => monotonic,
                cache);
            byte[] sessionId = SessionId();

            using (ActivePeerSession session = CreateSession(sessionId))
            {
                PeerInboundRequestDecision wrongSession = coordinator
                    .AuthenticateSessionBoundRequest(
                        CreateSignedInput(
                            PeerInboundOperation.Exchange,
                            PeerInstanceId,
                            KeyEpoch,
                            Bytes(48, 16),
                            Timestamp,
                            Timestamp,
                            Bytes(64, 16),
                            SessionRequestKey()),
                        session);
                AssertAuthenticationRejection(
                    wrongSession,
                    SecurityAuditOperation.PeerExchange,
                    SecurityAuditReason.SessionInvalid);
                Assert.AreEqual(0, cache.Count);

                PeerInboundRequestDecision exchange = coordinator
                    .AuthenticateSessionBoundRequest(
                        CreateSignedInput(
                            PeerInboundOperation.Exchange,
                            PeerInstanceId,
                            KeyEpoch,
                            sessionId,
                            Timestamp,
                            Timestamp,
                            Bytes(80, 16),
                            SessionRequestKey()),
                        session);
                Assert.IsTrue(exchange.IsAdmitted);

                PeerInboundRequestDecision release = coordinator
                    .AuthenticateSessionBoundRequest(
                        CreateSignedInput(
                            PeerInboundOperation.Release,
                            PeerInstanceId,
                            KeyEpoch,
                            sessionId,
                            Timestamp,
                            Timestamp,
                            Bytes(96, 16),
                            SessionRequestKey()),
                        session);
                Assert.IsTrue(release.IsAdmitted);
                Assert.AreEqual(2, cache.Count);
            }
        }

        [TestMethod]
        public void AuthenticatedRateLimitReturnsSignedRetryAfterDecision()
        {
            long monotonic = 0;
            var cache = new PeerNonceReplayCache(16, () => monotonic, 1);
            PeerInboundRequestCoordinator coordinator = CreateCoordinator(
                () => monotonic,
                cache);

            using (PeerPairAuthenticationContext context = CreatePairContext())
            {
                for (int index = 0; index < 3; index++)
                {
                    PeerInboundRequestDecision admitted = coordinator
                        .AuthenticatePairBoundRequest(
                            CreateSignedInput(
                                PeerInboundOperation.Handshake,
                                PeerInstanceId,
                                KeyEpoch,
                                null,
                                Timestamp,
                                Timestamp,
                                Bytes(index * 16, 16),
                                HandshakeRequestKey()),
                            context);
                    Assert.IsTrue(admitted.IsAdmitted);
                }

                PeerInboundRequestDecision limited = coordinator
                    .AuthenticatePairBoundRequest(
                        CreateSignedInput(
                            PeerInboundOperation.Handshake,
                            PeerInstanceId,
                            KeyEpoch,
                            null,
                            Timestamp,
                            Timestamp,
                            Bytes(48, 16),
                            HandshakeRequestKey()),
                        context);
                Assert.AreEqual(
                    PeerInboundRequestDecisionStatus.RateLimited,
                    limited.Status);
                Assert.AreEqual(5, limited.RetryAfterSeconds);
                Assert.IsTrue(limited.HasVerifiedSignature);
                Assert.IsTrue(limited.CanSignErrorResponse);
                Assert.IsNull(limited.AuditInput);

                // Authentication registers the valid nonce before admission.
                Assert.AreEqual(4, cache.Count);

                monotonic = 5;
                PeerInboundRequestDecision refilled = coordinator
                    .AuthenticatePairBoundRequest(
                        CreateSignedInput(
                            PeerInboundOperation.Handshake,
                            PeerInstanceId,
                            KeyEpoch,
                            null,
                            Timestamp,
                            Timestamp,
                            Bytes(64, 16),
                            HandshakeRequestKey()),
                        context);
                Assert.IsTrue(refilled.IsAdmitted);
            }
        }

        [TestMethod]
        public void ReplayCacheCapacityIsPostHmacSignedLimitWithoutRetryAfter()
        {
            long monotonic = 0;
            var cache = new PeerNonceReplayCache(1, () => monotonic, 1);
            PeerInboundRequestCoordinator coordinator = CreateCoordinator(
                () => monotonic,
                cache);

            using (PeerPairAuthenticationContext context = CreatePairContext())
            {
                Assert.IsTrue(coordinator.AuthenticatePairBoundRequest(
                    CreateSignedInput(
                        PeerInboundOperation.Handshake,
                        PeerInstanceId,
                        KeyEpoch,
                        null,
                        Timestamp,
                        Timestamp,
                        Bytes(0, 16),
                        HandshakeRequestKey()),
                    context).IsAdmitted);

                PeerInboundRequestDecision full = coordinator
                    .AuthenticatePairBoundRequest(
                        CreateSignedInput(
                            PeerInboundOperation.Handshake,
                            PeerInstanceId,
                            KeyEpoch,
                            null,
                            Timestamp,
                            Timestamp,
                            Bytes(16, 16),
                            HandshakeRequestKey()),
                        context);
                Assert.AreEqual(
                    PeerInboundRequestDecisionStatus
                        .ReplayCacheCapacityExceeded,
                    full.Status);
                Assert.IsTrue(full.HasVerifiedSignature);
                Assert.IsTrue(full.CanSignErrorResponse);
                Assert.IsFalse(full.RetryAfterSeconds.HasValue);
                Assert.IsNull(full.AuditInput);
            }
        }

        [TestMethod]
        public void CoordinatorsCanShareOneProcessReplayCache()
        {
            long monotonic = 0;
            var cache = new PeerNonceReplayCache(16, () => monotonic, 1);
            PeerInboundRequestCoordinator first = CreateCoordinator(
                () => monotonic,
                cache);
            PeerInboundRequestCoordinator second = CreateCoordinator(
                () => monotonic,
                cache);
            PeerInboundRequestData input = CreateSignedInput(
                PeerInboundOperation.Handshake,
                PeerInstanceId,
                KeyEpoch,
                null,
                Timestamp,
                Timestamp,
                Bytes(0, 16),
                HandshakeRequestKey());

            using (PeerPairAuthenticationContext firstContext =
                CreatePairContext())
            using (PeerPairAuthenticationContext secondContext =
                CreatePairContext())
            {
                Assert.IsTrue(first.AuthenticatePairBoundRequest(
                    input,
                    firstContext).IsAdmitted);
                Assert.AreEqual(
                    PeerInboundRequestDecisionStatus.ReplayDetected,
                    second.AuthenticatePairBoundRequest(
                        input,
                        secondContext).Status);
            }
        }

        private static PeerInboundRequestCoordinator CreateCoordinator(
            Func<long> timestampProvider,
            PeerNonceReplayCache cache)
        {
            var limiter = new PeerRequestRateLimiter(
                PeerEndpoint,
                PeerInstanceId,
                timestampProvider,
                1);
            return new PeerInboundRequestCoordinator(limiter, cache);
        }

        private static PeerPairAuthenticationContext CreatePairContext()
        {
            return new PeerPairAuthenticationContext(
                LocalInstanceId,
                PeerInstanceId,
                KeyEpoch,
                HandshakeRequestKey(),
                Bytes(32, 32),
                Bytes(64, 32),
                Bytes(96, 32));
        }

        private static ActivePeerSession CreateSession(byte[] sessionId)
        {
            return new ActivePeerSession(
                LocalInstanceId,
                PeerInstanceId,
                KeyEpoch,
                sessionId,
                Timestamp.AddMinutes(10),
                SessionRequestKey(),
                Bytes(160, 32));
        }

        private static PeerInboundRequestData CreateSignedInput(
            PeerInboundOperation operation,
            Guid senderInstanceId,
            ulong keyEpoch,
            byte[] sessionId,
            DateTimeOffset messageTimestamp,
            DateTimeOffset receivedAt,
            byte[] nonce,
            byte[] signingKey)
        {
            byte[] body = CreateBody();
            PeerCanonicalRequestTarget target = CreateTarget(operation);
            var authenticationData = new PeerRequestAuthenticationData(
                senderInstanceId,
                LocalInstanceId,
                keyEpoch,
                sessionId,
                "POST",
                target,
                ContentType,
                body,
                messageTimestamp,
                nonce);
            byte[] signature = PeerMessageAuthenticator
                .CreateRequestSignature(signingKey, authenticationData);
            try
            {
                return new PeerInboundRequestData(
                    operation,
                    CreateHeaders(
                        senderInstanceId,
                        keyEpoch,
                        sessionId,
                        messageTimestamp,
                        nonce,
                        signature),
                    "POST",
                    target,
                    ContentType,
                    body,
                    receivedAt);
            }
            finally
            {
                Array.Clear(signature, 0, signature.Length);
                Array.Clear(body, 0, body.Length);
            }
        }

        private static PeerAuthenticationHeaderValues CreateHeaders(
            Guid senderInstanceId,
            ulong keyEpoch,
            byte[] sessionId,
            DateTimeOffset timestamp,
            byte[] nonce,
            byte[] signature)
        {
            return new PeerAuthenticationHeaderValues(
                new[]
                {
                    senderInstanceId.ToString("D").ToLowerInvariant()
                },
                new[]
                {
                    keyEpoch.ToString(CultureInfo.InvariantCulture)
                },
                sessionId == null
                    ? new string[0]
                    : new[] { Convert.ToBase64String(sessionId) },
                new[]
                {
                    PeerAuthenticationContract.FormatTimestamp(timestamp)
                },
                new[] { Convert.ToBase64String(nonce) },
                new[] { Convert.ToBase64String(signature) });
        }

        private static PeerCanonicalRequestTarget CreateTarget(
            PeerInboundOperation operation)
        {
            string path;
            switch (operation)
            {
                case PeerInboundOperation.Handshake:
                    path = PeerAuthenticationContract.HandshakePath;
                    break;
                case PeerInboundOperation.Exchange:
                    path = PeerAuthenticationContract.ExchangePath;
                    break;
                case PeerInboundOperation.Release:
                    path = PeerAuthenticationContract.ReleasePath;
                    break;
                case PeerInboundOperation.Revoke:
                    path = PeerAuthenticationContract.RevokePath;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }

            return PeerCanonicalRequestTarget.Create(path, null);
        }

        private static void AssertAuthenticationRejection(
            PeerInboundRequestDecision decision,
            SecurityAuditOperation operation,
            SecurityAuditReason reason)
        {
            Assert.AreEqual(
                PeerInboundRequestDecisionStatus.AuthenticationRejected,
                decision.Status);
            Assert.IsFalse(decision.IsAdmitted);
            Assert.IsFalse(decision.HasVerifiedSignature);
            Assert.IsFalse(decision.CanSignErrorResponse);
            Assert.IsFalse(decision.RetryAfterSeconds.HasValue);
            Assert.IsNotNull(decision.AuditInput);
            Assert.AreEqual(
                SecurityAuditEventId.PeerAuthenticationRejected,
                decision.AuditInput.EventId);
            Assert.AreEqual(
                SecurityAuditBoundary.Peer,
                decision.AuditInput.Boundary);
            Assert.AreEqual(operation, decision.AuditInput.Operation);
            Assert.AreEqual(reason, decision.AuditInput.Reason);
        }

        private static byte[] CreateBody()
        {
            return Encoding.UTF8.GetBytes("<Request />");
        }

        private static byte[] HandshakeRequestKey()
        {
            return Bytes(0, 32);
        }

        private static byte[] SessionRequestKey()
        {
            return Bytes(128, 32);
        }

        private static byte[] SessionId()
        {
            return Bytes(192, 16);
        }

        private static byte[] Bytes(int start, int count)
        {
            var value = new byte[count];
            for (int index = 0; index < value.Length; index++)
            {
                value[index] = unchecked((byte)(start + index));
            }

            return value;
        }
    }
}
