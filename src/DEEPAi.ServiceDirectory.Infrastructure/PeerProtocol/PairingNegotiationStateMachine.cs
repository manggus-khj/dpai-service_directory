using System;
using System.Diagnostics;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    // Owns only the restart-volatile portion of initial pairing. The durable
    // PairedPendingCommit transition is deliberately outside this type.
    internal sealed partial class PairingNegotiationStateMachine : IDisposable
    {
        internal const int MaximumHelloAttempts = 3;
        internal static readonly TimeSpan PairingWindowLifetime =
            TimeSpan.FromMinutes(5);

        private readonly object _gate = new object();
        private readonly Guid _localInstanceId;
        private readonly string _localEndpoint;
        private readonly ulong _localLastPeerKeyEpoch;
        private readonly Func<long> _timestampProvider;
        private readonly long _timestampFrequency;

        private PairingNegotiationState _state;
        private string _peerEndpoint;
        private long _deadlineTimestamp;
        private long _lastObservedTimestamp;
        private int _helloAttempts;
        private PeerPairingRole? _localRole;
        private Guid _pairingId;
        private Guid _initiatorInstanceId;
        private Guid _peerInstanceId;
        private string _initiatorEndpoint;
        private ulong _initiatorLastPeerKeyEpoch;
        private ulong _keyEpoch;
        private byte[] _initiatorNonce;
        private byte[] _initiatorPublicKey;
        private byte[] _transcriptHash;
        private byte[] _localConfirmationMac;
        private byte[] _remoteConfirmationMac;
        private char[] _sas;
        private PairingSecretContext _secretContext;

        // Decision state is declared here so the single reset path can clear
        // every secret and replay record owned by the partial implementation.
        private PeerPairingDecision _localDecisionRequest;
        private byte[] _localDecisionBody;
        private byte[] _localDecisionMac;
        private PeerPairingDecision _remoteDecisionRequest;
        private byte[] _remoteDecisionBody;
        private byte[] _remoteDecisionMac;
        private byte[] _remoteDecisionResponseBody;
        private byte[] _remoteDecisionResponseMac;
        private bool _disposed;

        internal PairingNegotiationStateMachine(
            Guid localInstanceId,
            string localEndpoint,
            ulong localLastPeerKeyEpoch)
            : this(
                localInstanceId,
                localEndpoint,
                localLastPeerKeyEpoch,
                Stopwatch.GetTimestamp,
                Stopwatch.Frequency)
        {
        }

        internal PairingNegotiationStateMachine(
            Guid localInstanceId,
            string localEndpoint,
            ulong localLastPeerKeyEpoch,
            Func<long> timestampProvider,
            long timestampFrequency)
        {
            if (localInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The local instance ID must not be empty.",
                    nameof(localInstanceId));
            }

            if (timestampProvider == null)
            {
                throw new ArgumentNullException(nameof(timestampProvider));
            }

            if (timestampFrequency <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampFrequency));
            }

            _localInstanceId = localInstanceId;
            _localEndpoint = ValidateCanonicalEndpoint(
                localEndpoint,
                nameof(localEndpoint));
            _localLastPeerKeyEpoch = localLastPeerKeyEpoch;
            _timestampProvider = timestampProvider;
            _timestampFrequency = timestampFrequency;

            long initialTimestamp = timestampProvider();
            if (initialTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timestampProvider),
                    "The monotonic timestamp provider returned a negative value.");
            }

            _lastObservedTimestamp = initialTimestamp;
            _state = PairingNegotiationState.Unpaired;
        }

        internal PairingNegotiationState State
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    ExpireIfNeeded(GetMonotonicTimestamp());
                    return _state;
                }
            }
        }

        internal int HelloAttempts
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    ExpireIfNeeded(GetMonotonicTimestamp());
                    return _helloAttempts;
                }
            }
        }

        internal PeerPairingRole? LocalRole
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    ExpireIfNeeded(GetMonotonicTimestamp());
                    return _localRole;
                }
            }
        }

        internal Guid? CurrentPairingId
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    ExpireIfNeeded(GetMonotonicTimestamp());
                    return _pairingId == Guid.Empty
                        ? (Guid?)null
                        : _pairingId;
                }
            }
        }

        internal Guid? CurrentPeerInstanceId
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    ExpireIfNeeded(GetMonotonicTimestamp());
                    return _peerInstanceId == Guid.Empty
                        ? (Guid?)null
                        : _peerInstanceId;
                }
            }
        }

        internal string CurrentPeerEndpoint
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    ExpireIfNeeded(GetMonotonicTimestamp());
                    return _peerEndpoint;
                }
            }
        }

        internal ulong? CurrentKeyEpoch
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    ExpireIfNeeded(GetMonotonicTimestamp());
                    return _keyEpoch == 0 ? (ulong?)null : _keyEpoch;
                }
            }
        }

        internal bool LocalConfirmed
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    ExpireIfNeeded(GetMonotonicTimestamp());
                    return _localDecisionRequest != null
                        && _localDecisionRequest.Decision
                            == PeerPairingDecisionValue.Confirmed;
                }
            }
        }

        internal bool RemoteConfirmed
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    ExpireIfNeeded(GetMonotonicTimestamp());
                    return _remoteDecisionRequest != null
                        && _remoteDecisionRequest.Decision
                            == PeerPairingDecisionValue.Confirmed;
                }
            }
        }

        internal TimeSpan RemainingPairingTime
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    long now = GetMonotonicTimestamp();
                    ExpireIfNeeded(now);
                    if (_state == PairingNegotiationState.Unpaired
                        || now >= _deadlineTimestamp)
                    {
                        return TimeSpan.Zero;
                    }

                    double seconds = (double)(_deadlineTimestamp - now)
                        / _timestampFrequency;
                    return TimeSpan.FromSeconds(seconds);
                }
            }
        }

        internal void OpenWindow(string peerEndpoint)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ExpireIfNeeded(GetMonotonicTimestamp());
                if (_state != PairingNegotiationState.Unpaired)
                {
                    throw new InvalidOperationException(
                        "Only one pairing window may be active per service instance.");
                }

                if (_localLastPeerKeyEpoch == ulong.MaxValue)
                {
                    throw new InvalidOperationException(
                        "A new pairing cannot be issued after UInt64.MaxValue.");
                }

                string canonicalPeerEndpoint = ValidateCanonicalEndpoint(
                    peerEndpoint,
                    nameof(peerEndpoint));
                long now = GetMonotonicTimestamp();
                _peerEndpoint = canonicalPeerEndpoint;
                _deadlineTimestamp = AddDuration(
                    now,
                    PairingWindowLifetime);
                _helloAttempts = 0;
                _state = PairingNegotiationState.PairingWindowOpen;
            }
        }

        internal void BeginInitiator(PeerPairingHelloRequest request)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                EnsureWindowActive();
                if (_state != PairingNegotiationState.PairingWindowOpen)
                {
                    throw new InvalidOperationException(
                        "An active pairing already owns the pairing window.");
                }

                try
                {
                    if (request == null)
                    {
                        throw new ArgumentNullException(nameof(request));
                    }

                    if (request.InitiatorInstanceId != _localInstanceId
                        || !StringComparer.Ordinal.Equals(
                            request.InitiatorEndpoint,
                            _localEndpoint)
                        || request.InitiatorLastPeerKeyEpoch
                            != _localLastPeerKeyEpoch)
                    {
                        throw new ArgumentException(
                            "The initiator hello is not bound to the local instance.",
                            nameof(request));
                    }

                    SetInitiatorHello(request);
                    _localRole = PeerPairingRole.Initiator;
                    _state = PairingNegotiationState.Negotiating;
                }
                catch
                {
                    FailIfBeforeBothConfirmed();
                    throw;
                }
            }
        }

        internal PairingHelloDisposition ReceiveHello(
            PeerPairingHelloRequest request,
            string remoteEndpoint)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                EnsureWindowActive();
                try
                {
                    if (request == null)
                    {
                        throw new ArgumentNullException(nameof(request));
                    }

                    string canonicalRemoteEndpoint = ValidateCanonicalEndpoint(
                        remoteEndpoint,
                        nameof(remoteEndpoint));
                    if (!StringComparer.Ordinal.Equals(
                            canonicalRemoteEndpoint,
                            _peerEndpoint)
                        || !StringComparer.Ordinal.Equals(
                            request.InitiatorEndpoint,
                            _peerEndpoint))
                    {
                        throw new ArgumentException(
                            "The pairing hello did not arrive from the designated peer endpoint.",
                            nameof(remoteEndpoint));
                    }

                    _helloAttempts = checked(_helloAttempts + 1);
                    if (_helloAttempts > MaximumHelloAttempts)
                    {
                        throw new InvalidOperationException(
                            "The pairing window received more than three hello requests.");
                    }

                    if (request.InitiatorInstanceId == _localInstanceId)
                    {
                        throw new InvalidOperationException(
                            "The peer uses the same instance ID as the local installation.");
                    }

                    if (_state == PairingNegotiationState.PairingWindowOpen)
                    {
                        AdoptResponderHello(request);
                        return PairingHelloDisposition.AcceptedAsResponder;
                    }

                    if (_state != PairingNegotiationState.Negotiating)
                    {
                        throw new InvalidOperationException(
                            "A hello request is not valid in the current pairing state.");
                    }

                    if (_localRole == PeerPairingRole.Responder)
                    {
                        if (!InitiatorHelloMatches(request))
                        {
                            throw new InvalidOperationException(
                                "A different hello attempted to replace the active pairing.");
                        }

                        return PairingHelloDisposition
                            .ReplayedResponderHello;
                    }

                    if (_localRole != PeerPairingRole.Initiator)
                    {
                        throw new InvalidOperationException(
                            "The active pairing has no valid local role.");
                    }

                    int instanceOrder = CompareCanonicalInstanceIds(
                        _localInstanceId,
                        request.InitiatorInstanceId);
                    if (instanceOrder < 0)
                    {
                        return PairingHelloDisposition.RetainedInitiator;
                    }

                    ClearOwnedNegotiationMaterial();
                    AdoptResponderHello(request);
                    return PairingHelloDisposition.AcceptedAsResponder;
                }
                catch
                {
                    FailIfBeforeBothConfirmed();
                    throw;
                }
            }
        }

        // Takes ownership of secretContext. The caller must not use or dispose
        // it after this method returns, regardless of success or failure.
        internal void CompleteHello(
            PeerPairingHelloResult result,
            PairingSecretContext secretContext)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                EnsureWindowActive();
                byte[] responderNonce = null;
                byte[] responderPublicKey = null;
                byte[] transcriptHash = null;
                bool contextStored = false;
                try
                {
                    if (_state != PairingNegotiationState.Negotiating
                        || !_localRole.HasValue
                        || _pairingId == Guid.Empty)
                    {
                        throw new InvalidOperationException(
                            "A hello response cannot complete the current pairing state.");
                    }

                    if (result == null)
                    {
                        throw new ArgumentNullException(nameof(result));
                    }

                    if (secretContext == null)
                    {
                        throw new ArgumentNullException(nameof(secretContext));
                    }

                    ValidateHelloResultBinding(result);
                    responderNonce = result.CopyResponderNonce();
                    responderPublicKey = result.CopyResponderPublicKey();
                    transcriptHash = PairingTranscript.CreateHash(
                        _pairingId,
                        _initiatorInstanceId,
                        result.ResponderInstanceId,
                        _initiatorEndpoint,
                        result.ResponderEndpoint,
                        _initiatorNonce,
                        responderNonce,
                        _initiatorPublicKey,
                        responderPublicKey,
                        _initiatorLastPeerKeyEpoch,
                        result.ResponderLastPeerKeyEpoch,
                        result.KeyEpoch);

                    if (!secretContext.IsBoundToTranscriptHash(transcriptHash))
                    {
                        throw new InvalidOperationException(
                            "The pairing secret is not bound to the negotiated transcript.");
                    }

                    if (_secretContext != null)
                    {
                        if (_keyEpoch != result.KeyEpoch
                            || _peerInstanceId
                                != GetPeerInstanceId(result)
                            || !PairingCryptography.FixedTimeEquals32(
                                _transcriptHash,
                                transcriptHash))
                        {
                            throw new InvalidOperationException(
                                "A different hello result attempted to replace the negotiated transcript.");
                        }

                        contextStored = ReferenceEquals(
                            secretContext,
                            _secretContext);
                        return;
                    }

                    _peerInstanceId = GetPeerInstanceId(result);
                    _keyEpoch = result.KeyEpoch;
                    _transcriptHash = transcriptHash;
                    transcriptHash = null;
                    _secretContext = secretContext;
                    contextStored = true;
                }
                catch
                {
                    FailIfBeforeBothConfirmed();
                    throw;
                }
                finally
                {
                    Clear(responderNonce);
                    Clear(responderPublicKey);
                    Clear(transcriptHash);
                    if (!contextStored
                        && secretContext != null
                        && !ReferenceEquals(secretContext, _secretContext))
                    {
                        secretContext.Dispose();
                    }
                }
            }
        }

        internal PeerPairingKeyConfirmation CreateLocalKeyConfirmation()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                EnsureWindowActive();
                try
                {
                    EnsureKeyConfirmationState();
                    if (_localConfirmationMac == null)
                    {
                        _localConfirmationMac = _secretContext
                            .CreateConfirmationMac(
                                ToCryptographyRole(_localRole.Value));
                    }

                    var confirmation = new PeerPairingKeyConfirmation(
                        _pairingId,
                        _keyEpoch,
                        _localRole.Value,
                        _localInstanceId,
                        _peerInstanceId,
                        _transcriptHash,
                        _localConfirmationMac);
                    EnterSasPendingIfReady();
                    return confirmation;
                }
                catch
                {
                    FailIfBeforeBothConfirmed();
                    throw;
                }
            }
        }

        internal void AcceptRemoteKeyConfirmation(
            PeerPairingKeyConfirmation confirmation)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                EnsureWindowActive();
                byte[] transcriptHash = null;
                byte[] candidateMac = null;
                byte[] expectedMac = null;
                try
                {
                    EnsureKeyConfirmationState();
                    if (confirmation == null)
                    {
                        throw new ArgumentNullException(nameof(confirmation));
                    }

                    PeerPairingRole remoteRole = OppositeRole(
                        _localRole.Value);
                    if (confirmation.PairingId != _pairingId
                        || confirmation.KeyEpoch != _keyEpoch
                        || confirmation.SenderRole != remoteRole
                        || confirmation.SenderInstanceId != _peerInstanceId
                        || confirmation.ReceiverInstanceId
                            != _localInstanceId)
                    {
                        throw new InvalidOperationException(
                            "The key confirmation binding does not match the active pairing.");
                    }

                    transcriptHash = confirmation.CopyTranscriptHash();
                    if (!PairingCryptography.FixedTimeEquals32(
                            _transcriptHash,
                            transcriptHash))
                    {
                        throw new InvalidOperationException(
                            "The key confirmation transcript does not match.");
                    }

                    candidateMac = confirmation.CopyConfirmationMac();
                    expectedMac = _secretContext.CreateConfirmationMac(
                        ToCryptographyRole(remoteRole));
                    if (!PairingCryptography.FixedTimeEquals32(
                            expectedMac,
                            candidateMac))
                    {
                        throw new InvalidOperationException(
                            "The key confirmation MAC is invalid.");
                    }

                    if (_remoteConfirmationMac != null)
                    {
                        if (!PairingCryptography.FixedTimeEquals32(
                                _remoteConfirmationMac,
                                candidateMac))
                        {
                            throw new InvalidOperationException(
                                "A different remote key confirmation was received.");
                        }
                    }
                    else
                    {
                        _remoteConfirmationMac =
                            (byte[])candidateMac.Clone();
                    }

                    EnterSasPendingIfReady();
                }
                catch
                {
                    FailIfBeforeBothConfirmed();
                    throw;
                }
                finally
                {
                    Clear(transcriptHash);
                    Clear(candidateMac);
                    Clear(expectedMac);
                }
            }
        }

        internal bool TryCopySas(out char[] sas)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ExpireIfNeeded(GetMonotonicTimestamp());
                if (_state != PairingNegotiationState.SasPending
                    || _sas == null)
                {
                    sas = null;
                    return false;
                }

                sas = (char[])_sas.Clone();
                return true;
            }
        }

        internal void Cancel(Guid pairingId)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ExpireIfNeeded(GetMonotonicTimestamp());
                if (_state == PairingNegotiationState.Unpaired
                    || _state == PairingNegotiationState.PairingWindowOpen
                    || _state == PairingNegotiationState.BothConfirmed
                    || pairingId == Guid.Empty
                    || pairingId != _pairingId)
                {
                    throw new InvalidOperationException(
                        "The pairing cancellation does not match an active transient pairing.");
                }

                ResetToUnpaired();
            }
        }

        internal void FailCurrentPairing()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ExpireIfNeeded(GetMonotonicTimestamp());
                if (_state != PairingNegotiationState.BothConfirmed)
                {
                    ResetToUnpaired();
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                ResetToUnpaired();
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }

        private void AdoptResponderHello(PeerPairingHelloRequest request)
        {
            SetInitiatorHello(request);
            _peerInstanceId = request.InitiatorInstanceId;
            _localRole = PeerPairingRole.Responder;
            _state = PairingNegotiationState.Negotiating;
        }

        private void SetInitiatorHello(PeerPairingHelloRequest request)
        {
            byte[] nonce = null;
            byte[] publicKey = null;
            try
            {
                nonce = request.CopyInitiatorNonce();
                publicKey = request.CopyInitiatorPublicKey();
                Clear(_initiatorNonce);
                Clear(_initiatorPublicKey);
                _pairingId = request.PairingId;
                _initiatorInstanceId = request.InitiatorInstanceId;
                _initiatorEndpoint = request.InitiatorEndpoint;
                _initiatorLastPeerKeyEpoch =
                    request.InitiatorLastPeerKeyEpoch;
                _initiatorNonce = nonce;
                _initiatorPublicKey = publicKey;
                nonce = null;
                publicKey = null;
            }
            finally
            {
                Clear(nonce);
                Clear(publicKey);
            }
        }

        private bool InitiatorHelloMatches(PeerPairingHelloRequest request)
        {
            if (request.PairingId != _pairingId
                || request.InitiatorInstanceId != _initiatorInstanceId
                || !StringComparer.Ordinal.Equals(
                    request.InitiatorEndpoint,
                    _initiatorEndpoint)
                || request.InitiatorLastPeerKeyEpoch
                    != _initiatorLastPeerKeyEpoch)
            {
                return false;
            }

            byte[] nonce = null;
            byte[] publicKey = null;
            try
            {
                nonce = request.CopyInitiatorNonce();
                publicKey = request.CopyInitiatorPublicKey();
                return BytesEqual(_initiatorNonce, nonce)
                    && BytesEqual(_initiatorPublicKey, publicKey);
            }
            finally
            {
                Clear(nonce);
                Clear(publicKey);
            }
        }

        private void ValidateHelloResultBinding(
            PeerPairingHelloResult result)
        {
            if (result.PairingId != _pairingId)
            {
                throw new InvalidOperationException(
                    "The hello result pairing ID does not match.");
            }

            if (_localRole == PeerPairingRole.Initiator)
            {
                if (result.ResponderInstanceId == _localInstanceId
                    || !StringComparer.Ordinal.Equals(
                        result.ResponderEndpoint,
                        _peerEndpoint))
                {
                    throw new InvalidOperationException(
                        "The hello result is not bound to the designated peer.");
                }
            }
            else if (_localRole == PeerPairingRole.Responder)
            {
                if (result.ResponderInstanceId != _localInstanceId
                    || !StringComparer.Ordinal.Equals(
                        result.ResponderEndpoint,
                        _localEndpoint)
                    || result.ResponderLastPeerKeyEpoch
                        != _localLastPeerKeyEpoch)
                {
                    throw new InvalidOperationException(
                        "The hello result is not bound to the local responder.");
                }
            }
            else
            {
                throw new InvalidOperationException(
                    "The active pairing has no local role.");
            }
        }

        private Guid GetPeerInstanceId(PeerPairingHelloResult result)
        {
            return _localRole == PeerPairingRole.Initiator
                ? result.ResponderInstanceId
                : _initiatorInstanceId;
        }

        private void EnsureKeyConfirmationState()
        {
            if ((_state != PairingNegotiationState.Negotiating
                    && _state != PairingNegotiationState.SasPending)
                || _secretContext == null
                || _transcriptHash == null
                || !_localRole.HasValue
                || _peerInstanceId == Guid.Empty
                || _keyEpoch == 0)
            {
                throw new InvalidOperationException(
                    "Key confirmation is not available in the current pairing state.");
            }
        }

        private void EnterSasPendingIfReady()
        {
            if (_state == PairingNegotiationState.Negotiating
                && _localConfirmationMac != null
                && _remoteConfirmationMac != null)
            {
                _sas = _secretContext.CreateSas();
                _state = PairingNegotiationState.SasPending;
            }
        }

    }
}
