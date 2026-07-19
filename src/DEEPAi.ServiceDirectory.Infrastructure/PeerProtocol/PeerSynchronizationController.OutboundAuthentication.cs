using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public sealed partial class PeerSynchronizationController
    {
        private static readonly TimeSpan PeerControlTimeout =
            TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PeerExchangeTimeout =
            TimeSpan.FromSeconds(30);

        private enum OutboundPeerAuthenticationPurpose
        {
            Handshake = 1,
            Session = 2,
            Revoke = 3
        }

        private sealed class OutboundVerifiedResponse : IDisposable
        {
            private readonly byte[] _body;
            private readonly byte[] _sessionId;
            private bool _disposed;

            internal OutboundVerifiedResponse(
                int statusCode,
                byte[] body,
                byte[] sessionId,
                DateTimeOffset responseTimestamp)
            {
                StatusCode = statusCode;
                _body = (byte[])body.Clone();
                _sessionId = sessionId == null
                    ? null
                    : (byte[])sessionId.Clone();
                ResponseTimestamp = responseTimestamp.ToUniversalTime();
            }

            internal int StatusCode { get; }

            internal bool HasSession => _sessionId != null;

            internal DateTimeOffset ResponseTimestamp { get; }

            internal byte[] CopyBody()
            {
                ThrowIfDisposed();
                return (byte[])_body.Clone();
            }

            internal byte[] CopySessionId()
            {
                ThrowIfDisposed();
                return _sessionId == null
                    ? null
                    : (byte[])_sessionId.Clone();
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                Clear(_body);
                Clear(_sessionId);
                _disposed = true;
            }

            private void ThrowIfDisposed()
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(
                        nameof(OutboundVerifiedResponse));
                }
            }
        }

        private sealed class OutboundRequestResult : IDisposable
        {
            private OutboundRequestResult(
                OutboundVerifiedResponse response,
                PeerSyncResponseCode failureCode,
                long? clockSkewSeconds)
            {
                Response = response;
                FailureCode = failureCode;
                ClockSkewSeconds = clockSkewSeconds;
            }

            internal bool IsVerified => Response != null;

            internal OutboundVerifiedResponse Response { get; }

            internal PeerSyncResponseCode FailureCode { get; }

            internal long? ClockSkewSeconds { get; }

            internal static OutboundRequestResult Verified(
                OutboundVerifiedResponse response,
                long? clockSkewSeconds)
            {
                return new OutboundRequestResult(
                    response ?? throw new ArgumentNullException(
                        nameof(response)),
                    PeerSyncResponseCode.Ok,
                    clockSkewSeconds);
            }

            internal static OutboundRequestResult Failed(
                PeerSyncResponseCode failureCode)
            {
                return Failed(failureCode, null);
            }

            internal static OutboundRequestResult Failed(
                PeerSyncResponseCode failureCode,
                long? clockSkewSeconds)
            {
                if (failureCode == PeerSyncResponseCode.Ok)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(failureCode));
                }

                return new OutboundRequestResult(
                    null,
                    failureCode,
                    clockSkewSeconds);
            }

            public void Dispose()
            {
                if (Response != null)
                {
                    Response.Dispose();
                }
            }
        }

        private OutboundRequestResult SendAuthenticatedRequest(
            PairedPeerCredential credential,
            PeerPairAuthenticationContext pairAuthentication,
            ActivePeerSession session,
            OutboundPeerAuthenticationPurpose purpose,
            string path,
            byte[] body)
        {
            if (credential == null)
            {
                throw new ArgumentNullException(nameof(credential));
            }

            if (body == null || body.Length == 0)
            {
                throw new ArgumentException(
                    "The authenticated Peer request body is required.",
                    nameof(body));
            }

            bool isSession = purpose
                == OutboundPeerAuthenticationPurpose.Session;
            if (isSession != (session != null)
                || (!isSession && pairAuthentication == null))
            {
                throw new ArgumentException(
                    "The outbound authentication context does not match the request purpose.");
            }

            byte[] sessionId = null;
            byte[] requestNonce = null;
            byte[] authenticationKey = null;
            byte[] signature = null;
            try
            {
                sessionId = session == null
                    ? null
                    : session.CopySessionId();
                requestNonce = CreateRandomBytes(
                    PeerAuthenticationContract.NonceLength);
                DateTimeOffset sentAt = _utcNowProvider()
                    .ToUniversalTime();
                PeerCanonicalRequestTarget requestTarget =
                    PeerCanonicalRequestTarget.Create(path, null);
                var authenticationData =
                    new PeerRequestAuthenticationData(
                        credential.LocalInstanceId,
                        credential.PeerInstanceId,
                        credential.KeyEpoch,
                        sessionId,
                        "POST",
                        requestTarget,
                        PeerSyncContract.XmlContentType,
                        body,
                        sentAt,
                        requestNonce);

                authenticationKey = CopyOutboundRequestKey(
                    pairAuthentication,
                    session,
                    purpose);
                signature = PeerMessageAuthenticator
                    .CreateRequestSignature(
                        authenticationKey,
                        authenticationData);

                var headers = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    {
                        PeerAuthenticationContract.InstanceIdHeaderName,
                        credential.LocalInstanceId.ToString("D")
                            .ToLowerInvariant()
                    },
                    {
                        PeerAuthenticationContract.KeyEpochHeaderName,
                        credential.KeyEpoch.ToString(
                            CultureInfo.InvariantCulture)
                    },
                    {
                        PeerAuthenticationContract.TimestampHeaderName,
                        PeerAuthenticationContract.FormatTimestamp(sentAt)
                    },
                    {
                        PeerAuthenticationContract.NonceHeaderName,
                        Convert.ToBase64String(requestNonce)
                    },
                    {
                        PeerAuthenticationContract.SignatureHeaderName,
                        Convert.ToBase64String(signature)
                    }
                };
                if (sessionId != null)
                {
                    headers.Add(
                        PeerAuthenticationContract.SessionIdHeaderName,
                        Convert.ToBase64String(sessionId));
                }

                PeerHttpTransportResult transportResult = _transport.Send(
                    new PeerOutboundHttpRequest(
                        credential.PeerEndpoint,
                        path,
                        body,
                        headers,
                        isSession
                            && StringComparer.Ordinal.Equals(
                                path,
                                PeerAuthenticationContract.ExchangePath)
                            ? PeerExchangeTimeout
                            : PeerControlTimeout));
                if (!transportResult.IsSuccess)
                {
                    return OutboundRequestResult.Failed(
                        PeerSyncResponseCode.Internal);
                }

                DateTimeOffset receivedAt = _utcNowProvider()
                    .ToUniversalTime();
                return AuthenticateOutboundResponse(
                    credential,
                    pairAuthentication,
                    session,
                    purpose,
                    path,
                    requestTarget,
                    requestNonce,
                    receivedAt,
                    transportResult.Response);
            }
            finally
            {
                Clear(sessionId);
                Clear(requestNonce);
                Clear(authenticationKey);
                Clear(signature);
            }
        }

        private OutboundRequestResult AuthenticateOutboundResponse(
            PairedPeerCredential credential,
            PeerPairAuthenticationContext pairAuthentication,
            ActivePeerSession session,
            OutboundPeerAuthenticationPurpose purpose,
            string path,
            PeerCanonicalRequestTarget requestTarget,
            byte[] expectedRequestNonce,
            DateTimeOffset receivedAt,
            PeerInboundHttpResponse response)
        {
            byte[] body = null;
            byte[] responseSessionId = null;
            byte[] responseNonce = null;
            byte[] responseSignature = null;
            try
            {
                if (!string.IsNullOrEmpty(response.ContentEncoding)
                    || !StringComparer.OrdinalIgnoreCase.Equals(
                        response.ContentType,
                        PeerSyncContract.XmlContentType))
                {
                    AuditOutboundAuthenticationFailure(
                        credential,
                        path,
                        SecurityAuditReason
                            .AuthenticationDataMissingOrMalformed);
                    return OutboundRequestResult.Failed(
                        PeerSyncResponseCode.NotPeer);
                }

                Guid senderInstanceId;
                ulong keyEpoch;
                DateTimeOffset responseTimestamp;
                if (!TryParseResponseAuthenticationHeaders(
                        response,
                        purpose,
                        out senderInstanceId,
                        out keyEpoch,
                        out responseSessionId,
                        out responseTimestamp,
                        out responseNonce,
                        out responseSignature))
                {
                    AuditOutboundAuthenticationFailure(
                        credential,
                        path,
                        SecurityAuditReason
                            .AuthenticationDataMissingOrMalformed);
                    return OutboundRequestResult.Failed(
                        PeerSyncResponseCode.NotPeer);
                }

                body = response.CopyBody();
                var responseData = new PeerResponseAuthenticationData(
                    senderInstanceId,
                    credential.LocalInstanceId,
                    keyEpoch,
                    responseSessionId,
                    "POST",
                    requestTarget,
                    response.StatusCode,
                    PeerSyncContract.XmlContentType,
                    body,
                    responseTimestamp,
                    responseNonce,
                    expectedRequestNonce);
                PeerResponseAuthenticationResult authenticationResult =
                    AuthenticateOutboundResponseCore(
                        pairAuthentication,
                        session,
                        purpose,
                        responseData,
                        responseSignature,
                        receivedAt,
                        expectedRequestNonce);
                if (authenticationResult
                    != PeerResponseAuthenticationResult.Authenticated)
                {
                    SecurityAuditReason reason =
                        MapOutboundAuthenticationFailure(
                            authenticationResult);
                    AuditOutboundAuthenticationFailure(
                        credential,
                        path,
                        reason);
                    bool clockSkew = authenticationResult
                        == PeerResponseAuthenticationResult.ClockSkew;
                    return OutboundRequestResult.Failed(
                        clockSkew
                            ? PeerSyncResponseCode.ClockSkew
                            : PeerSyncResponseCode.NotPeer,
                        clockSkew
                            && purpose
                                == OutboundPeerAuthenticationPurpose
                                    .Handshake
                            ? CalculateClockSkewSeconds(
                                responseTimestamp,
                                receivedAt)
                            : (long?)null);
                }

                long? clockSkewSeconds = purpose
                        == OutboundPeerAuthenticationPurpose.Handshake
                    ? CalculateClockSkewSeconds(
                        responseTimestamp,
                        receivedAt)
                    : (long?)null;
                return OutboundRequestResult.Verified(
                    new OutboundVerifiedResponse(
                        response.StatusCode,
                        body,
                        responseSessionId,
                        responseTimestamp),
                    clockSkewSeconds);
            }
            finally
            {
                Clear(body);
                Clear(responseSessionId);
                Clear(responseNonce);
                Clear(responseSignature);
            }
        }

        private static byte[] CopyOutboundRequestKey(
            PeerPairAuthenticationContext pairAuthentication,
            ActivePeerSession session,
            OutboundPeerAuthenticationPurpose purpose)
        {
            switch (purpose)
            {
                case OutboundPeerAuthenticationPurpose.Handshake:
                    return pairAuthentication
                        .CopyIncomingHandshakeRequestAuthenticationKey();
                case OutboundPeerAuthenticationPurpose.Session:
                    return session.CopyIncomingRequestAuthenticationKey();
                case OutboundPeerAuthenticationPurpose.Revoke:
                    return pairAuthentication
                        .CopyIncomingRevokeRequestAuthenticationKey();
                default:
                    throw new ArgumentOutOfRangeException(nameof(purpose));
            }
        }

        private static PeerResponseAuthenticationResult
            AuthenticateOutboundResponseCore(
            PeerPairAuthenticationContext pairAuthentication,
            ActivePeerSession session,
            OutboundPeerAuthenticationPurpose purpose,
            PeerResponseAuthenticationData response,
            byte[] signature,
            DateTimeOffset receivedAt,
            byte[] expectedRequestNonce)
        {
            switch (purpose)
            {
                case OutboundPeerAuthenticationPurpose.Handshake:
                    return PeerMessageAuthenticator
                        .AuthenticateHandshakeResponse(
                            pairAuthentication,
                            response,
                            signature,
                            receivedAt,
                            expectedRequestNonce);
                case OutboundPeerAuthenticationPurpose.Session:
                    return PeerMessageAuthenticator
                        .AuthenticateSessionResponse(
                            session,
                            response,
                            signature,
                            receivedAt,
                            expectedRequestNonce);
                case OutboundPeerAuthenticationPurpose.Revoke:
                    return PeerMessageAuthenticator
                        .AuthenticateRevokeResponse(
                            pairAuthentication,
                            response,
                            signature,
                            receivedAt,
                            expectedRequestNonce);
                default:
                    throw new ArgumentOutOfRangeException(nameof(purpose));
            }
        }

        private static bool TryParseResponseAuthenticationHeaders(
            PeerInboundHttpResponse response,
            OutboundPeerAuthenticationPurpose purpose,
            out Guid senderInstanceId,
            out ulong keyEpoch,
            out byte[] sessionId,
            out DateTimeOffset timestamp,
            out byte[] nonce,
            out byte[] signature)
        {
            senderInstanceId = Guid.Empty;
            keyEpoch = 0;
            sessionId = null;
            timestamp = default(DateTimeOffset);
            nonce = null;
            signature = null;

            string instanceIdValue;
            string keyEpochValue;
            string timestampValue;
            string nonceValue;
            string signatureValue;
            if (!TryGetExactlyOne(
                    response.GetHeaderValues(
                        PeerAuthenticationContract.InstanceIdHeaderName),
                    out instanceIdValue)
                || !TryGetExactlyOne(
                    response.GetHeaderValues(
                        PeerAuthenticationContract.KeyEpochHeaderName),
                    out keyEpochValue)
                || !TryGetExactlyOne(
                    response.GetHeaderValues(
                        PeerAuthenticationContract.TimestampHeaderName),
                    out timestampValue)
                || !TryGetExactlyOne(
                    response.GetHeaderValues(
                        PeerAuthenticationContract.NonceHeaderName),
                    out nonceValue)
                || !TryGetExactlyOne(
                    response.GetHeaderValues(
                        PeerAuthenticationContract.SignatureHeaderName),
                    out signatureValue))
            {
                return false;
            }

            IReadOnlyList<string> sessionValues = response.GetHeaderValues(
                PeerAuthenticationContract.SessionIdHeaderName);
            if (purpose == OutboundPeerAuthenticationPurpose.Session)
            {
                string sessionValue;
                if (!TryGetExactlyOne(sessionValues, out sessionValue)
                    || !PeerAuthenticationContract
                        .TryParseCanonicalSessionId(
                            sessionValue,
                            out sessionId))
                {
                    return false;
                }
            }
            else if (purpose
                == OutboundPeerAuthenticationPurpose.Revoke)
            {
                if (sessionValues.Count != 0)
                {
                    return false;
                }
            }
            else if (sessionValues.Count != 0)
            {
                string sessionValue;
                if (!TryGetExactlyOne(sessionValues, out sessionValue)
                    || !PeerAuthenticationContract
                        .TryParseCanonicalSessionId(
                            sessionValue,
                            out sessionId))
                {
                    return false;
                }
            }

            return PeerAuthenticationContract.TryParseCanonicalInstanceId(
                    instanceIdValue,
                    out senderInstanceId)
                && PeerAuthenticationContract.TryParseCanonicalKeyEpoch(
                    keyEpochValue,
                    out keyEpoch)
                && PeerAuthenticationContract.TryParseCanonicalTimestamp(
                    timestampValue,
                    out timestamp)
                && PeerAuthenticationContract.TryParseCanonicalNonce(
                    nonceValue,
                    out nonce)
                && PeerAuthenticationContract.TryParseCanonicalSignature(
                    signatureValue,
                    out signature);
        }

        private static bool TryGetExactlyOne(
            IReadOnlyList<string> values,
            out string value)
        {
            value = null;
            if (values == null || values.Count != 1 || values[0] == null)
            {
                return false;
            }

            value = values[0];
            return true;
        }

        private void AuditOutboundAuthenticationFailure(
            PairedPeerCredential credential,
            string path,
            SecurityAuditReason reason)
        {
            IPAddress remoteAddress;
            if (!IPAddress.TryParse(
                    new Uri(credential.PeerEndpoint).Host,
                    out remoteAddress))
            {
                remoteAddress = null;
            }

            _securityAuditLogger.WriteFailure(
                SecurityAuditEventId.PeerAuthenticationRejected,
                SecurityAuditBoundary.Peer,
                GetSecurityAuditOperation(path),
                reason,
                Guid.NewGuid(),
                null,
                remoteAddress);
        }

        private static SecurityAuditOperation GetSecurityAuditOperation(
            string path)
        {
            if (StringComparer.Ordinal.Equals(
                    path,
                    PeerAuthenticationContract.HandshakePath))
            {
                return SecurityAuditOperation.PeerHandshake;
            }

            if (StringComparer.Ordinal.Equals(
                    path,
                    PeerAuthenticationContract.ExchangePath))
            {
                return SecurityAuditOperation.PeerExchange;
            }

            if (StringComparer.Ordinal.Equals(
                    path,
                    PeerAuthenticationContract.ReleasePath))
            {
                return SecurityAuditOperation.PeerRelease;
            }

            if (StringComparer.Ordinal.Equals(
                    path,
                    PeerAuthenticationContract.RevokePath))
            {
                return SecurityAuditOperation.PeerRevoke;
            }

            throw new ArgumentOutOfRangeException(nameof(path));
        }

        private static SecurityAuditReason MapOutboundAuthenticationFailure(
            PeerResponseAuthenticationResult result)
        {
            switch (result)
            {
                case PeerResponseAuthenticationResult.InvalidSignature:
                case PeerResponseAuthenticationResult
                    .UnexpectedRequestNonce:
                    return SecurityAuditReason.SignatureInvalid;
                case PeerResponseAuthenticationResult.ClockSkew:
                    return SecurityAuditReason
                        .TimestampOutsideAllowedWindow;
                case PeerResponseAuthenticationResult.InvalidSession:
                    return SecurityAuditReason.SessionInvalid;
                default:
                    return SecurityAuditReason
                        .AuthenticationDataMissingOrMalformed;
            }
        }

        private static long CalculateClockSkewSeconds(
            DateTimeOffset peerTimestamp,
            DateTimeOffset localTimestamp)
        {
            return checked((long)Math.Round(
                (peerTimestamp.ToUniversalTime()
                    - localTimestamp.ToUniversalTime()).TotalSeconds,
                MidpointRounding.AwayFromZero));
        }

        private static byte[] CreateRandomBytes(int length)
        {
            var value = new byte[length];
            using (RandomNumberGenerator generator =
                RandomNumberGenerator.Create())
            {
                generator.GetBytes(value);
            }

            return value;
        }
    }
}
