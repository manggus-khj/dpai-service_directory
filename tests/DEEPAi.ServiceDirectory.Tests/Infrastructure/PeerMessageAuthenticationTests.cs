using System;
using System.Text;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PeerMessageAuthenticationTests
    {
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly Guid InitiatorInstanceId = new Guid(
            "7a1c3bb2-9e8b-4a8d-b404-f670f746eb77");
        private static readonly Guid ResponderInstanceId = new Guid(
            "9f2ed127-9834-42b4-a379-eaad9df8fcec");
        private static readonly Guid OtherInstanceId = new Guid(
            "3f753250-f078-43a8-a799-cfb51df83c5e");
        private static readonly DateTimeOffset RequestTimestamp =
            new DateTimeOffset(
                2026,
                7,
                17,
                2,
                0,
                0,
                TimeSpan.Zero).AddMilliseconds(123);
        private static readonly DateTimeOffset ResponseTimestamp =
            new DateTimeOffset(
                2026,
                7,
                17,
                2,
                0,
                0,
                TimeSpan.Zero).AddMilliseconds(456);

        [TestMethod]
        public void HeaderValuesRequireCanonicalGuidEpochUtcAndBase64()
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    "X-DPAI-Instance-Id",
                    "X-DPAI-Key-Epoch",
                    "X-DPAI-Session-Id",
                    "X-DPAI-Timestamp",
                    "X-DPAI-Nonce",
                    "X-DPAI-Signature"
                },
                new[]
                {
                    PeerAuthenticationContract.InstanceIdHeaderName,
                    PeerAuthenticationContract.KeyEpochHeaderName,
                    PeerAuthenticationContract.SessionIdHeaderName,
                    PeerAuthenticationContract.TimestampHeaderName,
                    PeerAuthenticationContract.NonceHeaderName,
                    PeerAuthenticationContract.SignatureHeaderName
                });

            Guid parsedInstanceId;
            string canonicalInstanceId = InitiatorInstanceId.ToString("D");
            Assert.IsTrue(
                PeerAuthenticationContract.TryParseCanonicalInstanceId(
                    canonicalInstanceId,
                    out parsedInstanceId));
            Assert.AreEqual(InitiatorInstanceId, parsedInstanceId);
            Assert.IsFalse(
                PeerAuthenticationContract.TryParseCanonicalInstanceId(
                    canonicalInstanceId.ToUpperInvariant(),
                    out parsedInstanceId));

            ulong keyEpoch;
            Assert.IsTrue(
                PeerAuthenticationContract.TryParseCanonicalKeyEpoch(
                    "7",
                    out keyEpoch));
            Assert.AreEqual(7UL, keyEpoch);
            Assert.IsFalse(
                PeerAuthenticationContract.TryParseCanonicalKeyEpoch(
                    "07",
                    out keyEpoch));
            Assert.IsFalse(
                PeerAuthenticationContract.TryParseCanonicalKeyEpoch(
                    "0",
                    out keyEpoch));

            DateTimeOffset parsedTimestamp;
            const string canonicalTimestamp =
                "2026-07-17T02:00:00.123Z";
            Assert.IsTrue(
                PeerAuthenticationContract.TryParseCanonicalTimestamp(
                    canonicalTimestamp,
                    out parsedTimestamp));
            Assert.AreEqual(RequestTimestamp, parsedTimestamp);
            Assert.IsFalse(
                PeerAuthenticationContract.TryParseCanonicalTimestamp(
                    "2026-07-17T02:00:00Z",
                    out parsedTimestamp));
            Assert.IsFalse(
                PeerAuthenticationContract.TryParseCanonicalTimestamp(
                    "2026-07-17T02:00:00.123+00:00",
                    out parsedTimestamp));

            byte[] nonce = CreateSequentialBytes(0, 16);
            byte[] parsedNonce;
            string encodedNonce = Convert.ToBase64String(nonce);
            Assert.IsTrue(
                PeerAuthenticationContract.TryParseCanonicalNonce(
                    encodedNonce,
                    out parsedNonce));
            CollectionAssert.AreEqual(nonce, parsedNonce);
            Array.Clear(parsedNonce, 0, parsedNonce.Length);
            Assert.IsFalse(
                PeerAuthenticationContract.TryParseCanonicalNonce(
                    encodedNonce + " ",
                    out parsedNonce));
            Assert.IsFalse(
                PeerAuthenticationContract.TryParseCanonicalNonce(
                    new string('A', 4096),
                    out parsedNonce));
            Assert.IsNull(parsedNonce);

            byte[] parsedSessionId;
            Assert.IsTrue(
                PeerAuthenticationContract.TryParseCanonicalSessionId(
                    encodedNonce,
                    out parsedSessionId));
            CollectionAssert.AreEqual(nonce, parsedSessionId);
            Array.Clear(parsedSessionId, 0, parsedSessionId.Length);
            Assert.IsFalse(
                PeerAuthenticationContract.TryParseCanonicalSessionId(
                    new string('A', 4096),
                    out parsedSessionId));
            Assert.IsNull(parsedSessionId);

            byte[] signature = CreateSequentialBytes(32, 32);
            byte[] parsedSignature;
            Assert.IsTrue(
                PeerAuthenticationContract.TryParseCanonicalSignature(
                    Convert.ToBase64String(signature),
                    out parsedSignature));
            CollectionAssert.AreEqual(signature, parsedSignature);
            Array.Clear(parsedSignature, 0, parsedSignature.Length);
            Assert.IsFalse(
                PeerAuthenticationContract.TryParseCanonicalSignature(
                    new string('A', 4096),
                    out parsedSignature));
            Assert.IsNull(parsedSignature);
        }

        [TestMethod]
        public void TimestampFreshnessIncludesExactlySixtySeconds()
        {
            Assert.IsTrue(
                PeerAuthenticationContract.IsTimestampFresh(
                    RequestTimestamp,
                    RequestTimestamp.AddSeconds(60)));
            Assert.IsTrue(
                PeerAuthenticationContract.IsTimestampFresh(
                    RequestTimestamp,
                    RequestTimestamp.AddSeconds(-60)));
            Assert.IsFalse(
                PeerAuthenticationContract.IsTimestampFresh(
                    RequestTimestamp,
                    RequestTimestamp.AddMilliseconds(60001)));
            Assert.IsFalse(
                PeerAuthenticationContract.IsTimestampFresh(
                    RequestTimestamp,
                    RequestTimestamp.AddMilliseconds(-60001)));
        }

        [TestMethod]
        public void HttpMethodNormalizationRejectsUnicodeCaseFolding()
        {
            Assert.AreEqual(
                "POST",
                PeerAuthenticationContract.NormalizeHttpMethod("post"));
            Assert.ThrowsExactly<ArgumentException>(
                () => PeerAuthenticationContract.NormalizeHttpMethod(
                    "po\u017ft"));
            Assert.ThrowsExactly<ArgumentException>(
                () => PeerAuthenticationContract.NormalizeHttpMethod(
                    "POST "));
        }

        [TestMethod]
        public void RequestTargetUsesRfc3986EncodingAndSortedDuplicateQuery()
        {
            PeerCanonicalRequestTarget target =
                PeerCanonicalRequestTarget.Create(
                    "/api/sync/ex change",
                    new[]
                    {
                        new PeerQueryParameter("z", "last"),
                        new PeerQueryParameter("a", "first"),
                        new PeerQueryParameter("a", "한 글"),
                        new PeerQueryParameter("a", string.Empty),
                        new PeerQueryParameter("a", "first")
                    });

            Assert.AreEqual(
                "/api/sync/ex%20change"
                    + "?a=&a=%ED%95%9C%20%EA%B8%80"
                    + "&a=first&a=first&z=last",
                target.Value);
            Assert.ThrowsExactly<ArgumentException>(
                () => PeerCanonicalRequestTarget.Create(
                    "/api/../sync",
                    null));
            Assert.ThrowsExactly<ArgumentException>(
                () => PeerCanonicalRequestTarget.Create(
                    "/api/\ud800",
                    null));
        }

        [TestMethod]
        public void RequestSignatureMatchesFixedCanonicalVectorAndBindsBody()
        {
            byte[] key = CreateSequentialBytes(32, 32);
            PeerRequestAuthenticationData request = CreateRequest(
                RequestTimestamp,
                CreateSequentialBytes(0, 16),
                null,
                CreateRequestBody());

            byte[] signature = PeerMessageAuthenticator
                .CreateRequestSignature(key, request);

            Assert.AreEqual(
                "7f8flv6u4TxdbzbyVsvxgM2YScNt5jrlFRtG+OQ8vRs=",
                Convert.ToBase64String(signature));
            Assert.IsTrue(
                PeerMessageAuthenticator.VerifyRequestSignature(
                    key,
                    request,
                    signature));

            PeerRequestAuthenticationData changedBody = CreateRequest(
                RequestTimestamp,
                CreateSequentialBytes(0, 16),
                null,
                StrictUtf8.GetBytes(
                    "<Handshake xmlns=\"urn:deepai:service-directory:peer\">"
                    + "</Handshake>"));
            Assert.IsFalse(
                PeerMessageAuthenticator.VerifyRequestSignature(
                    key,
                    changedBody,
                    signature));
        }

        [TestMethod]
        public void ResponseSignatureMatchesFixedVectorAndBindsRequestNonce()
        {
            byte[] key = CreateSequentialBytes(64, 32);
            PeerResponseAuthenticationData response = CreateResponse(
                CreateSequentialBytes(0, 16));

            byte[] signature = PeerMessageAuthenticator
                .CreateResponseSignature(key, response);
            PeerPairAuthenticationContext authenticationContext =
                CreatePairAuthenticationContext(
                    InitiatorInstanceId,
                    ResponderInstanceId,
                    null,
                    key);

            Assert.AreEqual(
                "aBa1VIg/DLgRuQKkPiVfBV/5XX28Q/+BLjGGspeJHOs=",
                Convert.ToBase64String(signature));
            Assert.AreEqual(
                PeerResponseAuthenticationResult.Authenticated,
                PeerMessageAuthenticator.AuthenticateHandshakeResponse(
                    authenticationContext,
                    response,
                    signature,
                    ResponseTimestamp.AddSeconds(60),
                    CreateSequentialBytes(0, 16)));
            Assert.AreEqual(
                PeerResponseAuthenticationResult.InvalidSession,
                PeerMessageAuthenticator.AuthenticateRevokeResponse(
                    authenticationContext,
                    response,
                    signature,
                    ResponseTimestamp,
                    CreateSequentialBytes(0, 16)));

            PeerResponseAuthenticationData wrongRequestNonce =
                CreateResponse(CreateSequentialBytes(1, 16));
            Assert.IsFalse(
                PeerMessageAuthenticator.VerifyResponseSignature(
                    key,
                    wrongRequestNonce,
                    signature));
            Assert.AreEqual(
                PeerResponseAuthenticationResult.ClockSkew,
                PeerMessageAuthenticator.AuthenticateHandshakeResponse(
                    authenticationContext,
                    response,
                    signature,
                    ResponseTimestamp.AddMilliseconds(60001),
                    CreateSequentialBytes(0, 16)));
        }

        [TestMethod]
        public void RequestAuthenticationCachesOnlyValidFreshSignature()
        {
            long monotonicTimestamp = 0;
            var cache = new PeerNonceReplayCache(
                1,
                () => monotonicTimestamp,
                1);
            byte[] key = CreateSequentialBytes(32, 32);
            PeerPairAuthenticationContext authenticationContext =
                CreatePairAuthenticationContext(
                    ResponderInstanceId,
                    InitiatorInstanceId,
                    key,
                    null);
            byte[] nonce = CreateSequentialBytes(0, 16);
            PeerRequestAuthenticationData request = CreateRequest(
                RequestTimestamp,
                nonce,
                null,
                CreateRequestBody());
            byte[] signature = PeerMessageAuthenticator
                .CreateRequestSignature(key, request);
            byte[] invalidSignature = (byte[])signature.Clone();
            invalidSignature[0] ^= 0x01;

            Assert.AreEqual(
                PeerRequestAuthenticationResult.InvalidSignature,
                PeerMessageAuthenticator.AuthenticateHandshakeRequest(
                    authenticationContext,
                    request,
                    invalidSignature,
                    RequestTimestamp,
                    cache));
            Assert.AreEqual(0, cache.Count);

            PeerRequestAuthenticationData staleRequest = CreateRequest(
                RequestTimestamp.AddMilliseconds(-60001),
                CreateSequentialBytes(16, 16),
                null,
                CreateRequestBody());
            byte[] staleSignature = PeerMessageAuthenticator
                .CreateRequestSignature(key, staleRequest);
            Assert.AreEqual(
                PeerRequestAuthenticationResult.ClockSkew,
                PeerMessageAuthenticator.AuthenticateHandshakeRequest(
                    authenticationContext,
                    staleRequest,
                    staleSignature,
                    RequestTimestamp,
                    cache));
            Assert.AreEqual(0, cache.Count);

            Assert.AreEqual(
                PeerRequestAuthenticationResult.Authenticated,
                PeerMessageAuthenticator.AuthenticateHandshakeRequest(
                    authenticationContext,
                    request,
                    signature,
                    RequestTimestamp,
                    cache));
            Assert.AreEqual(1, cache.Count);
            Assert.AreEqual(
                PeerRequestAuthenticationResult.ReplayDetected,
                PeerMessageAuthenticator.AuthenticateHandshakeRequest(
                    authenticationContext,
                    request,
                    signature,
                    RequestTimestamp,
                    cache));
            Assert.AreEqual(1, cache.Count);

            PeerRequestAuthenticationData secondRequest = CreateRequest(
                RequestTimestamp,
                CreateSequentialBytes(32, 16),
                null,
                CreateRequestBody());
            byte[] secondSignature = PeerMessageAuthenticator
                .CreateRequestSignature(key, secondRequest);
            Assert.AreEqual(
                PeerRequestAuthenticationResult
                    .ReplayCacheCapacityExceeded,
                PeerMessageAuthenticator.AuthenticateHandshakeRequest(
                    authenticationContext,
                    secondRequest,
                    secondSignature,
                    RequestTimestamp,
                    cache));
            Assert.AreEqual(1, cache.Count);
        }

        [TestMethod]
        public void SessionRequestRequiresExactStoredContextBeforeNonceRegistration()
        {
            long monotonicTimestamp = 0;
            var cache = new PeerNonceReplayCache(
                1,
                () => monotonicTimestamp,
                1);
            byte[] sessionId = CreateSequentialBytes(128, 16);
            byte[] requestKey = CreateSequentialBytes(96, 32);
            byte[] responseKey = CreateSequentialBytes(64, 32);
            PeerRequestAuthenticationData request = CreateSessionRequest(
                InitiatorInstanceId,
                ResponderInstanceId,
                7,
                sessionId,
                RequestTimestamp,
                CreateSequentialBytes(144, 16));
            byte[] signature = PeerMessageAuthenticator
                .CreateRequestSignature(requestKey, request);
            var activeSession = new ActivePeerSession(
                ResponderInstanceId,
                InitiatorInstanceId,
                7,
                sessionId,
                RequestTimestamp.AddMinutes(10),
                requestKey,
                responseKey);
            PeerPairAuthenticationContext pairAuthenticationContext =
                CreatePairAuthenticationContext(
                    ResponderInstanceId,
                    InitiatorInstanceId,
                    requestKey,
                    null);

            Assert.AreEqual(
                PeerRequestAuthenticationResult.InvalidSession,
                PeerMessageAuthenticator.AuthenticateHandshakeRequest(
                    pairAuthenticationContext,
                    request,
                    signature,
                    RequestTimestamp,
                    cache));

            PeerRequestAuthenticationData wrongSessionRequest =
                CreateSessionRequest(
                    InitiatorInstanceId,
                    ResponderInstanceId,
                    7,
                    CreateSequentialBytes(160, 16),
                    RequestTimestamp,
                    CreateSequentialBytes(144, 16));
            Assert.AreEqual(
                PeerRequestAuthenticationResult.InvalidSession,
                PeerMessageAuthenticator.AuthenticateSessionRequest(
                    activeSession,
                    wrongSessionRequest,
                    PeerMessageAuthenticator.CreateRequestSignature(
                        requestKey,
                        wrongSessionRequest),
                    RequestTimestamp,
                    cache));

            var wrongIdentity = new ActivePeerSession(
                OtherInstanceId,
                InitiatorInstanceId,
                7,
                sessionId,
                RequestTimestamp.AddMinutes(10),
                requestKey,
                responseKey);
            Assert.AreEqual(
                PeerRequestAuthenticationResult.InvalidSession,
                PeerMessageAuthenticator.AuthenticateSessionRequest(
                    wrongIdentity,
                    request,
                    signature,
                    RequestTimestamp,
                    cache));

            var wrongEpoch = new ActivePeerSession(
                ResponderInstanceId,
                InitiatorInstanceId,
                8,
                sessionId,
                RequestTimestamp.AddMinutes(10),
                requestKey,
                responseKey);
            Assert.AreEqual(
                PeerRequestAuthenticationResult.InvalidSession,
                PeerMessageAuthenticator.AuthenticateSessionRequest(
                    wrongEpoch,
                    request,
                    signature,
                    RequestTimestamp,
                    cache));

            var wrongRequestKey = new ActivePeerSession(
                ResponderInstanceId,
                InitiatorInstanceId,
                7,
                sessionId,
                RequestTimestamp.AddMinutes(10),
                CreateSequentialBytes(192, 32),
                responseKey);
            Assert.AreEqual(
                PeerRequestAuthenticationResult.InvalidSignature,
                PeerMessageAuthenticator.AuthenticateSessionRequest(
                    wrongRequestKey,
                    request,
                    signature,
                    RequestTimestamp,
                    cache));

            var expiredSession = new ActivePeerSession(
                ResponderInstanceId,
                InitiatorInstanceId,
                7,
                sessionId,
                RequestTimestamp,
                requestKey,
                responseKey);
            Assert.AreEqual(
                PeerRequestAuthenticationResult.InvalidSession,
                PeerMessageAuthenticator.AuthenticateSessionRequest(
                    expiredSession,
                    request,
                    signature,
                    RequestTimestamp,
                    cache));

            var invalidLongSession = new ActivePeerSession(
                ResponderInstanceId,
                InitiatorInstanceId,
                7,
                sessionId,
                RequestTimestamp.AddMinutes(10).AddMilliseconds(1),
                requestKey,
                responseKey);
            Assert.AreEqual(
                PeerRequestAuthenticationResult.InvalidSession,
                PeerMessageAuthenticator.AuthenticateSessionRequest(
                    invalidLongSession,
                    request,
                    signature,
                    RequestTimestamp,
                    cache));
            Assert.AreEqual(0, cache.Count);

            Assert.AreEqual(
                PeerRequestAuthenticationResult.Authenticated,
                PeerMessageAuthenticator.AuthenticateSessionRequest(
                    activeSession,
                    request,
                    signature,
                    RequestTimestamp,
                    cache));
            Assert.AreEqual(
                PeerRequestAuthenticationResult.ReplayDetected,
                PeerMessageAuthenticator.AuthenticateSessionRequest(
                    activeSession,
                    request,
                    signature,
                    RequestTimestamp,
                    cache));
        }

        [TestMethod]
        public void SessionResponseRequiresStoredBindingAndExpectedRequestNonce()
        {
            byte[] sessionId = CreateSequentialBytes(128, 16);
            byte[] requestKey = CreateSequentialBytes(96, 32);
            byte[] responseKey = CreateSequentialBytes(64, 32);
            byte[] requestNonce = CreateSequentialBytes(0, 16);
            PeerResponseAuthenticationData response = CreateSessionResponse(
                ResponderInstanceId,
                InitiatorInstanceId,
                7,
                sessionId,
                requestNonce);
            byte[] signature = PeerMessageAuthenticator
                .CreateResponseSignature(responseKey, response);
            var activeSession = new ActivePeerSession(
                InitiatorInstanceId,
                ResponderInstanceId,
                7,
                sessionId,
                ResponseTimestamp.AddMinutes(10),
                requestKey,
                responseKey);

            Assert.AreEqual(
                PeerResponseAuthenticationResult.Authenticated,
                PeerMessageAuthenticator.AuthenticateSessionResponse(
                    activeSession,
                    response,
                    signature,
                    ResponseTimestamp,
                    requestNonce));
            Assert.AreEqual(
                PeerResponseAuthenticationResult.UnexpectedRequestNonce,
                PeerMessageAuthenticator.AuthenticateSessionResponse(
                    activeSession,
                    response,
                    signature,
                    ResponseTimestamp,
                    CreateSequentialBytes(1, 16)));

            PeerResponseAuthenticationData wrongSessionResponse =
                CreateSessionResponse(
                    ResponderInstanceId,
                    InitiatorInstanceId,
                    7,
                    CreateSequentialBytes(160, 16),
                    requestNonce);
            Assert.AreEqual(
                PeerResponseAuthenticationResult.InvalidSession,
                PeerMessageAuthenticator.AuthenticateSessionResponse(
                    activeSession,
                    wrongSessionResponse,
                    PeerMessageAuthenticator.CreateResponseSignature(
                        responseKey,
                        wrongSessionResponse),
                    ResponseTimestamp,
                    requestNonce));

            var wrongResponseKey = new ActivePeerSession(
                InitiatorInstanceId,
                ResponderInstanceId,
                7,
                sessionId,
                ResponseTimestamp.AddMinutes(10),
                requestKey,
                CreateSequentialBytes(192, 32));
            Assert.AreEqual(
                PeerResponseAuthenticationResult.InvalidSignature,
                PeerMessageAuthenticator.AuthenticateSessionResponse(
                    wrongResponseKey,
                    response,
                    signature,
                    ResponseTimestamp,
                    requestNonce));

            var expiredSession = new ActivePeerSession(
                InitiatorInstanceId,
                ResponderInstanceId,
                7,
                sessionId,
                ResponseTimestamp,
                requestKey,
                responseKey);
            Assert.AreEqual(
                PeerResponseAuthenticationResult.InvalidSession,
                PeerMessageAuthenticator.AuthenticateSessionResponse(
                    expiredSession,
                    response,
                    signature,
                    ResponseTimestamp,
                    requestNonce));
        }

        [TestMethod]
        public void AuthenticationModelsDefensivelyCopyMutableArrays()
        {
            byte[] sessionId = CreateSequentialBytes(128, 16);
            byte[] nonce = CreateSequentialBytes(144, 16);
            byte[] body = CreateRequestBody();
            byte[] requestKey = CreateSequentialBytes(96, 32);
            byte[] responseKey = CreateSequentialBytes(64, 32);
            PeerRequestAuthenticationData request = CreateSessionRequest(
                InitiatorInstanceId,
                ResponderInstanceId,
                7,
                sessionId,
                RequestTimestamp,
                nonce,
                body);
            var activeSession = new ActivePeerSession(
                ResponderInstanceId,
                InitiatorInstanceId,
                7,
                sessionId,
                RequestTimestamp.AddMinutes(10),
                requestKey,
                responseKey);

            Array.Clear(sessionId, 0, sessionId.Length);
            Array.Clear(nonce, 0, nonce.Length);
            Array.Clear(body, 0, body.Length);
            Array.Clear(requestKey, 0, requestKey.Length);
            Array.Clear(responseKey, 0, responseKey.Length);

            byte[] sessionCopy = activeSession.CopySessionId();
            byte[] requestKeyCopy =
                activeSession.CopyIncomingRequestAuthenticationKey();
            byte[] responseKeyCopy =
                activeSession.CopyIncomingResponseAuthenticationKey();
            CollectionAssert.AreEqual(
                CreateSequentialBytes(128, 16),
                sessionCopy);
            CollectionAssert.AreEqual(
                CreateSequentialBytes(96, 32),
                requestKeyCopy);
            CollectionAssert.AreEqual(
                CreateSequentialBytes(64, 32),
                responseKeyCopy);

            Array.Clear(sessionCopy, 0, sessionCopy.Length);
            Array.Clear(requestKeyCopy, 0, requestKeyCopy.Length);
            Array.Clear(responseKeyCopy, 0, responseKeyCopy.Length);
            CollectionAssert.AreEqual(
                CreateSequentialBytes(128, 16),
                activeSession.CopySessionId());
            CollectionAssert.AreEqual(
                CreateSequentialBytes(144, 16),
                request.CopyNonce());
            CollectionAssert.AreEqual(
                CreateRequestBody(),
                request.CopyBody());

            byte[] signature = PeerMessageAuthenticator
                .CreateRequestSignature(
                    CreateSequentialBytes(96, 32),
                    request);
            Assert.AreEqual(
                PeerRequestAuthenticationResult.Authenticated,
                PeerMessageAuthenticator.AuthenticateSessionRequest(
                    activeSession,
                    request,
                    signature,
                    RequestTimestamp,
                    new PeerNonceReplayCache(1, () => 0, 1)));
        }

        private static PeerPairAuthenticationContext
            CreatePairAuthenticationContext(
            Guid localInstanceId,
            Guid peerInstanceId,
            byte[] handshakeRequestAuthenticationKey,
            byte[] handshakeResponseAuthenticationKey)
        {
            return new PeerPairAuthenticationContext(
                localInstanceId,
                peerInstanceId,
                7,
                handshakeRequestAuthenticationKey
                    ?? CreateSequentialBytes(32, 32),
                handshakeResponseAuthenticationKey
                    ?? CreateSequentialBytes(64, 32),
                CreateSequentialBytes(128, 32),
                CreateSequentialBytes(160, 32));
        }

        private static PeerRequestAuthenticationData CreateRequest(
            DateTimeOffset timestamp,
            byte[] nonce,
            byte[] sessionId,
            byte[] body)
        {
            return new PeerRequestAuthenticationData(
                InitiatorInstanceId,
                ResponderInstanceId,
                7,
                sessionId,
                "post",
                PeerCanonicalRequestTarget.Create(
                    "/api/sync/handshake",
                    null),
                "Application/XML; Charset=UTF-8",
                body,
                timestamp,
                nonce);
        }

        private static PeerRequestAuthenticationData CreateSessionRequest(
            Guid senderInstanceId,
            Guid receiverInstanceId,
            ulong keyEpoch,
            byte[] sessionId,
            DateTimeOffset timestamp,
            byte[] nonce)
        {
            return CreateSessionRequest(
                senderInstanceId,
                receiverInstanceId,
                keyEpoch,
                sessionId,
                timestamp,
                nonce,
                StrictUtf8.GetBytes(
                    "<Exchange xmlns=\"urn:deepai:service-directory:peer\""
                    + " Mode=\"Pull\"><PullRequest /></Exchange>"));
        }

        private static PeerRequestAuthenticationData CreateSessionRequest(
            Guid senderInstanceId,
            Guid receiverInstanceId,
            ulong keyEpoch,
            byte[] sessionId,
            DateTimeOffset timestamp,
            byte[] nonce,
            byte[] body)
        {
            return new PeerRequestAuthenticationData(
                senderInstanceId,
                receiverInstanceId,
                keyEpoch,
                sessionId,
                "POST",
                PeerCanonicalRequestTarget.Create(
                    "/api/sync/exchange",
                    null),
                "application/xml; charset=utf-8",
                body,
                timestamp,
                nonce);
        }

        private static PeerResponseAuthenticationData CreateResponse(
            byte[] requestNonce)
        {
            return new PeerResponseAuthenticationData(
                ResponderInstanceId,
                InitiatorInstanceId,
                7,
                CreateSequentialBytes(160, 16),
                "post",
                PeerCanonicalRequestTarget.Create(
                    "/api/sync/handshake",
                    null),
                200,
                "Application/XML; Charset=UTF-8",
                StrictUtf8.GetBytes(
                    "<Response xmlns=\"urn:deepai:service-directory:peer\">"
                    + "<Result>OK</Result><Code>0</Code><Message />"
                    + "</Response>"),
                ResponseTimestamp,
                CreateSequentialBytes(16, 16),
                requestNonce);
        }

        private static PeerResponseAuthenticationData CreateSessionResponse(
            Guid senderInstanceId,
            Guid receiverInstanceId,
            ulong keyEpoch,
            byte[] sessionId,
            byte[] requestNonce)
        {
            return new PeerResponseAuthenticationData(
                senderInstanceId,
                receiverInstanceId,
                keyEpoch,
                sessionId,
                "POST",
                PeerCanonicalRequestTarget.Create(
                    "/api/sync/exchange",
                    null),
                200,
                "application/xml; charset=utf-8",
                StrictUtf8.GetBytes(
                    "<Response xmlns=\"urn:deepai:service-directory:peer\">"
                    + "<Result>OK</Result><Code>0</Code><Message />"
                    + "</Response>"),
                ResponseTimestamp,
                CreateSequentialBytes(16, 16),
                requestNonce);
        }

        private static byte[] CreateRequestBody()
        {
            return StrictUtf8.GetBytes(
                "<Handshake xmlns=\"urn:deepai:service-directory:peer\" />");
        }

        private static byte[] CreateSequentialBytes(int start, int length)
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
