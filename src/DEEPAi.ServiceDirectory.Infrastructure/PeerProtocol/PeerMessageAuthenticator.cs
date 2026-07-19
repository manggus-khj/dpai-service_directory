using System;
using System.Security.Cryptography;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal enum PeerRequestAuthenticationResult
    {
        Authenticated = 0,
        InvalidSignature = 1,
        ClockSkew = 2,
        InvalidSession = 3,
        ReplayDetected = 4,
        ReplayCacheCapacityExceeded = 5
    }

    internal enum PeerResponseAuthenticationResult
    {
        Authenticated = 0,
        InvalidSignature = 1,
        ClockSkew = 2,
        InvalidSession = 3,
        UnexpectedRequestNonce = 4
    }

    internal static class PeerMessageAuthenticator
    {
        private enum NonSessionAuthenticationPurpose
        {
            HandshakeRequest = 1,
            HandshakeResponse = 2,
            RevokeRequest = 3,
            RevokeResponse = 4
        }

        public static byte[] CreateRequestSignature(
            byte[] authenticationKey,
            PeerRequestAuthenticationData request)
        {
            byte[] canonical = PeerMessageCanonicalizer.CreateRequest(request);
            try
            {
                return ComputeHmac(authenticationKey, canonical);
            }
            finally
            {
                Array.Clear(canonical, 0, canonical.Length);
            }
        }

        public static byte[] CreateResponseSignature(
            byte[] authenticationKey,
            PeerResponseAuthenticationData response)
        {
            byte[] canonical = PeerMessageCanonicalizer.CreateResponse(response);
            try
            {
                return ComputeHmac(authenticationKey, canonical);
            }
            finally
            {
                Array.Clear(canonical, 0, canonical.Length);
            }
        }

        public static bool VerifyRequestSignature(
            byte[] authenticationKey,
            PeerRequestAuthenticationData request,
            byte[] signature)
        {
            if (signature == null
                || signature.Length
                    != PeerAuthenticationContract.SignatureLength)
            {
                return false;
            }

            byte[] expected = CreateRequestSignature(
                authenticationKey,
                request);
            try
            {
                return PairingCryptography.FixedTimeEquals32(
                    expected,
                    signature);
            }
            finally
            {
                Array.Clear(expected, 0, expected.Length);
            }
        }

        public static bool VerifyResponseSignature(
            byte[] authenticationKey,
            PeerResponseAuthenticationData response,
            byte[] signature)
        {
            if (signature == null
                || signature.Length
                    != PeerAuthenticationContract.SignatureLength)
            {
                return false;
            }

            byte[] expected = CreateResponseSignature(
                authenticationKey,
                response);
            try
            {
                return PairingCryptography.FixedTimeEquals32(
                    expected,
                    signature);
            }
            finally
            {
                Array.Clear(expected, 0, expected.Length);
            }
        }

        public static PeerRequestAuthenticationResult
            AuthenticateHandshakeRequest(
            PeerPairAuthenticationContext authenticationContext,
            PeerRequestAuthenticationData request,
            byte[] signature,
            DateTimeOffset receivedAt,
            PeerNonceReplayCache replayCache)
        {
            return AuthenticateNonSessionRequest(
                authenticationContext,
                request,
                signature,
                receivedAt,
                replayCache,
                PeerAuthenticationContract.HandshakePath,
                NonSessionAuthenticationPurpose.HandshakeRequest);
        }

        public static PeerRequestAuthenticationResult AuthenticateRevokeRequest(
            PeerPairAuthenticationContext authenticationContext,
            PeerRequestAuthenticationData request,
            byte[] signature,
            DateTimeOffset receivedAt,
            PeerNonceReplayCache replayCache)
        {
            return AuthenticateNonSessionRequest(
                authenticationContext,
                request,
                signature,
                receivedAt,
                replayCache,
                PeerAuthenticationContract.RevokePath,
                NonSessionAuthenticationPurpose.RevokeRequest);
        }

        public static PeerRequestAuthenticationResult AuthenticateSessionRequest(
            ActivePeerSession activeSession,
            PeerRequestAuthenticationData request,
            byte[] signature,
            DateTimeOffset receivedAt,
            PeerNonceReplayCache replayCache)
        {
            if (activeSession == null)
            {
                throw new ArgumentNullException(nameof(activeSession));
            }

            ValidateRequestArguments(request, replayCache);
            TimeSpan remainingLifetime;
            if (!IsSessionRequestTarget(request)
                || !activeSession.MatchesInboundRequest(request)
                || !activeSession.TryGetRemainingLifetime(
                    receivedAt,
                    out remainingLifetime))
            {
                return PeerRequestAuthenticationResult.InvalidSession;
            }

            byte[] authenticationKey =
                activeSession.CopyIncomingRequestAuthenticationKey();
            try
            {
                if (!VerifyRequestSignature(
                    authenticationKey,
                    request,
                    signature))
                {
                    return PeerRequestAuthenticationResult.InvalidSignature;
                }
            }
            finally
            {
                Array.Clear(
                    authenticationKey,
                    0,
                    authenticationKey.Length);
            }

            DateTimeOffset receivedAtUtc = receivedAt.ToUniversalTime();
            if (!PeerAuthenticationContract.IsTimestampFresh(
                request.Timestamp,
                receivedAtUtc))
            {
                return PeerRequestAuthenticationResult.ClockSkew;
            }

            byte[] sessionId = activeSession.CopySessionId();
            byte[] nonce = request.CopyNonce();
            try
            {
                return MapRegistration(
                    replayCache.RegisterSession(
                        activeSession.PeerInstanceId,
                        activeSession.KeyEpoch,
                        sessionId,
                        nonce,
                        remainingLifetime));
            }
            finally
            {
                Array.Clear(sessionId, 0, sessionId.Length);
                Array.Clear(nonce, 0, nonce.Length);
            }
        }

        public static PeerResponseAuthenticationResult
            AuthenticateRevokeResponse(
            PeerPairAuthenticationContext authenticationContext,
            PeerResponseAuthenticationData response,
            byte[] signature,
            DateTimeOffset receivedAt,
            byte[] expectedRequestNonce)
        {
            return AuthenticateNonSessionResponse(
                authenticationContext,
                response,
                signature,
                receivedAt,
                expectedRequestNonce,
                PeerAuthenticationContract.RevokePath,
                false,
                NonSessionAuthenticationPurpose.RevokeResponse);
        }

        public static PeerResponseAuthenticationResult
            AuthenticateHandshakeResponse(
            PeerPairAuthenticationContext authenticationContext,
            PeerResponseAuthenticationData response,
            byte[] signature,
            DateTimeOffset receivedAt,
            byte[] expectedRequestNonce)
        {
            return AuthenticateNonSessionResponse(
                authenticationContext,
                response,
                signature,
                receivedAt,
                expectedRequestNonce,
                PeerAuthenticationContract.HandshakePath,
                true,
                NonSessionAuthenticationPurpose.HandshakeResponse);
        }

        public static PeerResponseAuthenticationResult
            AuthenticateSessionResponse(
            ActivePeerSession activeSession,
            PeerResponseAuthenticationData response,
            byte[] signature,
            DateTimeOffset receivedAt,
            byte[] expectedRequestNonce)
        {
            if (activeSession == null)
            {
                throw new ArgumentNullException(nameof(activeSession));
            }

            ValidateResponse(response);
            TimeSpan ignoredRemainingLifetime;
            if (!IsSessionResponseTarget(response)
                || !activeSession.MatchesInboundResponse(response)
                || !activeSession.TryGetRemainingLifetime(
                    receivedAt,
                    out ignoredRemainingLifetime))
            {
                return PeerResponseAuthenticationResult.InvalidSession;
            }

            byte[] authenticationKey =
                activeSession.CopyIncomingResponseAuthenticationKey();
            try
            {
                return AuthenticateResponseCore(
                    authenticationKey,
                    response,
                    signature,
                    receivedAt,
                    expectedRequestNonce);
            }
            finally
            {
                Array.Clear(
                    authenticationKey,
                    0,
                    authenticationKey.Length);
            }
        }

        private static PeerResponseAuthenticationResult AuthenticateResponseCore(
            byte[] authenticationKey,
            PeerResponseAuthenticationData response,
            byte[] signature,
            DateTimeOffset receivedAt,
            byte[] expectedRequestNonce)
        {
            PeerAuthenticationContract.ValidateExactLength(
                expectedRequestNonce,
                nameof(expectedRequestNonce),
                PeerAuthenticationContract.NonceLength);

            byte[] expectedRequestNonceCopy =
                (byte[])expectedRequestNonce.Clone();
            try
            {
                if (!VerifyResponseSignature(
                    authenticationKey,
                    response,
                    signature))
                {
                    return PeerResponseAuthenticationResult.InvalidSignature;
                }

                if (!PeerAuthenticationContract.IsTimestampFresh(
                    response.Timestamp,
                    receivedAt))
                {
                    return PeerResponseAuthenticationResult.ClockSkew;
                }

                byte[] actualRequestNonce = response.CopyRequestNonce();
                try
                {
                    return PeerAuthenticationContract.FixedTimeEquals16(
                        actualRequestNonce,
                        expectedRequestNonceCopy)
                        ? PeerResponseAuthenticationResult.Authenticated
                        : PeerResponseAuthenticationResult
                            .UnexpectedRequestNonce;
                }
                finally
                {
                    Array.Clear(
                        actualRequestNonce,
                        0,
                        actualRequestNonce.Length);
                }
            }
            finally
            {
                Array.Clear(
                    expectedRequestNonceCopy,
                    0,
                    expectedRequestNonceCopy.Length);
            }
        }

        private static PeerRequestAuthenticationResult
            AuthenticateNonSessionRequest(
            PeerPairAuthenticationContext authenticationContext,
            PeerRequestAuthenticationData request,
            byte[] signature,
            DateTimeOffset receivedAt,
            PeerNonceReplayCache replayCache,
            string expectedPath,
            NonSessionAuthenticationPurpose purpose)
        {
            ValidateAuthenticationContext(authenticationContext);
            ValidateRequestArguments(request, replayCache);
            if (request.HasSession
                || !authenticationContext.MatchesInboundRequest(request)
                || !IsExpectedTarget(
                    request.Method,
                    request.RequestTarget,
                    expectedPath))
            {
                return PeerRequestAuthenticationResult.InvalidSession;
            }

            byte[] authenticationKey = CopyAuthenticationKey(
                authenticationContext,
                purpose);
            try
            {
                if (!VerifyRequestSignature(
                    authenticationKey,
                    request,
                    signature))
                {
                    return PeerRequestAuthenticationResult.InvalidSignature;
                }
            }
            finally
            {
                Array.Clear(
                    authenticationKey,
                    0,
                    authenticationKey.Length);
            }

            DateTimeOffset receivedAtUtc = receivedAt.ToUniversalTime();
            if (!PeerAuthenticationContract.IsTimestampFresh(
                request.Timestamp,
                receivedAtUtc))
            {
                return PeerRequestAuthenticationResult.ClockSkew;
            }

            byte[] nonce = request.CopyNonce();
            try
            {
                return MapRegistration(
                    replayCache.RegisterNonSession(
                        authenticationContext.PeerInstanceId,
                        authenticationContext.KeyEpoch,
                        nonce));
            }
            finally
            {
                Array.Clear(nonce, 0, nonce.Length);
            }
        }

        private static PeerResponseAuthenticationResult
            AuthenticateNonSessionResponse(
            PeerPairAuthenticationContext authenticationContext,
            PeerResponseAuthenticationData response,
            byte[] signature,
            DateTimeOffset receivedAt,
            byte[] expectedRequestNonce,
            string expectedPath,
            bool allowSessionHeader,
            NonSessionAuthenticationPurpose purpose)
        {
            ValidateAuthenticationContext(authenticationContext);
            ValidateResponse(response);
            if ((!allowSessionHeader && response.HasSession)
                || !authenticationContext.MatchesInboundResponse(response)
                || !IsExpectedTarget(
                    response.RequestMethod,
                    response.RequestTarget,
                    expectedPath))
            {
                return PeerResponseAuthenticationResult.InvalidSession;
            }

            byte[] authenticationKey = CopyAuthenticationKey(
                authenticationContext,
                purpose);
            try
            {
                return AuthenticateResponseCore(
                    authenticationKey,
                    response,
                    signature,
                    receivedAt,
                    expectedRequestNonce);
            }
            finally
            {
                Array.Clear(
                    authenticationKey,
                    0,
                    authenticationKey.Length);
            }
        }

        private static byte[] CopyAuthenticationKey(
            PeerPairAuthenticationContext authenticationContext,
            NonSessionAuthenticationPurpose purpose)
        {
            switch (purpose)
            {
                case NonSessionAuthenticationPurpose.HandshakeRequest:
                    return authenticationContext
                        .CopyIncomingHandshakeRequestAuthenticationKey();
                case NonSessionAuthenticationPurpose.HandshakeResponse:
                    return authenticationContext
                        .CopyIncomingHandshakeResponseAuthenticationKey();
                case NonSessionAuthenticationPurpose.RevokeRequest:
                    return authenticationContext
                        .CopyIncomingRevokeRequestAuthenticationKey();
                case NonSessionAuthenticationPurpose.RevokeResponse:
                    return authenticationContext
                        .CopyIncomingRevokeResponseAuthenticationKey();
                default:
                    throw new ArgumentOutOfRangeException(nameof(purpose));
            }
        }

        private static bool IsSessionRequestTarget(
            PeerRequestAuthenticationData request)
        {
            return IsExpectedTarget(
                    request.Method,
                    request.RequestTarget,
                    PeerAuthenticationContract.ExchangePath)
                || IsExpectedTarget(
                    request.Method,
                    request.RequestTarget,
                    PeerAuthenticationContract.ReleasePath);
        }

        private static bool IsSessionResponseTarget(
            PeerResponseAuthenticationData response)
        {
            return IsExpectedTarget(
                    response.RequestMethod,
                    response.RequestTarget,
                    PeerAuthenticationContract.ExchangePath)
                || IsExpectedTarget(
                    response.RequestMethod,
                    response.RequestTarget,
                    PeerAuthenticationContract.ReleasePath);
        }

        private static bool IsExpectedTarget(
            string requestMethod,
            PeerCanonicalRequestTarget requestTarget,
            string expectedPath)
        {
            return StringComparer.Ordinal.Equals(requestMethod, "POST")
                && StringComparer.Ordinal.Equals(
                    requestTarget.Value,
                    expectedPath);
        }

        private static void ValidateRequestArguments(
            PeerRequestAuthenticationData request,
            PeerNonceReplayCache replayCache)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (replayCache == null)
            {
                throw new ArgumentNullException(nameof(replayCache));
            }
        }

        private static void ValidateAuthenticationContext(
            PeerPairAuthenticationContext authenticationContext)
        {
            if (authenticationContext == null)
            {
                throw new ArgumentNullException(
                    nameof(authenticationContext));
            }
        }

        private static void ValidateResponse(
            PeerResponseAuthenticationData response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }
        }

        private static PeerRequestAuthenticationResult MapRegistration(
            PeerNonceRegistrationResult registration)
        {
            switch (registration)
            {
                case PeerNonceRegistrationResult.Accepted:
                    return PeerRequestAuthenticationResult.Authenticated;
                case PeerNonceRegistrationResult.ReplayDetected:
                    return PeerRequestAuthenticationResult.ReplayDetected;
                case PeerNonceRegistrationResult.CapacityExceeded:
                    return PeerRequestAuthenticationResult
                        .ReplayCacheCapacityExceeded;
                default:
                    throw new InvalidOperationException(
                        "The replay cache returned an unknown result.");
            }
        }

        private static byte[] ComputeHmac(
            byte[] authenticationKey,
            byte[] canonicalMessage)
        {
            PeerAuthenticationContract.ValidateExactLength(
                authenticationKey,
                nameof(authenticationKey),
                PeerAuthenticationContract.AuthenticationKeyLength);

            byte[] keyCopy = (byte[])authenticationKey.Clone();
            try
            {
                using (var hmac = new HMACSHA256(keyCopy))
                {
                    byte[] signature = hmac.ComputeHash(canonicalMessage);
                    if (signature.Length
                        != PeerAuthenticationContract.SignatureLength)
                    {
                        Array.Clear(signature, 0, signature.Length);
                        throw new CryptographicException(
                            "HMAC-SHA256 returned an unexpected signature length.");
                    }

                    return signature;
                }
            }
            finally
            {
                Array.Clear(keyCopy, 0, keyCopy.Length);
            }
        }
    }
}
