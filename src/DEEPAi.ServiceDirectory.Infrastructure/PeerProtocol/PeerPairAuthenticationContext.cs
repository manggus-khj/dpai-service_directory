using System;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    // This value is constructed only from the already trusted durable peer
    // binding and its purpose-separated inbound keys. Never populate it from
    // headers on the message currently being authenticated.
    internal sealed class PeerPairAuthenticationContext : IDisposable
    {
        private readonly object _lifecycleGate = new object();
        private readonly byte[] _incomingHandshakeRequestAuthenticationKey;
        private readonly byte[] _incomingHandshakeResponseAuthenticationKey;
        private readonly byte[] _incomingRevokeRequestAuthenticationKey;
        private readonly byte[] _incomingRevokeResponseAuthenticationKey;
        private bool _disposed;

        public PeerPairAuthenticationContext(
            Guid localInstanceId,
            Guid peerInstanceId,
            ulong keyEpoch,
            byte[] incomingHandshakeRequestAuthenticationKey,
            byte[] incomingHandshakeResponseAuthenticationKey,
            byte[] incomingRevokeRequestAuthenticationKey,
            byte[] incomingRevokeResponseAuthenticationKey)
        {
            ValidateBinding(localInstanceId, peerInstanceId, keyEpoch);
            ValidateAuthenticationKey(
                incomingHandshakeRequestAuthenticationKey,
                nameof(incomingHandshakeRequestAuthenticationKey));
            ValidateAuthenticationKey(
                incomingHandshakeResponseAuthenticationKey,
                nameof(incomingHandshakeResponseAuthenticationKey));
            ValidateAuthenticationKey(
                incomingRevokeRequestAuthenticationKey,
                nameof(incomingRevokeRequestAuthenticationKey));
            ValidateAuthenticationKey(
                incomingRevokeResponseAuthenticationKey,
                nameof(incomingRevokeResponseAuthenticationKey));

            LocalInstanceId = localInstanceId;
            PeerInstanceId = peerInstanceId;
            KeyEpoch = keyEpoch;
            _incomingHandshakeRequestAuthenticationKey =
                (byte[])incomingHandshakeRequestAuthenticationKey.Clone();
            _incomingHandshakeResponseAuthenticationKey =
                (byte[])incomingHandshakeResponseAuthenticationKey.Clone();
            _incomingRevokeRequestAuthenticationKey =
                (byte[])incomingRevokeRequestAuthenticationKey.Clone();
            _incomingRevokeResponseAuthenticationKey =
                (byte[])incomingRevokeResponseAuthenticationKey.Clone();
        }

        internal static PeerPairAuthenticationContext CreateFromPairRoot(
            Guid localInstanceId,
            Guid peerInstanceId,
            ulong keyEpoch,
            byte[] pairRoot)
        {
            byte[] handshakeRequestKey = null;
            byte[] handshakeResponseKey = null;
            byte[] revokeRequestKey = null;
            byte[] revokeResponseKey = null;
            try
            {
                handshakeRequestKey = PeerKeyDerivation.DerivePairBoundKey(
                    pairRoot,
                    keyEpoch,
                    localInstanceId,
                    peerInstanceId,
                    PeerPairBoundKeyPurpose.HandshakeRequest);
                handshakeResponseKey = PeerKeyDerivation.DerivePairBoundKey(
                    pairRoot,
                    keyEpoch,
                    localInstanceId,
                    peerInstanceId,
                    PeerPairBoundKeyPurpose.HandshakeResponse);
                revokeRequestKey = PeerKeyDerivation.DerivePairBoundKey(
                    pairRoot,
                    keyEpoch,
                    localInstanceId,
                    peerInstanceId,
                    PeerPairBoundKeyPurpose.RevokeRequest);
                revokeResponseKey = PeerKeyDerivation.DerivePairBoundKey(
                    pairRoot,
                    keyEpoch,
                    localInstanceId,
                    peerInstanceId,
                    PeerPairBoundKeyPurpose.RevokeResponse);

                return new PeerPairAuthenticationContext(
                    localInstanceId,
                    peerInstanceId,
                    keyEpoch,
                    handshakeRequestKey,
                    handshakeResponseKey,
                    revokeRequestKey,
                    revokeResponseKey);
            }
            finally
            {
                Clear(handshakeRequestKey);
                Clear(handshakeResponseKey);
                Clear(revokeRequestKey);
                Clear(revokeResponseKey);
            }
        }

        public Guid LocalInstanceId { get; }

        public Guid PeerInstanceId { get; }

        public ulong KeyEpoch { get; }

        internal byte[] CopyIncomingHandshakeRequestAuthenticationKey()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return (byte[])_incomingHandshakeRequestAuthenticationKey
                    .Clone();
            }
        }

        internal byte[] CopyIncomingHandshakeResponseAuthenticationKey()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return (byte[])_incomingHandshakeResponseAuthenticationKey
                    .Clone();
            }
        }

        internal byte[] CopyIncomingRevokeRequestAuthenticationKey()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return (byte[])_incomingRevokeRequestAuthenticationKey
                    .Clone();
            }
        }

        internal byte[] CopyIncomingRevokeResponseAuthenticationKey()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return (byte[])_incomingRevokeResponseAuthenticationKey
                    .Clone();
            }
        }

        internal bool MatchesInboundRequest(
            PeerRequestAuthenticationData request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return request.SenderInstanceId == PeerInstanceId
                    && request.ReceiverInstanceId == LocalInstanceId
                    && request.KeyEpoch == KeyEpoch;
            }
        }

        internal bool MatchesInboundResponse(
            PeerResponseAuthenticationData response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return response.SenderInstanceId == PeerInstanceId
                    && response.ReceiverInstanceId == LocalInstanceId
                    && response.KeyEpoch == KeyEpoch;
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

                Clear(_incomingHandshakeRequestAuthenticationKey);
                Clear(_incomingHandshakeResponseAuthenticationKey);
                Clear(_incomingRevokeRequestAuthenticationKey);
                Clear(_incomingRevokeResponseAuthenticationKey);
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PeerPairAuthenticationContext));
            }
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
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

        private static void ValidateAuthenticationKey(
            byte[] authenticationKey,
            string parameterName)
        {
            PeerAuthenticationContract.ValidateExactLength(
                authenticationKey,
                parameterName,
                PeerAuthenticationContract.AuthenticationKeyLength);
        }
    }
}
