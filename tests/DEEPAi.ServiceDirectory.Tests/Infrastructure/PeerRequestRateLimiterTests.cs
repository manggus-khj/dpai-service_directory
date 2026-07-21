using System;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PeerRequestRateLimiterTests
    {
        private static readonly Guid PeerInstanceId = Guid.Parse(
            "22222222-2222-2222-2222-222222222222");

        [TestMethod]
        public void HandshakeUsesTwelvePerMinuteWithBurstOfThree()
        {
            long now = 0;
            var limiter = CreateLimiter(() => now);

            for (int index = 0;
                index < PeerRequestRateLimiter.HandshakeBurstCapacity;
                index++)
            {
                PeerRateLimitDecision allowed = limiter.TryAcquire(
                    PeerInboundOperation.Handshake);
                Assert.IsTrue(allowed.IsAllowed);
                Assert.IsTrue(allowed.IsConfigured);
                Assert.IsFalse(allowed.RetryAfterSeconds.HasValue);
            }

            PeerRateLimitDecision denied = limiter.TryAcquire(
                PeerInboundOperation.Handshake);
            Assert.IsFalse(denied.IsAllowed);
            Assert.IsTrue(denied.IsConfigured);
            Assert.AreEqual(5, denied.RetryAfterSeconds);

            now = 4;
            PeerRateLimitDecision nearlyRefilled = limiter.TryAcquire(
                PeerInboundOperation.Handshake);
            Assert.IsFalse(nearlyRefilled.IsAllowed);
            Assert.AreEqual(1, nearlyRefilled.RetryAfterSeconds);

            now = 5;
            Assert.IsTrue(
                limiter.TryAcquire(PeerInboundOperation.Handshake)
                    .IsAllowed);
        }

        [TestMethod]
        public void ExchangeUsesThirtyRequestSlidingMinute()
        {
            long now = 0;
            var limiter = CreateLimiter(() => now);

            for (int index = 0;
                index < PeerRequestRateLimiter.ExchangeRequestsPerMinute;
                index++)
            {
                Assert.IsTrue(
                    limiter.TryAcquire(PeerInboundOperation.Exchange)
                        .IsAllowed);
            }

            PeerRateLimitDecision denied = limiter.TryAcquire(
                PeerInboundOperation.Exchange);
            Assert.IsFalse(denied.IsAllowed);
            Assert.AreEqual(60, denied.RetryAfterSeconds);

            now = 59;
            PeerRateLimitDecision oneSecondRemaining = limiter.TryAcquire(
                PeerInboundOperation.Exchange);
            Assert.IsFalse(oneSecondRemaining.IsAllowed);
            Assert.AreEqual(1, oneSecondRemaining.RetryAfterSeconds);

            now = 60;
            Assert.IsTrue(
                limiter.TryAcquire(PeerInboundOperation.Exchange)
                    .IsAllowed);
        }

        [TestMethod]
        public void PkiStateSharesTheExchangeRateBucket()
        {
            var limiter = CreateLimiter(() => 0);
            for (int index = 0;
                index < PeerRequestRateLimiter.ExchangeRequestsPerMinute - 1;
                index++)
            {
                Assert.IsTrue(limiter.TryAcquire(
                    PeerInboundOperation.Exchange).IsAllowed);
            }

            Assert.IsTrue(limiter.TryAcquire(
                PeerInboundOperation.PkiState).IsAllowed);
            Assert.IsFalse(limiter.TryAcquire(
                PeerInboundOperation.PkiState).IsAllowed);
        }

        [TestMethod]
        public void ReleaseAndRevokeRemainExplicitlyUnconfigured()
        {
            var limiter = CreateLimiter(() => 0);

            for (int index = 0; index < 100; index++)
            {
                AssertUnconfiguredAllowed(
                    limiter.TryAcquire(PeerInboundOperation.Release));
                AssertUnconfiguredAllowed(
                    limiter.TryAcquire(PeerInboundOperation.Revoke));
            }
        }

        [TestMethod]
        public void LimiterRequiresCanonicalTrustedBinding()
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => new PeerRequestRateLimiter(
                    "HTTP://10.0.0.2:21000",
                    PeerInstanceId,
                    () => 0,
                    1));
            Assert.ThrowsExactly<ArgumentException>(
                () => new PeerRequestRateLimiter(
                    "https://10.0.0.2:21000",
                    Guid.Empty,
                    () => 0,
                    1));
        }

        private static PeerRequestRateLimiter CreateLimiter(
            Func<long> timestampProvider)
        {
            return new PeerRequestRateLimiter(
                "https://10.0.0.2:21000",
                PeerInstanceId,
                timestampProvider,
                1);
        }

        private static void AssertUnconfiguredAllowed(
            PeerRateLimitDecision decision)
        {
            Assert.IsTrue(decision.IsAllowed);
            Assert.IsFalse(decision.IsConfigured);
            Assert.IsFalse(decision.RetryAfterSeconds.HasValue);
        }
    }
}
