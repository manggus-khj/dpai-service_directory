using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal enum PeerResponseKeySource
    {
        Handshake = 1,
        Revoke = 2,
        Session = 3
    }

    internal static class PeerAuthenticatedResponseFactory
    {
        internal static PeerHttpResponseData CreateControl(
            PeerRequestAuthenticationData request,
            PeerPairAuthenticationContext pairContext,
            ActivePeerSession session,
            PeerResponseKeySource keySource,
            int statusCode,
            PeerControlResponse response,
            byte[] responseSessionId,
            DateTimeOffset utcNow,
            int? retryAfterSeconds = null)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            return Create(
                request,
                pairContext,
                session,
                keySource,
                statusCode,
                PeerSyncXmlCodec.SerializeControlResponse(response),
                responseSessionId,
                utcNow,
                retryAfterSeconds);
        }

        internal static PeerHttpResponseData CreateExchange(
            PeerRequestAuthenticationData request,
            ActivePeerSession session,
            int statusCode,
            PeerExchangeResponse response,
            DateTimeOffset utcNow,
            int? retryAfterSeconds = null)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            return Create(
                request,
                null,
                session,
                PeerResponseKeySource.Session,
                statusCode,
                PeerSyncXmlCodec.SerializeExchangeResponse(response),
                session.CopySessionId(),
                utcNow,
                retryAfterSeconds);
        }

        private static PeerHttpResponseData Create(
            PeerRequestAuthenticationData request,
            PeerPairAuthenticationContext pairContext,
            ActivePeerSession session,
            PeerResponseKeySource keySource,
            int statusCode,
            byte[] body,
            byte[] responseSessionId,
            DateTimeOffset utcNow,
            int? retryAfterSeconds)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (body == null || body.Length == 0)
            {
                throw new ArgumentException(
                    "An authenticated Peer response body is required.",
                    nameof(body));
            }

            byte[] responseNonce = null;
            byte[] requestNonce = null;
            byte[] authenticationKey = null;
            byte[] signature = null;
            byte[] sessionId = null;
            try
            {
                responseNonce = CreateRandomBytes(
                    PeerAuthenticationContract.NonceLength);
                requestNonce = request.CopyNonce();
                sessionId = responseSessionId == null
                    ? null
                    : (byte[])responseSessionId.Clone();
                authenticationKey = CopyResponseKey(
                    pairContext,
                    session,
                    keySource);

                var responseAuthentication =
                    new PeerResponseAuthenticationData(
                        request.ReceiverInstanceId,
                        request.SenderInstanceId,
                        request.KeyEpoch,
                        sessionId,
                        request.Method,
                        request.RequestTarget,
                        statusCode,
                        PeerSyncContract.XmlContentType,
                        body,
                        utcNow,
                        responseNonce,
                        requestNonce);
                signature = PeerMessageAuthenticator.CreateResponseSignature(
                    authenticationKey,
                    responseAuthentication);

                var headers = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    {
                        PeerAuthenticationContract.InstanceIdHeaderName,
                        request.ReceiverInstanceId
                            .ToString("D")
                            .ToLowerInvariant()
                    },
                    {
                        PeerAuthenticationContract.KeyEpochHeaderName,
                        request.KeyEpoch.ToString(
                            CultureInfo.InvariantCulture)
                    },
                    {
                        PeerAuthenticationContract.TimestampHeaderName,
                        PeerAuthenticationContract.FormatTimestamp(utcNow)
                    },
                    {
                        PeerAuthenticationContract.NonceHeaderName,
                        Convert.ToBase64String(responseNonce)
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

                return PeerHttpResponseData.Xml(
                    statusCode,
                    body,
                    headers,
                    retryAfterSeconds);
            }
            finally
            {
                Clear(responseNonce);
                Clear(requestNonce);
                Clear(authenticationKey);
                Clear(signature);
                Clear(sessionId);
                Clear(responseSessionId);
                Clear(body);
            }
        }

        private static byte[] CopyResponseKey(
            PeerPairAuthenticationContext pairContext,
            ActivePeerSession session,
            PeerResponseKeySource keySource)
        {
            switch (keySource)
            {
                case PeerResponseKeySource.Handshake:
                    if (pairContext == null)
                    {
                        throw new ArgumentNullException(
                            nameof(pairContext));
                    }

                    return pairContext
                        .CopyIncomingHandshakeResponseAuthenticationKey();
                case PeerResponseKeySource.Revoke:
                    if (pairContext == null)
                    {
                        throw new ArgumentNullException(
                            nameof(pairContext));
                    }

                    return pairContext
                        .CopyIncomingRevokeResponseAuthenticationKey();
                case PeerResponseKeySource.Session:
                    if (session == null)
                    {
                        throw new ArgumentNullException(nameof(session));
                    }

                    return session
                        .CopyIncomingResponseAuthenticationKey();
                default:
                    throw new ArgumentOutOfRangeException(nameof(keySource));
            }
        }

        private static byte[] CreateRandomBytes(int length)
        {
            var value = new byte[length];
            using (RandomNumberGenerator random =
                RandomNumberGenerator.Create())
            {
                random.GetBytes(value);
            }

            return value;
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
