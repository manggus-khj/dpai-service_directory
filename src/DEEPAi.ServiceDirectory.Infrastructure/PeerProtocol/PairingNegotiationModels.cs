using System;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal enum PairingNegotiationState
    {
        Unpaired = 0,
        PairingWindowOpen = 1,
        Negotiating = 2,
        SasPending = 3,
        BothConfirmed = 4
    }

    internal enum PairingHelloDisposition
    {
        AcceptedAsResponder = 1,
        ReplayedResponderHello = 2,
        RetainedInitiator = 3
    }

    internal sealed class PairingLocalDecisionMessage : IDisposable
    {
        private readonly byte[] _requestBody;
        private readonly byte[] _requestMac;
        private bool _disposed;

        internal PairingLocalDecisionMessage(
            PeerPairingDecision request,
            byte[] requestBody,
            byte[] requestMac,
            bool isReplay,
            bool pairingCancelled)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            ValidateBody(requestBody, nameof(requestBody));
            ValidateMac(requestMac, nameof(requestMac));

            Request = request;
            IsReplay = isReplay;
            PairingCancelled = pairingCancelled;
            _requestBody = (byte[])requestBody.Clone();
            _requestMac = (byte[])requestMac.Clone();
        }

        internal PeerPairingDecision Request { get; }

        internal bool IsReplay { get; }

        internal bool PairingCancelled { get; }

        internal byte[] CopyRequestBody()
        {
            ThrowIfDisposed();
            return (byte[])_requestBody.Clone();
        }

        internal byte[] CopyRequestMac()
        {
            ThrowIfDisposed();
            return (byte[])_requestMac.Clone();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Array.Clear(_requestBody, 0, _requestBody.Length);
            Array.Clear(_requestMac, 0, _requestMac.Length);
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PairingLocalDecisionMessage));
            }
        }

        private static void ValidateBody(byte[] value, string parameterName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length == 0)
            {
                throw new ArgumentException(
                    "The pairing decision body must not be empty.",
                    parameterName);
            }
        }

        private static void ValidateMac(byte[] value, string parameterName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length != PairingCryptography.AuthenticationCodeLength)
            {
                throw new ArgumentException(
                    "The pairing decision MAC must contain exactly 32 bytes.",
                    parameterName);
            }
        }
    }

    internal sealed class PairingRemoteDecisionResult : IDisposable
    {
        private readonly byte[] _responseBody;
        private readonly byte[] _responseMac;
        private bool _disposed;

        internal PairingRemoteDecisionResult(
            PeerPairingDecision decision,
            byte[] responseBody,
            byte[] responseMac,
            bool isReplay,
            bool pairingCancelled)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            if (responseBody == null)
            {
                throw new ArgumentNullException(nameof(responseBody));
            }

            if (responseBody.Length == 0)
            {
                throw new ArgumentException(
                    "The pairing decision response body must not be empty.",
                    nameof(responseBody));
            }

            if (responseMac == null)
            {
                throw new ArgumentNullException(nameof(responseMac));
            }

            if (responseMac.Length
                != PairingCryptography.AuthenticationCodeLength)
            {
                throw new ArgumentException(
                    "The pairing decision response MAC must contain exactly 32 bytes.",
                    nameof(responseMac));
            }

            Decision = decision;
            IsReplay = isReplay;
            PairingCancelled = pairingCancelled;
            _responseBody = (byte[])responseBody.Clone();
            _responseMac = (byte[])responseMac.Clone();
        }

        internal PeerPairingDecision Decision { get; }

        internal bool IsReplay { get; }

        internal bool PairingCancelled { get; }

        internal byte[] CopyResponseBody()
        {
            ThrowIfDisposed();
            return (byte[])_responseBody.Clone();
        }

        internal byte[] CopyResponseMac()
        {
            ThrowIfDisposed();
            return (byte[])_responseMac.Clone();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Array.Clear(_responseBody, 0, _responseBody.Length);
            Array.Clear(_responseMac, 0, _responseMac.Length);
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PairingRemoteDecisionResult));
            }
        }
    }

    // Owns the pair root returned at the transient/durable boundary. The
    // persistence coordinator must dispose it after DPAPI protection succeeds
    // or fails.
    internal sealed class PairingBothConfirmedMaterial : IDisposable
    {
        private readonly byte[] _transcriptHash;
        private readonly byte[] _pairRoot;
        private bool _disposed;

        internal PairingBothConfirmedMaterial(
            Guid pairingId,
            Guid localInstanceId,
            Guid peerInstanceId,
            string localEndpoint,
            string peerEndpoint,
            ulong keyEpoch,
            byte[] transcriptHash,
            byte[] pairRoot)
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

            if (peerInstanceId == Guid.Empty
                || peerInstanceId == localInstanceId)
            {
                throw new ArgumentException(
                    "The peer instance ID must be non-empty and different from local.",
                    nameof(peerInstanceId));
            }

            if (string.IsNullOrEmpty(localEndpoint))
            {
                throw new ArgumentException(
                    "The local endpoint must not be empty.",
                    nameof(localEndpoint));
            }

            if (string.IsNullOrEmpty(peerEndpoint))
            {
                throw new ArgumentException(
                    "The peer endpoint must not be empty.",
                    nameof(peerEndpoint));
            }

            if (keyEpoch == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(keyEpoch));
            }

            ValidateExactLength(
                transcriptHash,
                nameof(transcriptHash),
                PairingCryptography.TranscriptHashLength);
            ValidateExactLength(
                pairRoot,
                nameof(pairRoot),
                PairingCryptography.KeyMaterialLength);

            PairingId = pairingId;
            LocalInstanceId = localInstanceId;
            PeerInstanceId = peerInstanceId;
            LocalEndpoint = localEndpoint;
            PeerEndpoint = peerEndpoint;
            KeyEpoch = keyEpoch;
            _transcriptHash = (byte[])transcriptHash.Clone();
            _pairRoot = (byte[])pairRoot.Clone();
        }

        internal Guid PairingId { get; }

        internal Guid LocalInstanceId { get; }

        internal Guid PeerInstanceId { get; }

        internal string LocalEndpoint { get; }

        internal string PeerEndpoint { get; }

        internal ulong KeyEpoch { get; }

        internal byte[] CopyTranscriptHash()
        {
            ThrowIfDisposed();
            return (byte[])_transcriptHash.Clone();
        }

        internal byte[] CopyPairRoot()
        {
            ThrowIfDisposed();
            return (byte[])_pairRoot.Clone();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Array.Clear(_transcriptHash, 0, _transcriptHash.Length);
            Array.Clear(_pairRoot, 0, _pairRoot.Length);
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PairingBothConfirmedMaterial));
            }
        }

        private static void ValidateExactLength(
            byte[] value,
            string parameterName,
            int expectedLength)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length != expectedLength)
            {
                throw new ArgumentException(
                    "The pairing handoff buffer has an invalid length.",
                    parameterName);
            }
        }
    }
}
