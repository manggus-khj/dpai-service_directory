using System;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    // This value represents session state that was authenticated and accepted
    // previously. Never construct it directly from headers on the request being
    // authenticated.
    internal sealed class ActivePeerSession : IDisposable
    {
        private readonly object _lifecycleGate = new object();
        private readonly byte[] _sessionId;
        private readonly byte[] _incomingRequestAuthenticationKey;
        private readonly byte[] _incomingResponseAuthenticationKey;
        private bool _disposed;

        public ActivePeerSession(
            Guid localInstanceId,
            Guid peerInstanceId,
            ulong keyEpoch,
            byte[] sessionId,
            DateTimeOffset expiresAt,
            byte[] incomingRequestAuthenticationKey,
            byte[] incomingResponseAuthenticationKey)
        {
            ValidateBinding(localInstanceId, peerInstanceId, keyEpoch);
            PeerAuthenticationContract.ValidateExactLength(
                sessionId,
                nameof(sessionId),
                PeerAuthenticationContract.SessionIdLength);
            PeerAuthenticationContract.ValidateExactLength(
                incomingRequestAuthenticationKey,
                nameof(incomingRequestAuthenticationKey),
                PeerAuthenticationContract.AuthenticationKeyLength);
            PeerAuthenticationContract.ValidateExactLength(
                incomingResponseAuthenticationKey,
                nameof(incomingResponseAuthenticationKey),
                PeerAuthenticationContract.AuthenticationKeyLength);

            LocalInstanceId = localInstanceId;
            PeerInstanceId = peerInstanceId;
            KeyEpoch = keyEpoch;
            _sessionId = (byte[])sessionId.Clone();
            ExpiresAtUtc = expiresAt.ToUniversalTime();
            _incomingRequestAuthenticationKey =
                (byte[])incomingRequestAuthenticationKey.Clone();
            _incomingResponseAuthenticationKey =
                (byte[])incomingResponseAuthenticationKey.Clone();
        }

        internal static ActivePeerSession CreateFromHandshake(
            Guid localInstanceId,
            Guid peerInstanceId,
            ulong keyEpoch,
            byte[] pairRoot,
            byte[] handshakeRequestNonce,
            byte[] handshakeResponseNonce,
            byte[] sessionId,
            DateTimeOffset responseUtcNow,
            DateTimeOffset expiresAtUtc)
        {
            DateTimeOffset canonicalResponseTime =
                responseUtcNow.ToUniversalTime();
            DateTimeOffset canonicalExpiry = expiresAtUtc.ToUniversalTime();
            DateTimeOffset requiredExpiry;
            try
            {
                requiredExpiry = canonicalResponseTime.AddMinutes(
                    PeerAuthenticationContract.SessionLifetimeMinutes);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(responseUtcNow),
                    responseUtcNow,
                    "The handshake response time cannot represent a complete session lifetime.");
            }

            if (canonicalExpiry != requiredExpiry)
            {
                throw new ArgumentException(
                    "The session expiry must be exactly ten minutes after the handshake response time.",
                    nameof(expiresAtUtc));
            }

            byte[] requestKey = null;
            byte[] responseKey = null;
            try
            {
                requestKey = PeerKeyDerivation.DeriveSessionKey(
                    pairRoot,
                    keyEpoch,
                    localInstanceId,
                    peerInstanceId,
                    handshakeRequestNonce,
                    handshakeResponseNonce,
                    sessionId,
                    PeerSessionKeyPurpose.Request);
                responseKey = PeerKeyDerivation.DeriveSessionKey(
                    pairRoot,
                    keyEpoch,
                    localInstanceId,
                    peerInstanceId,
                    handshakeRequestNonce,
                    handshakeResponseNonce,
                    sessionId,
                    PeerSessionKeyPurpose.Response);

                return new ActivePeerSession(
                    localInstanceId,
                    peerInstanceId,
                    keyEpoch,
                    sessionId,
                    canonicalExpiry,
                    requestKey,
                    responseKey);
            }
            finally
            {
                Clear(requestKey);
                Clear(responseKey);
            }
        }

        public Guid LocalInstanceId { get; }

        public Guid PeerInstanceId { get; }

        public ulong KeyEpoch { get; }

        public DateTimeOffset ExpiresAtUtc { get; }

        public byte[] CopySessionId()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return (byte[])_sessionId.Clone();
            }
        }

        internal byte[] CopyIncomingRequestAuthenticationKey()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return (byte[])_incomingRequestAuthenticationKey.Clone();
            }
        }

        internal byte[] CopyIncomingResponseAuthenticationKey()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return (byte[])_incomingResponseAuthenticationKey.Clone();
            }
        }

        internal bool MatchesInboundRequest(
            PeerRequestAuthenticationData request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            return request.HasSession
                && request.SenderInstanceId == PeerInstanceId
                && request.ReceiverInstanceId == LocalInstanceId
                && request.KeyEpoch == KeyEpoch
                && MatchesSessionId(request.CopySessionId());
        }

        internal bool MatchesInboundResponse(
            PeerResponseAuthenticationData response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            return response.HasSession
                && response.SenderInstanceId == PeerInstanceId
                && response.ReceiverInstanceId == LocalInstanceId
                && response.KeyEpoch == KeyEpoch
                && MatchesSessionId(response.CopySessionId());
        }

        internal bool TryGetRemainingLifetime(
            DateTimeOffset receivedAt,
            out TimeSpan remainingLifetime)
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                remainingLifetime = ExpiresAtUtc
                    - receivedAt.ToUniversalTime();
                return remainingLifetime > TimeSpan.Zero
                    && remainingLifetime
                        <= TimeSpan.FromMinutes(
                            PeerAuthenticationContract
                                .SessionLifetimeMinutes);
            }
        }

        public void Dispose()
        {
            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    return;
                }

                Clear(_sessionId);
                Clear(_incomingRequestAuthenticationKey);
                Clear(_incomingResponseAuthenticationKey);
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }

        private bool MatchesSessionId(byte[] candidate)
        {
            try
            {
                lock (_lifecycleGate)
                {
                    ThrowIfDisposed();
                    return candidate.Length
                            == PeerAuthenticationContract.SessionIdLength
                        && PeerAuthenticationContract.FixedTimeEquals16(
                            _sessionId,
                            candidate);
                }
            }
            finally
            {
                Array.Clear(candidate, 0, candidate.Length);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(ActivePeerSession));
            }
        }

        private static void ValidateBinding(
            Guid localInstanceId,
            Guid peerInstanceId,
            ulong keyEpoch)
        {
            if (localInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The local instance ID must not be empty.",
                    nameof(localInstanceId));
            }

            if (peerInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The peer instance ID must not be empty.",
                    nameof(peerInstanceId));
            }

            if (localInstanceId == peerInstanceId)
            {
                throw new ArgumentException(
                    "The local and peer instance IDs must be different.",
                    nameof(peerInstanceId));
            }

            if (keyEpoch == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keyEpoch),
                    keyEpoch,
                    "The key epoch must be positive.");
            }
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
