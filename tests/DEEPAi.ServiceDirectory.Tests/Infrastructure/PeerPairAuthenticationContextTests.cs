using System;
using System.Text;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PeerPairAuthenticationContextTests
    {
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly Guid LocalInstanceId = new Guid(
            "9f2ed127-9834-42b4-a379-eaad9df8fcec");
        private static readonly Guid PeerInstanceId = new Guid(
            "7a1c3bb2-9e8b-4a8d-b404-f670f746eb77");
        private static readonly Guid OtherInstanceId = new Guid(
            "3f753250-f078-43a8-a799-cfb51df83c5e");
        private static readonly DateTimeOffset Timestamp =
            new DateTimeOffset(
                2026,
                7,
                17,
                2,
                0,
                0,
                TimeSpan.Zero).AddMilliseconds(123);

        [TestMethod]
        public void ContextValidatesBindingAndDefensivelyCopiesAllKeys()
        {
            byte[] handshakeRequestKey = Bytes(0, 32);
            byte[] handshakeResponseKey = Bytes(32, 32);
            byte[] revokeRequestKey = Bytes(64, 32);
            byte[] revokeResponseKey = Bytes(96, 32);
            var context = new PeerPairAuthenticationContext(
                LocalInstanceId,
                PeerInstanceId,
                7,
                handshakeRequestKey,
                handshakeResponseKey,
                revokeRequestKey,
                revokeResponseKey);

            Array.Clear(
                handshakeRequestKey,
                0,
                handshakeRequestKey.Length);
            Array.Clear(
                handshakeResponseKey,
                0,
                handshakeResponseKey.Length);
            Array.Clear(revokeRequestKey, 0, revokeRequestKey.Length);
            Array.Clear(revokeResponseKey, 0, revokeResponseKey.Length);

            Assert.AreEqual(LocalInstanceId, context.LocalInstanceId);
            Assert.AreEqual(PeerInstanceId, context.PeerInstanceId);
            Assert.AreEqual(7UL, context.KeyEpoch);
            AssertKeyCopy(
                Bytes(0, 32),
                context.CopyIncomingHandshakeRequestAuthenticationKey,
                context.CopyIncomingHandshakeRequestAuthenticationKey);
            AssertKeyCopy(
                Bytes(32, 32),
                context.CopyIncomingHandshakeResponseAuthenticationKey,
                context.CopyIncomingHandshakeResponseAuthenticationKey);
            AssertKeyCopy(
                Bytes(64, 32),
                context.CopyIncomingRevokeRequestAuthenticationKey,
                context.CopyIncomingRevokeRequestAuthenticationKey);
            AssertKeyCopy(
                Bytes(96, 32),
                context.CopyIncomingRevokeResponseAuthenticationKey,
                context.CopyIncomingRevokeResponseAuthenticationKey);

            Assert.ThrowsExactly<ArgumentException>(
                () => CreateContext(Guid.Empty, PeerInstanceId, 7));
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateContext(LocalInstanceId, Guid.Empty, 7));
            Assert.ThrowsExactly<ArgumentException>(
                () => CreateContext(
                    LocalInstanceId,
                    LocalInstanceId,
                    7));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => CreateContext(LocalInstanceId, PeerInstanceId, 0));

            for (int invalidIndex = 0; invalidIndex < 4; invalidIndex++)
            {
                byte[][] keys =
                {
                    Bytes(0, 32),
                    Bytes(32, 32),
                    Bytes(64, 32),
                    Bytes(96, 32)
                };
                keys[invalidIndex] = new byte[31];
                Assert.ThrowsExactly<ArgumentException>(
                    () => new PeerPairAuthenticationContext(
                        LocalInstanceId,
                        PeerInstanceId,
                        7,
                        keys[0],
                        keys[1],
                        keys[2],
                        keys[3]));
            }
        }

        [TestMethod]
        public void PairRootFactoryDerivesFixedKeysAndDisposeClosesContext()
        {
            byte[] pairRoot = Bytes(0, 32);
            PeerPairAuthenticationContext context =
                PeerPairAuthenticationContext.CreateFromPairRoot(
                    new Guid(
                        "11111111-1111-1111-1111-111111111111"),
                    new Guid(
                        "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                    42,
                    pairRoot);

            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "9eMpt5w0txG4uK1XusGsHSVIRt36ow/YfKGJ8mOAN30="),
                context.CopyIncomingHandshakeRequestAuthenticationKey());
            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "wxz21DWJInmWxxj9aFHY4Kgi4qsCD1AP/fZuyxHT4YI="),
                context.CopyIncomingHandshakeResponseAuthenticationKey());
            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "NFgDZiO3xoNAPDE+kINAAy/sVL+EBQaCXLUHvhoOwXw="),
                context.CopyIncomingRevokeRequestAuthenticationKey());
            CollectionAssert.AreEqual(
                Convert.FromBase64String(
                    "4oqaxg5AmxEA8NMqE6v1rIL7uHQVmKulNhICCQtWSbI="),
                context.CopyIncomingRevokeResponseAuthenticationKey());
            CollectionAssert.AreEqual(Bytes(0, 32), pairRoot);

            context.Dispose();
            context.Dispose();

            Assert.ThrowsExactly<ObjectDisposedException>(
                () => context
                    .CopyIncomingHandshakeRequestAuthenticationKey());
            Assert.ThrowsExactly<ObjectDisposedException>(
                () => context
                    .CopyIncomingHandshakeResponseAuthenticationKey());
            Assert.ThrowsExactly<ObjectDisposedException>(
                () => context.CopyIncomingRevokeRequestAuthenticationKey());
            Assert.ThrowsExactly<ObjectDisposedException>(
                () => context.CopyIncomingRevokeResponseAuthenticationKey());
        }

        [TestMethod]
        public void HandshakeBindingMismatchNeverRegistersNonce()
        {
            PeerPairAuthenticationContext context = CreateContext(
                LocalInstanceId,
                PeerInstanceId,
                7);
            byte[] nonce = Bytes(128, 16);
            var cache = new PeerNonceReplayCache(8, () => 0, 1);
            PeerRequestAuthenticationData[] mismatches =
            {
                CreateRequest(
                    OtherInstanceId,
                    LocalInstanceId,
                    7,
                    null,
                    PeerAuthenticationContract.HandshakePath,
                    nonce),
                CreateRequest(
                    PeerInstanceId,
                    OtherInstanceId,
                    7,
                    null,
                    PeerAuthenticationContract.HandshakePath,
                    nonce),
                CreateRequest(
                    PeerInstanceId,
                    LocalInstanceId,
                    8,
                    null,
                    PeerAuthenticationContract.HandshakePath,
                    nonce),
                CreateRequest(
                    PeerInstanceId,
                    LocalInstanceId,
                    7,
                    null,
                    PeerAuthenticationContract.RevokePath,
                    nonce)
            };

            foreach (PeerRequestAuthenticationData mismatch in mismatches)
            {
                byte[] signature = PeerMessageAuthenticator
                    .CreateRequestSignature(Bytes(0, 32), mismatch);
                Assert.AreEqual(
                    PeerRequestAuthenticationResult.InvalidSession,
                    PeerMessageAuthenticator.AuthenticateHandshakeRequest(
                        context,
                        mismatch,
                        signature,
                        Timestamp,
                        cache));
                Assert.AreEqual(0, cache.Count);
            }

            PeerRequestAuthenticationData valid = CreateRequest(
                PeerInstanceId,
                LocalInstanceId,
                7,
                null,
                PeerAuthenticationContract.HandshakePath,
                nonce);
            Assert.AreEqual(
                PeerRequestAuthenticationResult.Authenticated,
                PeerMessageAuthenticator.AuthenticateHandshakeRequest(
                    context,
                    valid,
                    PeerMessageAuthenticator.CreateRequestSignature(
                        Bytes(0, 32),
                        valid),
                    Timestamp,
                    cache));
            Assert.AreEqual(1, cache.Count);
        }

        [TestMethod]
        public void RequestPurposeKeysAndSessionAbsenceAreEnforced()
        {
            PeerPairAuthenticationContext context = CreateContext(
                LocalInstanceId,
                PeerInstanceId,
                7);
            PeerRequestAuthenticationData handshake = CreateRequest(
                PeerInstanceId,
                LocalInstanceId,
                7,
                null,
                PeerAuthenticationContract.HandshakePath,
                Bytes(128, 16));
            var handshakeCache = new PeerNonceReplayCache(8, () => 0, 1);
            Assert.AreEqual(
                PeerRequestAuthenticationResult.InvalidSignature,
                PeerMessageAuthenticator.AuthenticateHandshakeRequest(
                    context,
                    handshake,
                    PeerMessageAuthenticator.CreateRequestSignature(
                        Bytes(64, 32),
                        handshake),
                    Timestamp,
                    handshakeCache));
            Assert.AreEqual(0, handshakeCache.Count);

            PeerRequestAuthenticationData revoke = CreateRequest(
                PeerInstanceId,
                LocalInstanceId,
                7,
                null,
                PeerAuthenticationContract.RevokePath,
                Bytes(144, 16));
            var revokeCache = new PeerNonceReplayCache(8, () => 0, 1);
            Assert.AreEqual(
                PeerRequestAuthenticationResult.InvalidSignature,
                PeerMessageAuthenticator.AuthenticateRevokeRequest(
                    context,
                    revoke,
                    PeerMessageAuthenticator.CreateRequestSignature(
                        Bytes(0, 32),
                        revoke),
                    Timestamp,
                    revokeCache));
            Assert.AreEqual(0, revokeCache.Count);
            Assert.AreEqual(
                PeerRequestAuthenticationResult.Authenticated,
                PeerMessageAuthenticator.AuthenticateRevokeRequest(
                    context,
                    revoke,
                    PeerMessageAuthenticator.CreateRequestSignature(
                        Bytes(64, 32),
                        revoke),
                    Timestamp,
                    revokeCache));

            PeerRequestAuthenticationData revokeWithSession = CreateRequest(
                PeerInstanceId,
                LocalInstanceId,
                7,
                Bytes(160, 16),
                PeerAuthenticationContract.RevokePath,
                Bytes(176, 16));
            var sessionCache = new PeerNonceReplayCache(8, () => 0, 1);
            Assert.AreEqual(
                PeerRequestAuthenticationResult.InvalidSession,
                PeerMessageAuthenticator.AuthenticateRevokeRequest(
                    context,
                    revokeWithSession,
                    PeerMessageAuthenticator.CreateRequestSignature(
                        Bytes(64, 32),
                        revokeWithSession),
                    Timestamp,
                    sessionCache));
            Assert.AreEqual(0, sessionCache.Count);
        }

        [TestMethod]
        public void HandshakeResponseBindsContextTargetAndRequestNonce()
        {
            PeerPairAuthenticationContext context = CreateContext(
                LocalInstanceId,
                PeerInstanceId,
                7);
            byte[] expectedRequestNonce = Bytes(128, 16);
            PeerResponseAuthenticationData withoutSession = CreateResponse(
                PeerInstanceId,
                LocalInstanceId,
                7,
                null,
                PeerAuthenticationContract.HandshakePath,
                expectedRequestNonce);
            PeerResponseAuthenticationData withSession = CreateResponse(
                PeerInstanceId,
                LocalInstanceId,
                7,
                Bytes(160, 16),
                PeerAuthenticationContract.HandshakePath,
                expectedRequestNonce);

            AssertHandshakeResponseAuthenticated(
                context,
                withoutSession,
                expectedRequestNonce);
            AssertHandshakeResponseAuthenticated(
                context,
                withSession,
                expectedRequestNonce);

            PeerResponseAuthenticationData[] mismatches =
            {
                CreateResponse(
                    OtherInstanceId,
                    LocalInstanceId,
                    7,
                    null,
                    PeerAuthenticationContract.HandshakePath,
                    expectedRequestNonce),
                CreateResponse(
                    PeerInstanceId,
                    OtherInstanceId,
                    7,
                    null,
                    PeerAuthenticationContract.HandshakePath,
                    expectedRequestNonce),
                CreateResponse(
                    PeerInstanceId,
                    LocalInstanceId,
                    8,
                    null,
                    PeerAuthenticationContract.HandshakePath,
                    expectedRequestNonce),
                CreateResponse(
                    PeerInstanceId,
                    LocalInstanceId,
                    7,
                    null,
                    PeerAuthenticationContract.RevokePath,
                    expectedRequestNonce)
            };
            foreach (PeerResponseAuthenticationData mismatch in mismatches)
            {
                Assert.AreEqual(
                    PeerResponseAuthenticationResult.InvalidSession,
                    PeerMessageAuthenticator.AuthenticateHandshakeResponse(
                        context,
                        mismatch,
                        PeerMessageAuthenticator.CreateResponseSignature(
                            Bytes(32, 32),
                            mismatch),
                        Timestamp,
                        expectedRequestNonce));
            }

            Assert.AreEqual(
                PeerResponseAuthenticationResult.UnexpectedRequestNonce,
                PeerMessageAuthenticator.AuthenticateHandshakeResponse(
                    context,
                    withoutSession,
                    PeerMessageAuthenticator.CreateResponseSignature(
                        Bytes(32, 32),
                        withoutSession),
                    Timestamp,
                    Bytes(129, 16)));
            Assert.AreEqual(
                PeerResponseAuthenticationResult.InvalidSignature,
                PeerMessageAuthenticator.AuthenticateHandshakeResponse(
                    context,
                    withoutSession,
                    PeerMessageAuthenticator.CreateResponseSignature(
                        Bytes(96, 32),
                        withoutSession),
                    Timestamp,
                    expectedRequestNonce));
        }

        [TestMethod]
        public void RevokeResponseRequiresNoSessionAndRevokeResponseKey()
        {
            PeerPairAuthenticationContext context = CreateContext(
                LocalInstanceId,
                PeerInstanceId,
                7);
            byte[] expectedRequestNonce = Bytes(128, 16);
            PeerResponseAuthenticationData response = CreateResponse(
                PeerInstanceId,
                LocalInstanceId,
                7,
                null,
                PeerAuthenticationContract.RevokePath,
                expectedRequestNonce);
            Assert.AreEqual(
                PeerResponseAuthenticationResult.Authenticated,
                PeerMessageAuthenticator.AuthenticateRevokeResponse(
                    context,
                    response,
                    PeerMessageAuthenticator.CreateResponseSignature(
                        Bytes(96, 32),
                        response),
                    Timestamp,
                    expectedRequestNonce));
            Assert.AreEqual(
                PeerResponseAuthenticationResult.InvalidSignature,
                PeerMessageAuthenticator.AuthenticateRevokeResponse(
                    context,
                    response,
                    PeerMessageAuthenticator.CreateResponseSignature(
                        Bytes(32, 32),
                        response),
                    Timestamp,
                    expectedRequestNonce));

            PeerResponseAuthenticationData withSession = CreateResponse(
                PeerInstanceId,
                LocalInstanceId,
                7,
                Bytes(160, 16),
                PeerAuthenticationContract.RevokePath,
                expectedRequestNonce);
            Assert.AreEqual(
                PeerResponseAuthenticationResult.InvalidSession,
                PeerMessageAuthenticator.AuthenticateRevokeResponse(
                    context,
                    withSession,
                    PeerMessageAuthenticator.CreateResponseSignature(
                        Bytes(96, 32),
                        withSession),
                    Timestamp,
                    expectedRequestNonce));
        }

        [TestMethod]
        public void ClearedSourceKeysStillAuthenticateWithContextCopies()
        {
            byte[] handshakeRequestKey = Bytes(0, 32);
            byte[] handshakeResponseKey = Bytes(32, 32);
            byte[] revokeRequestKey = Bytes(64, 32);
            byte[] revokeResponseKey = Bytes(96, 32);
            var context = new PeerPairAuthenticationContext(
                LocalInstanceId,
                PeerInstanceId,
                7,
                handshakeRequestKey,
                handshakeResponseKey,
                revokeRequestKey,
                revokeResponseKey);
            Array.Clear(
                handshakeRequestKey,
                0,
                handshakeRequestKey.Length);
            Array.Clear(
                handshakeResponseKey,
                0,
                handshakeResponseKey.Length);
            Array.Clear(revokeRequestKey, 0, revokeRequestKey.Length);
            Array.Clear(revokeResponseKey, 0, revokeResponseKey.Length);

            PeerRequestAuthenticationData request = CreateRequest(
                PeerInstanceId,
                LocalInstanceId,
                7,
                null,
                PeerAuthenticationContract.HandshakePath,
                Bytes(128, 16));
            Assert.AreEqual(
                PeerRequestAuthenticationResult.Authenticated,
                PeerMessageAuthenticator.AuthenticateHandshakeRequest(
                    context,
                    request,
                    PeerMessageAuthenticator.CreateRequestSignature(
                        Bytes(0, 32),
                        request),
                    Timestamp,
                    new PeerNonceReplayCache(8, () => 0, 1)));
        }

        private static PeerPairAuthenticationContext CreateContext(
            Guid localInstanceId,
            Guid peerInstanceId,
            ulong keyEpoch)
        {
            return new PeerPairAuthenticationContext(
                localInstanceId,
                peerInstanceId,
                keyEpoch,
                Bytes(0, 32),
                Bytes(32, 32),
                Bytes(64, 32),
                Bytes(96, 32));
        }

        private static PeerRequestAuthenticationData CreateRequest(
            Guid senderInstanceId,
            Guid receiverInstanceId,
            ulong keyEpoch,
            byte[] sessionId,
            string path,
            byte[] nonce)
        {
            return new PeerRequestAuthenticationData(
                senderInstanceId,
                receiverInstanceId,
                keyEpoch,
                sessionId,
                "POST",
                PeerCanonicalRequestTarget.Create(path, null),
                "application/xml; charset=utf-8",
                StrictUtf8.GetBytes("<Request />"),
                Timestamp,
                nonce);
        }

        private static PeerResponseAuthenticationData CreateResponse(
            Guid senderInstanceId,
            Guid receiverInstanceId,
            ulong keyEpoch,
            byte[] sessionId,
            string path,
            byte[] requestNonce)
        {
            return new PeerResponseAuthenticationData(
                senderInstanceId,
                receiverInstanceId,
                keyEpoch,
                sessionId,
                "POST",
                PeerCanonicalRequestTarget.Create(path, null),
                200,
                "application/xml; charset=utf-8",
                StrictUtf8.GetBytes("<Response />"),
                Timestamp,
                Bytes(144, 16),
                requestNonce);
        }

        private static void AssertHandshakeResponseAuthenticated(
            PeerPairAuthenticationContext context,
            PeerResponseAuthenticationData response,
            byte[] expectedRequestNonce)
        {
            Assert.AreEqual(
                PeerResponseAuthenticationResult.Authenticated,
                PeerMessageAuthenticator.AuthenticateHandshakeResponse(
                    context,
                    response,
                    PeerMessageAuthenticator.CreateResponseSignature(
                        Bytes(32, 32),
                        response),
                    Timestamp,
                    expectedRequestNonce));
        }

        private static void AssertKeyCopy(
            byte[] expected,
            Func<byte[]> firstCopy,
            Func<byte[]> secondCopy)
        {
            byte[] first = firstCopy();
            CollectionAssert.AreEqual(expected, first);
            first[0] ^= 0xff;
            byte[] second = secondCopy();
            CollectionAssert.AreEqual(expected, second);
            Assert.AreNotSame(first, second);
        }

        private static byte[] Bytes(int start, int length)
        {
            var result = new byte[length];
            for (int index = 0; index < length; index++)
            {
                result[index] = checked((byte)(start + index));
            }

            return result;
        }
    }
}
