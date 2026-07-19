using System;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal enum DurablePeerCredentialState
    {
        PairedPendingCommit = 1,
        PairedDisabled = 2,
        Enabled = 3
    }

    internal enum PairingRole
    {
        Initiator = 1,
        Responder = 2
    }

    internal sealed class PairingCommitEvidence : IDisposable
    {
        internal const int MaximumResponseBodyBytes = 16 * 1024;

        private readonly object _lifecycleGate = new object();
        private readonly byte[] _requestMac;
        private readonly byte[] _responseBody;
        private readonly byte[] _responseMac;
        private bool _disposed;

        internal PairingCommitEvidence(
            byte[] requestMac,
            int responseStatusCode,
            byte[] responseBody,
            byte[] responseMac)
        {
            PeerAuthenticationContract.ValidateExactLength(
                requestMac,
                nameof(requestMac),
                PeerAuthenticationContract.AuthenticationKeyLength);
            if (responseStatusCode != 200)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(responseStatusCode),
                    responseStatusCode,
                    "Only a successful pairing commit response can be persisted.");
            }

            if (responseBody == null)
            {
                throw new ArgumentNullException(nameof(responseBody));
            }

            if (responseBody.Length == 0
                || responseBody.Length > MaximumResponseBodyBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(responseBody),
                    responseBody.Length,
                    "The pairing commit response body is outside its bounded wire size.");
            }

            PeerAuthenticationContract.ValidateExactLength(
                responseMac,
                nameof(responseMac),
                PeerAuthenticationContract.AuthenticationKeyLength);

            _requestMac = (byte[])requestMac.Clone();
            ResponseStatusCode = responseStatusCode;
            _responseBody = (byte[])responseBody.Clone();
            _responseMac = (byte[])responseMac.Clone();
        }

        internal int ResponseStatusCode { get; }

        internal byte[] CopyRequestMac()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return (byte[])_requestMac.Clone();
            }
        }

        internal byte[] CopyResponseBody()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return (byte[])_responseBody.Clone();
            }
        }

        internal byte[] CopyResponseMac()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return (byte[])_responseMac.Clone();
            }
        }

        internal PairingCommitEvidence Clone()
        {
            byte[] requestMac = null;
            byte[] responseBody = null;
            byte[] responseMac = null;
            try
            {
                requestMac = CopyRequestMac();
                responseBody = CopyResponseBody();
                responseMac = CopyResponseMac();
                return new PairingCommitEvidence(
                    requestMac,
                    ResponseStatusCode,
                    responseBody,
                    responseMac);
            }
            finally
            {
                Clear(requestMac);
                Clear(responseBody);
                Clear(responseMac);
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

                Clear(_requestMac);
                Clear(_responseBody);
                Clear(_responseMac);
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PairingCommitEvidence));
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

    internal sealed class PairedPeerCredential : IDisposable
    {
        private const int HashLength = 32;

        private readonly object _lifecycleGate = new object();
        private readonly byte[] _transcriptHash;
        private readonly byte[] _pairRoot;
        private bool _disposed;

        internal PairedPeerCredential(
            DurablePeerCredentialState state,
            PairingRole localRole,
            Guid pairingId,
            Guid localInstanceId,
            Guid peerInstanceId,
            string localEndpoint,
            string peerEndpoint,
            ulong keyEpoch,
            byte[] transcriptHash,
            byte[] pairRoot,
            DateTime commitExpiresUtc,
            bool localCommitConfirmed,
            bool remoteCommitConfirmed,
            PairingCommitEvidence localCommitEvidence,
            PairingCommitEvidence remoteCommitEvidence)
        {
            if (!Enum.IsDefined(typeof(DurablePeerCredentialState), state))
            {
                throw new ArgumentOutOfRangeException(nameof(state));
            }

            if (!Enum.IsDefined(typeof(PairingRole), localRole))
            {
                throw new ArgumentOutOfRangeException(nameof(localRole));
            }

            ValidateIdentifiers(pairingId, localInstanceId, peerInstanceId);
            LocalEndpoint = RequireCanonicalEndpoint(
                localEndpoint,
                nameof(localEndpoint));
            PeerEndpoint = RequireCanonicalEndpoint(
                peerEndpoint,
                nameof(peerEndpoint));
            if (StringComparer.Ordinal.Equals(LocalEndpoint, PeerEndpoint))
            {
                throw new ArgumentException(
                    "The local and peer endpoints must be different.",
                    nameof(peerEndpoint));
            }

            if (keyEpoch == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keyEpoch),
                    keyEpoch,
                    "The peer key epoch must be positive.");
            }

            PeerAuthenticationContract.ValidateExactLength(
                transcriptHash,
                nameof(transcriptHash),
                HashLength);
            PeerAuthenticationContract.ValidateExactLength(
                pairRoot,
                nameof(pairRoot),
                HashLength);
            if (commitExpiresUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "The pairing commit expiry must be UTC.",
                    nameof(commitExpiresUtc));
            }

            ValidateCommitEvidence(
                localCommitConfirmed,
                localCommitEvidence,
                nameof(localCommitEvidence));
            ValidateCommitEvidence(
                remoteCommitConfirmed,
                remoteCommitEvidence,
                nameof(remoteCommitEvidence));
            if (state != DurablePeerCredentialState.PairedPendingCommit
                && (!localCommitConfirmed || !remoteCommitConfirmed))
            {
                throw new ArgumentException(
                    "A completed paired credential requires both commit confirmations.",
                    nameof(state));
            }

            PairingCommitEvidence localEvidenceClone = null;
            PairingCommitEvidence remoteEvidenceClone = null;
            byte[] transcriptHashClone = null;
            byte[] pairRootClone = null;
            try
            {
                localEvidenceClone = localCommitEvidence == null
                    ? null
                    : localCommitEvidence.Clone();
                remoteEvidenceClone = remoteCommitEvidence == null
                    ? null
                    : remoteCommitEvidence.Clone();
                transcriptHashClone = (byte[])transcriptHash.Clone();
                pairRootClone = (byte[])pairRoot.Clone();

                State = state;
                LocalRole = localRole;
                PairingId = pairingId;
                LocalInstanceId = localInstanceId;
                PeerInstanceId = peerInstanceId;
                KeyEpoch = keyEpoch;
                _transcriptHash = transcriptHashClone;
                _pairRoot = pairRootClone;
                CommitExpiresUtc = commitExpiresUtc;
                LocalCommitConfirmed = localCommitConfirmed;
                RemoteCommitConfirmed = remoteCommitConfirmed;
                LocalCommitEvidence = localEvidenceClone;
                RemoteCommitEvidence = remoteEvidenceClone;
                transcriptHashClone = null;
                pairRootClone = null;
            }
            catch
            {
                Clear(transcriptHashClone);
                Clear(pairRootClone);
                if (localEvidenceClone != null)
                {
                    localEvidenceClone.Dispose();
                }

                if (remoteEvidenceClone != null)
                {
                    remoteEvidenceClone.Dispose();
                }

                throw;
            }
        }

        internal DurablePeerCredentialState State { get; }

        internal PairingRole LocalRole { get; }

        internal Guid PairingId { get; }

        internal Guid LocalInstanceId { get; }

        internal Guid PeerInstanceId { get; }

        internal string LocalEndpoint { get; }

        internal string PeerEndpoint { get; }

        internal ulong KeyEpoch { get; }

        internal DateTime CommitExpiresUtc { get; }

        internal bool LocalCommitConfirmed { get; }

        internal bool RemoteCommitConfirmed { get; }

        internal PairingCommitEvidence LocalCommitEvidence { get; }

        internal PairingCommitEvidence RemoteCommitEvidence { get; }

        internal byte[] CopyTranscriptHash()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return (byte[])_transcriptHash.Clone();
            }
        }

        internal byte[] CopyPairRoot()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return (byte[])_pairRoot.Clone();
            }
        }

        internal PairedPeerCredential Clone()
        {
            byte[] transcriptHash = null;
            byte[] pairRoot = null;
            try
            {
                transcriptHash = CopyTranscriptHash();
                pairRoot = CopyPairRoot();
                return new PairedPeerCredential(
                    State,
                    LocalRole,
                    PairingId,
                    LocalInstanceId,
                    PeerInstanceId,
                    LocalEndpoint,
                    PeerEndpoint,
                    KeyEpoch,
                    transcriptHash,
                    pairRoot,
                    CommitExpiresUtc,
                    LocalCommitConfirmed,
                    RemoteCommitConfirmed,
                    LocalCommitEvidence,
                    RemoteCommitEvidence);
            }
            finally
            {
                Clear(transcriptHash);
                Clear(pairRoot);
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

                Array.Clear(_transcriptHash, 0, _transcriptHash.Length);
                Array.Clear(_pairRoot, 0, _pairRoot.Length);
                if (LocalCommitEvidence != null)
                {
                    LocalCommitEvidence.Dispose();
                }

                if (RemoteCommitEvidence != null)
                {
                    RemoteCommitEvidence.Dispose();
                }

                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PairedPeerCredential));
            }
        }

        private static void ValidateIdentifiers(
            Guid pairingId,
            Guid localInstanceId,
            Guid peerInstanceId)
        {
            if (pairingId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The pairing ID must not be empty.",
                    nameof(pairingId));
            }

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
        }

        private static string RequireCanonicalEndpoint(
            string endpoint,
            string parameterName)
        {
            string canonical;
            if (!AdminPeerEndpoint.TryNormalize(endpoint, out canonical)
                || !StringComparer.Ordinal.Equals(endpoint, canonical))
            {
                throw new ArgumentException(
                    "The endpoint must use exact canonical HTTP IP literal form on port 21000.",
                    parameterName);
            }

            return canonical;
        }

        private static void ValidateCommitEvidence(
            bool confirmed,
            PairingCommitEvidence evidence,
            string parameterName)
        {
            if (confirmed != (evidence != null))
            {
                throw new ArgumentException(
                    "Commit confirmation and persisted signed response evidence are inconsistent.",
                    parameterName);
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
