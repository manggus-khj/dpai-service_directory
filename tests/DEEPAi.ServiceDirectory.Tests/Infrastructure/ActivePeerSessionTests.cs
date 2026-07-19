using System;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class ActivePeerSessionTests
    {
        private static readonly Guid LocalInstanceId =
            new Guid("11111111-1111-1111-1111-111111111111");

        private static readonly Guid PeerInstanceId =
            new Guid("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

        [TestMethod]
        public void HandshakeFactoryDerivesFixedDirectionKeysAndCopiesInputs()
        {
            byte[] pairRoot = SequentialBytes(0, 32);
            byte[] requestNonce = SequentialBytes(32, 32);
            byte[] responseNonce = SequentialBytes(64, 32);
            byte[] sessionId = SequentialBytes(96, 16);
            DateTimeOffset responseTime = new DateTimeOffset(
                2026,
                7,
                18,
                1,
                2,
                3,
                TimeSpan.Zero);

            ActivePeerSession session =
                ActivePeerSession.CreateFromHandshake(
                    LocalInstanceId,
                    PeerInstanceId,
                    42UL,
                    pairRoot,
                    requestNonce,
                    responseNonce,
                    sessionId,
                    responseTime,
                    responseTime.AddMinutes(10));

            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "as76V2zzG1kia6KNw5T9w5Oddvdd7A66DTD/k8pYWvs="),
                session.CopyIncomingRequestAuthenticationKey());
            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "11/2F9NgF7qMZIpo8hNiK/jULF+FCKTh1XzOF6QB+Lc="),
                session.CopyIncomingResponseAuthenticationKey());
            Assert.AreEqual(
                responseTime.AddMinutes(10),
                session.ExpiresAtUtc);

            Array.Clear(pairRoot, 0, pairRoot.Length);
            Array.Clear(requestNonce, 0, requestNonce.Length);
            Array.Clear(responseNonce, 0, responseNonce.Length);
            Array.Clear(sessionId, 0, sessionId.Length);

            CollectionAssert.AreEqual(
                SequentialBytes(96, 16),
                session.CopySessionId());
            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "as76V2zzG1kia6KNw5T9w5Oddvdd7A66DTD/k8pYWvs="),
                session.CopyIncomingRequestAuthenticationKey());
        }

        [TestMethod]
        public void HandshakeFactoryRequiresExactTenMinuteExpiry()
        {
            DateTimeOffset responseTime = new DateTimeOffset(
                2026,
                7,
                18,
                1,
                2,
                3,
                TimeSpan.Zero);

            Assert.ThrowsExactly<ArgumentException>(
                () => ActivePeerSession.CreateFromHandshake(
                    LocalInstanceId,
                    PeerInstanceId,
                    42UL,
                    SequentialBytes(0, 32),
                    SequentialBytes(32, 32),
                    SequentialBytes(64, 32),
                    SequentialBytes(96, 16),
                    responseTime,
                    responseTime.AddMinutes(10).AddTicks(1)));
        }

        [TestMethod]
        public void HandshakeFactoryUsesUtcInstantsForExpiryValidation()
        {
            DateTimeOffset responseTime = new DateTimeOffset(
                2026,
                7,
                18,
                10,
                2,
                3,
                TimeSpan.FromHours(9));

            ActivePeerSession session =
                ActivePeerSession.CreateFromHandshake(
                    LocalInstanceId,
                    PeerInstanceId,
                    42UL,
                    SequentialBytes(0, 32),
                    SequentialBytes(32, 32),
                    SequentialBytes(64, 32),
                    SequentialBytes(96, 16),
                    responseTime,
                    responseTime.AddMinutes(10));

            Assert.AreEqual(TimeSpan.Zero, session.ExpiresAtUtc.Offset);
            Assert.AreEqual(
                responseTime.ToUniversalTime().AddMinutes(10),
                session.ExpiresAtUtc);
        }

        [TestMethod]
        public void DisposeIsIdempotentAndClosesSensitiveSessionAccess()
        {
            DateTimeOffset responseTime = new DateTimeOffset(
                2026,
                7,
                18,
                1,
                2,
                3,
                TimeSpan.Zero);
            ActivePeerSession session =
                ActivePeerSession.CreateFromHandshake(
                    LocalInstanceId,
                    PeerInstanceId,
                    42UL,
                    SequentialBytes(0, 32),
                    SequentialBytes(32, 32),
                    SequentialBytes(64, 32),
                    SequentialBytes(96, 16),
                    responseTime,
                    responseTime.AddMinutes(10));

            session.Dispose();
            session.Dispose();

            Assert.ThrowsExactly<ObjectDisposedException>(
                () => session.CopySessionId());
            Assert.ThrowsExactly<ObjectDisposedException>(
                () => session.CopyIncomingRequestAuthenticationKey());
            Assert.ThrowsExactly<ObjectDisposedException>(
                () => session.CopyIncomingResponseAuthenticationKey());
            Assert.ThrowsExactly<ObjectDisposedException>(
                () =>
                {
                    TimeSpan ignored;
                    session.TryGetRemainingLifetime(
                        responseTime,
                        out ignored);
                });
        }

        private static byte[] SequentialBytes(int start, int count)
        {
            var value = new byte[count];
            for (int index = 0; index < value.Length; index++)
            {
                value[index] = checked((byte)(start + index));
            }

            return value;
        }
    }
}
