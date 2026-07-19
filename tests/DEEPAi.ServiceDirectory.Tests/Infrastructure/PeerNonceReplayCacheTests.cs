using System;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PeerNonceReplayCacheTests
    {
        private static readonly Guid FirstPeer = new Guid(
            "11111111-1111-1111-1111-111111111111");
        private static readonly Guid SecondPeer = new Guid(
            "22222222-2222-2222-2222-222222222222");

        [TestMethod]
        public void FullCacheRejectsNewNonceWithoutEvictingLiveEntry()
        {
            long monotonicTimestamp = 0;
            var cache = new PeerNonceReplayCache(
                1,
                () => monotonicTimestamp,
                1);
            byte[] firstNonce = CreateNonce(0);
            byte[] secondNonce = CreateNonce(16);

            Assert.AreEqual(
                PeerNonceRegistrationResult.Accepted,
                cache.RegisterNonSession(FirstPeer, 1, firstNonce));
            Assert.AreEqual(
                PeerNonceRegistrationResult.CapacityExceeded,
                cache.RegisterNonSession(FirstPeer, 1, secondNonce));
            Assert.AreEqual(
                PeerNonceRegistrationResult.ReplayDetected,
                cache.RegisterNonSession(FirstPeer, 1, firstNonce));
            Assert.AreEqual(1, cache.Count);

            monotonicTimestamp = 599;
            Assert.AreEqual(1, cache.Count);
            monotonicTimestamp = 600;
            Assert.AreEqual(
                PeerNonceRegistrationResult.Accepted,
                cache.RegisterNonSession(FirstPeer, 1, secondNonce));
            Assert.AreEqual(1, cache.Count);
        }

        [TestMethod]
        public void ReplayScopeIncludesPeerEpochAndSession()
        {
            long monotonicTimestamp = 0;
            var cache = new PeerNonceReplayCache(
                4,
                () => monotonicTimestamp,
                1);
            byte[] nonce = CreateNonce(0);
            byte[] sessionId = CreateNonce(64);

            Assert.AreEqual(
                PeerNonceRegistrationResult.Accepted,
                cache.RegisterNonSession(FirstPeer, 1, nonce));
            Assert.AreEqual(
                PeerNonceRegistrationResult.Accepted,
                cache.RegisterNonSession(SecondPeer, 1, nonce));
            Assert.AreEqual(
                PeerNonceRegistrationResult.Accepted,
                cache.RegisterNonSession(FirstPeer, 2, nonce));
            Assert.AreEqual(
                PeerNonceRegistrationResult.Accepted,
                cache.RegisterSession(
                    FirstPeer,
                    1,
                    sessionId,
                    nonce,
                    TimeSpan.FromMinutes(10)));
            Assert.AreEqual(4, cache.Count);
        }

        [TestMethod]
        public void SessionNonceRemainsUntilSuppliedSessionExpiry()
        {
            long monotonicTimestamp = 0;
            var cache = new PeerNonceReplayCache(
                1,
                () => monotonicTimestamp,
                1);
            byte[] nonce = CreateNonce(0);
            byte[] sessionId = CreateNonce(32);

            Assert.AreEqual(
                PeerNonceRegistrationResult.Accepted,
                cache.RegisterSession(
                    FirstPeer,
                    1,
                    sessionId,
                    nonce,
                    TimeSpan.FromSeconds(90)));
            monotonicTimestamp = 89;
            Assert.AreEqual(
                PeerNonceRegistrationResult.ReplayDetected,
                cache.RegisterSession(
                    FirstPeer,
                    1,
                    sessionId,
                    nonce,
                    TimeSpan.FromSeconds(1)));
            monotonicTimestamp = 90;
            Assert.AreEqual(
                PeerNonceRegistrationResult.Accepted,
                cache.RegisterSession(
                    FirstPeer,
                    1,
                    sessionId,
                    nonce,
                    TimeSpan.FromSeconds(1)));
        }

        [TestMethod]
        public void ConcurrentDuplicateRegistrationAcceptsExactlyOne()
        {
            var cache = new PeerNonceReplayCache(8, () => 0, 1);
            byte[] nonce = CreateNonce(0);
            int accepted = 0;
            int replayed = 0;

            Parallel.For(
                0,
                32,
                index =>
                {
                    PeerNonceRegistrationResult result =
                        cache.RegisterNonSession(FirstPeer, 1, nonce);
                    if (result == PeerNonceRegistrationResult.Accepted)
                    {
                        Interlocked.Increment(ref accepted);
                    }
                    else if (result
                        == PeerNonceRegistrationResult.ReplayDetected)
                    {
                        Interlocked.Increment(ref replayed);
                    }
                });

            Assert.AreEqual(1, accepted);
            Assert.AreEqual(31, replayed);
            Assert.AreEqual(1, cache.Count);
        }

        private static byte[] CreateNonce(int start)
        {
            var result = new byte[PeerAuthenticationContract.NonceLength];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = checked((byte)(start + index));
            }

            return result;
        }
    }
}
