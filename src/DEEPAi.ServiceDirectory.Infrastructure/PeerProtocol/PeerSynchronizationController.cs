using System;
using System.Security.Cryptography;
using System.Threading;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public sealed partial class PeerSynchronizationController :
        IAdminSynchronizationController,
        IPeerHttpRequestHandler,
        IDisposable
    {
        private static readonly TimeSpan PairingDisplayLifetime =
            TimeSpan.FromMinutes(5);
        private static readonly TimeSpan SynchronizationInterval =
            TimeSpan.FromMinutes(10);

        private readonly object _gate = new object();
        private readonly ServiceDirectoryRuntimeConfigurationState
            _configurationState;
        private readonly StateMutationCoordinator _stateCoordinator;
        private readonly SystemFileLogger _systemLog;
        private readonly SecurityAuditEventLogger _securityAuditLogger;
        private readonly IPeerHttpTransport _transport;
        private readonly ICertificateAuthorityPeerSynchronization _peerPki;
        private readonly Func<DateTimeOffset> _utcNowProvider;
        private readonly Timer _periodicTimer;

        private PairingNegotiationStateMachine _pairing;
        private ECDiffieHellmanCng _pairingKeyAgreement;
        private PeerPairingHelloResult _responderHelloResult;
        private DateTime _pairingExpiresUtc;
        private int _pairingGeneration;

        private PeerPairAuthenticationContext _pairAuthentication;
        private PeerInboundRequestCoordinator _inboundAuthentication;
        private ActivePeerSession _activeSession;
        private PeerPushBatchProcessor _pushProcessor;
        private PeerOutboundSnapshotLease _outboundLease;

        private bool _syncRunning;
        private bool _initialSynchronizationPending;
        private bool _disposed;

        public PeerSynchronizationController(
            ServiceDirectoryRuntimeConfigurationState configurationState,
            StateMutationCoordinator stateCoordinator,
            SystemFileLogger systemFileLogger,
            SecurityAuditEventLogger securityAuditLogger)
            : this(
                configurationState,
                stateCoordinator,
                systemFileLogger,
                securityAuditLogger,
                new SystemPeerHttpTransport(),
                () => DateTimeOffset.UtcNow,
                null)
        {
        }

        public PeerSynchronizationController(
            ServiceDirectoryRuntimeConfigurationState configurationState,
            StateMutationCoordinator stateCoordinator,
            SystemFileLogger systemFileLogger,
            SecurityAuditEventLogger securityAuditLogger,
            ICertificateAuthorityPeerSynchronization peerPki)
            : this(
                configurationState,
                stateCoordinator,
                systemFileLogger,
                securityAuditLogger,
                new SystemPeerHttpTransport(
                    RequireTlsTrustProvider(peerPki)),
                () => DateTimeOffset.UtcNow,
                peerPki)
        {
        }

        internal PeerSynchronizationController(
            ServiceDirectoryRuntimeConfigurationState configurationState,
            StateMutationCoordinator stateCoordinator,
            SystemFileLogger systemFileLogger,
            SecurityAuditEventLogger securityAuditLogger,
            IPeerHttpTransport transport,
            Func<DateTimeOffset> utcNowProvider)
            : this(
                configurationState,
                stateCoordinator,
                systemFileLogger,
                securityAuditLogger,
                transport,
                utcNowProvider,
                null)
        {
        }

        internal PeerSynchronizationController(
            ServiceDirectoryRuntimeConfigurationState configurationState,
            StateMutationCoordinator stateCoordinator,
            SystemFileLogger systemFileLogger,
            SecurityAuditEventLogger securityAuditLogger,
            IPeerHttpTransport transport,
            Func<DateTimeOffset> utcNowProvider,
            ICertificateAuthorityPeerSynchronization peerPki)
        {
            _configurationState = configurationState
                ?? throw new ArgumentNullException(
                    nameof(configurationState));
            _stateCoordinator = stateCoordinator
                ?? throw new ArgumentNullException(
                    nameof(stateCoordinator));
            _systemLog = systemFileLogger
                ?? throw new ArgumentNullException(
                    nameof(systemFileLogger));
            _securityAuditLogger = securityAuditLogger
                ?? throw new ArgumentNullException(
                    nameof(securityAuditLogger));
            _transport = transport ?? throw new ArgumentNullException(
                nameof(transport));
            _peerPki = peerPki;
            _utcNowProvider = utcNowProvider
                ?? throw new ArgumentNullException(
                    nameof(utcNowProvider));

            lock (_gate)
            {
                RefreshDurablePeerContextLocked();
                ServiceDirectoryConfiguration configuration =
                    _configurationState.GetCurrent();
                _initialSynchronizationPending =
                    configuration.Synchronization.State
                        == DurableSynchronizationState.Enabled;
            }

            _periodicTimer = new Timer(
                OnPeriodicTimer,
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }

        public AdminHandlerResult<AdminServerSyncStatusResponse> GetStatus()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return AdminHandlerResult<AdminServerSyncStatusResponse>
                    .Success(CreateStatusLocked());
            }
        }

        public AdminHandlerResult<AdminServerUnitResponse> Enable(
            AdminEnableSyncRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            bool queuePairing = false;
            bool queueInitialSync = false;
            if (request.RePair
                && !ForgetCurrentPeerForRepair())
            {
                return FailureUnit(AdminServerErrorCode.Internal);
            }

            lock (_gate)
            {
                ThrowIfDisposed();
                ServiceDirectoryConfiguration configuration =
                    _configurationState.GetCurrent();
                DurableSynchronizationState state =
                    configuration.Synchronization.State;

                if (state == DurableSynchronizationState.Unpaired)
                {
                    if (_pairing != null
                        && _pairing.State
                            != PairingNegotiationState.Unpaired)
                    {
                        return FailureUnit(
                            AdminServerErrorCode.Conflict);
                    }

                    OpenPairingWindowLocked(
                        configuration,
                        request.PeerEndpoint);
                    queuePairing = true;
                }
                else if (state
                        == DurableSynchronizationState.PairedDisabled
                    && !request.RePair
                    && StringComparer.Ordinal.Equals(
                        configuration.Synchronization.PeerEndpoint,
                        request.PeerEndpoint))
                {
                    using (PairedPeerCredential current =
                        _configurationState.CopyCredential())
                    using (PairedPeerCredential enabled =
                        ChangeCredentialState(
                            current,
                            DurablePeerCredentialState.Enabled))
                    {
                        SynchronizationConfiguration synchronization =
                            SynchronizationConfiguration.Enabled(
                                configuration.Synchronization.PeerEndpoint,
                                configuration.Synchronization
                                    .PeerInstanceId.Value,
                                configuration.Synchronization
                                    .KeyEpoch.Value,
                                configuration.Synchronization
                                    .LastSynchronization,
                                configuration.Synchronization
                                    .LastPeerNotification);
                        if (!CommitSynchronizationLocked(
                                configuration.LastPeerKeyEpoch,
                                synchronization,
                                enabled))
                        {
                            return FailureUnit(
                                AdminServerErrorCode.Internal);
                        }
                    }

                    _initialSynchronizationPending = true;
                    queueInitialSync = true;
                }
                else
                {
                    return FailureUnit(AdminServerErrorCode.Conflict);
                }
            }

            if (queuePairing)
            {
                QueuePairingInitiatorAttempt();
            }

            if (queueInitialSync)
            {
                QueueSynchronization("ADMIN_ENABLE", true);
            }

            return UnitSuccess();
        }

        public AdminHandlerResult<AdminServerUnitResponse> ConfirmPairing(
            AdminPairingConfirmationRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            PairingLocalDecisionMessage decisionMessage;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!request.Confirmed)
                {
                    return FailureUnit(AdminServerErrorCode.Conflict);
                }

                if (_pairing == null
                    || _pairing.CurrentPairingId != request.PairingId)
                {
                    using (PairedPeerCredential durableCredential =
                        _configurationState.CopyCredential())
                    {
                        return durableCredential != null
                                && durableCredential.PairingId
                                    == request.PairingId
                            ? UnitSuccess()
                            : FailureUnit(
                                AdminServerErrorCode.Conflict);
                    }
                }

                try
                {
                    decisionMessage = _pairing.CreateLocalDecision(
                        request.PairingId,
                        PeerPairingDecisionValue.Confirmed);
                    if (_pairing.State
                        == PairingNegotiationState.BothConfirmed
                        && !PersistBothConfirmedLocked())
                    {
                        decisionMessage.Dispose();
                        return FailureUnit(AdminServerErrorCode.Internal);
                    }
                }
                catch (InvalidOperationException)
                {
                    return FailureUnit(AdminServerErrorCode.Conflict);
                }
            }

            QueueLocalPairingDecision(decisionMessage);
            return UnitSuccess();
        }

        public AdminHandlerResult<AdminServerUnitResponse> CancelPairing(
            AdminPairingCancellationRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            lock (_gate)
            {
                ThrowIfDisposed();
                ServiceDirectoryConfiguration configuration =
                    _configurationState.GetCurrent();
                if (configuration.Synchronization.State
                    == DurableSynchronizationState.PairedPendingCommit)
                {
                    if (configuration.Synchronization.PairingId
                            != request.PairingId
                        || !CommitUnpairedLocked(
                            configuration,
                            configuration.Synchronization
                                .LastPeerNotification))
                    {
                        return FailureUnit(
                            configuration.Synchronization.PairingId
                                != request.PairingId
                                ? AdminServerErrorCode.Conflict
                                : AdminServerErrorCode.Internal);
                    }

                    return UnitSuccess();
                }

                if (_pairing == null
                    || _pairing.CurrentPairingId != request.PairingId)
                {
                    return FailureUnit(AdminServerErrorCode.Conflict);
                }

                try
                {
                    _pairing.Cancel(request.PairingId);
                    DisposeTransientPairingLocked();
                    DisposePairingDecisionReplayLocked();
                    return UnitSuccess();
                }
                catch (InvalidOperationException)
                {
                    return FailureUnit(AdminServerErrorCode.Conflict);
                }
            }
        }

        public AdminHandlerResult<AdminServerSyncDisableResponse> Disable(
            AdminDisableSyncRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            return DisableCore(request.ForgetPeer);
        }

        public AdminHandlerResult<AdminServerUnitResponse> SynchronizeNow()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_configurationState.GetCurrent()
                        .Synchronization.State
                        != DurableSynchronizationState.Enabled)
                {
                    return FailureUnit(
                        AdminServerErrorCode.SyncDisabled);
                }

                if (_syncRunning)
                {
                    return FailureUnit(AdminServerErrorCode.Conflict);
                }

                _syncRunning = true;
                _outboundSynchronizationSuperseded = false;
            }

            QueueSynchronizationWorker("ADMIN_MANUAL", false);
            return UnitSuccess();
        }

        public void ScheduleDirectoryChanged()
        {
            QueueSynchronization("DIRECTORY_CHANGED", false);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_backgroundWorkCount != 0
                    || (_started && !_stopped)
                    || (_stopping && !_stopped))
                {
                    throw new InvalidOperationException(
                        "Peer synchronization must drain before disposal.");
                }

                _disposed = true;
                _periodicTimer.Dispose();
                DisposeTransientPairingLocked();
                DisposePairingDecisionReplayLocked();
                DisposeSessionLocked();
                DisposePairAuthenticationLocked();
                if (_fatalException == null)
                {
                    _completion.TrySetResult(null);
                }
            }

            _backgroundWorkDrained.Dispose();

            GC.SuppressFinalize(this);
        }

        private void OpenPairingWindowLocked(
            ServiceDirectoryConfiguration configuration,
            string peerEndpoint)
        {
            DisposePairingDecisionReplayLocked();
            DisposeTransientPairingLocked();
            var listenerAddress = RequireListenerAddress(
                configuration.ListenAddress);
            _pairing = new PairingNegotiationStateMachine(
                configuration.InstanceId,
                listenerAddress.HttpsPrefix.TrimEnd('/'),
                configuration.LastPeerKeyEpoch);
            _pairing.OpenWindow(peerEndpoint);
            DateTime now = GetUtcNow();
            _pairingExpiresUtc = now.Add(PairingDisplayLifetime);
            _pairingGeneration = NextGeneration(_pairingGeneration);
        }

        private AdminServerSyncStatusResponse CreateStatusLocked()
        {
            ServiceDirectoryConfiguration configuration =
                _configurationState.GetCurrent();
            SynchronizationConfiguration durable =
                configuration.Synchronization;
            AdminPairingState state;
            string peerEndpoint;
            Guid? peerInstanceId;
            ulong? keyEpoch;
            Guid? pairingId;
            string sas = null;
            DateTime? pairingExpiresUtc = null;
            int? remainingSeconds = null;
            bool? localConfirmed = null;
            bool? remoteConfirmed = null;

            PairingNegotiationState? transientState = null;
            if (durable.State
                    != DurableSynchronizationState.PairedPendingCommit
                && _pairing != null)
            {
                PairingNegotiationState candidate = _pairing.State;
                if (candidate != PairingNegotiationState.Unpaired)
                {
                    transientState = candidate;
                }
            }

            if (transientState.HasValue)
            {
                state = MapPairingState(transientState.Value);
                peerEndpoint = _pairing.CurrentPeerEndpoint;
                peerInstanceId = _pairing.CurrentPeerInstanceId;
                keyEpoch = null;
                pairingId = _pairing.CurrentPairingId;
                TimeSpan remaining = _pairing.RemainingPairingTime;
                pairingExpiresUtc = _pairingExpiresUtc;
                remainingSeconds = Math.Max(
                    0,
                    Math.Min(
                        300,
                        (int)Math.Floor(remaining.TotalSeconds)));
                if (state == AdminPairingState.SasPending
                    || state == AdminPairingState.BothConfirmed)
                {
                    localConfirmed = _pairing.LocalConfirmed;
                    remoteConfirmed = _pairing.RemoteConfirmed;
                    if (state == AdminPairingState.SasPending
                        && localConfirmed == false)
                    {
                        char[] sasBuffer;
                        if (_pairing.TryCopySas(out sasBuffer))
                        {
                            try
                            {
                                sas = new string(sasBuffer);
                            }
                            finally
                            {
                                Array.Clear(
                                    sasBuffer,
                                    0,
                                    sasBuffer.Length);
                            }
                        }
                    }
                }
            }
            else
            {
                state = MapDurableState(durable.State);
                peerEndpoint = durable.PeerEndpoint;
                peerInstanceId = durable.PeerInstanceId;
                keyEpoch = durable.KeyEpoch;
                pairingId = durable.PairingId;
            }

            return new AdminServerSyncStatusResponse(
                state == AdminPairingState.Enabled,
                state,
                peerEndpoint,
                peerInstanceId,
                keyEpoch,
                durable.LastSynchronization.LastSyncUtc,
                durable.LastSynchronization.Result,
                durable.LastSynchronization.ClockSkewSeconds,
                pairingId,
                sas,
                pairingExpiresUtc,
                remainingSeconds,
                localConfirmed,
                remoteConfirmed,
                state == AdminPairingState.PairedPendingCommit
                    ? durable.CommitExpiresUtc
                    : null,
                state == AdminPairingState.PairedPendingCommit
                    ? durable.LocalCommitConfirmed
                    : null,
                state == AdminPairingState.PairedPendingCommit
                    ? durable.RemoteCommitConfirmed
                    : null,
                MapNotificationOperation(
                    durable.LastPeerNotification.Operation),
                MapNotificationResult(
                    durable.LastPeerNotification.Result),
                durable.LastPeerNotification.NotificationUtc);
        }

        private bool CommitSynchronizationLocked(
            ulong lastPeerKeyEpoch,
            SynchronizationConfiguration synchronization,
            PairedPeerCredential credential)
        {
            ServiceDirectoryConfiguration current =
                _configurationState.GetCurrent();
            bool credentialChanged;
            using (PairedPeerCredential existing =
                _configurationState.CopyCredential())
            {
                credentialChanged =
                    !PeerCredentialValueComparer.Equals(
                        existing,
                        credential);
            }

            ServiceDirectoryConfiguration next = current.WithSynchronization(
                lastPeerKeyEpoch,
                synchronization);
            RuntimeConfigurationCommitResult result =
                _configurationState.CommitPeerState(next, credential);
            if (result.Status != RuntimeConfigurationCommitStatus.Completed)
            {
                return false;
            }

            if (_syncRunning
                && current.Synchronization.State
                    == DurableSynchronizationState.Enabled
                && synchronization.State
                    != DurableSynchronizationState.Enabled)
            {
                // Any successful Enabled -> non-Enabled transition owns the
                // linearization point against an in-flight outbound pull.
                // The final pull publication checks this flag while holding
                // the same controller gate.
                _outboundSynchronizationSuperseded = true;
            }

            if (credentialChanged
                || (credential != null
                    && _pairAuthentication == null))
            {
                RefreshDurablePeerContextLocked();
            }
            return true;
        }

        private bool CommitUnpairedLocked(
            ServiceDirectoryConfiguration current,
            PeerNotificationStatus notification)
        {
            var synchronization = SynchronizationConfiguration.Unpaired(
                current.Synchronization.LastSynchronization,
                notification);
            bool committed = CommitSynchronizationLocked(
                current.LastPeerKeyEpoch,
                synchronization,
                null);
            if (committed)
            {
                DisposeSessionLocked();
                DisposeTransientPairingLocked();
                DisposePairingDecisionReplayLocked();
            }

            return committed;
        }

        private void RefreshDurablePeerContextLocked()
        {
            DisposePairAuthenticationLocked();
            using (PairedPeerCredential credential =
                _configurationState.CopyCredential())
            {
                if (credential == null)
                {
                    return;
                }

                byte[] pairRoot = null;
                try
                {
                    pairRoot = credential.CopyPairRoot();
                    _pairAuthentication =
                        PeerPairAuthenticationContext.CreateFromPairRoot(
                            credential.LocalInstanceId,
                            credential.PeerInstanceId,
                            credential.KeyEpoch,
                            pairRoot);
                    _inboundAuthentication =
                        new PeerInboundRequestCoordinator(
                            new PeerRequestRateLimiter(
                                credential.PeerEndpoint,
                                credential.PeerInstanceId));
                }
                finally
                {
                    Clear(pairRoot);
                }
            }
        }

        private void DisposePairAuthenticationLocked()
        {
            if (_pairAuthentication != null)
            {
                _pairAuthentication.Dispose();
                _pairAuthentication = null;
            }

            _inboundAuthentication = null;
        }

        private void DisposeSessionLocked()
        {
            if (_activeSession != null)
            {
                _activeSession.Dispose();
                _activeSession = null;
            }

            _pushProcessor = null;
            _outboundLease = null;
        }

        private void DisposeTransientPairingLocked()
        {
            if (_pairing != null)
            {
                _pairing.Dispose();
                _pairing = null;
            }

            if (_pairingKeyAgreement != null)
            {
                _pairingKeyAgreement.Dispose();
                _pairingKeyAgreement = null;
            }

            _responderHelloResult = null;
            _pairingExpiresUtc = default(DateTime);
            _pairingGeneration = NextGeneration(_pairingGeneration);
        }

        private static PairedPeerCredential ChangeCredentialState(
            PairedPeerCredential current,
            DurablePeerCredentialState state)
        {
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            byte[] transcriptHash = null;
            byte[] pairRoot = null;
            try
            {
                transcriptHash = current.CopyTranscriptHash();
                pairRoot = current.CopyPairRoot();
                return new PairedPeerCredential(
                    state,
                    current.LocalRole,
                    current.PairingId,
                    current.LocalInstanceId,
                    current.PeerInstanceId,
                    current.LocalEndpoint,
                    current.PeerEndpoint,
                    current.KeyEpoch,
                    transcriptHash,
                    pairRoot,
                    current.CommitExpiresUtc,
                    current.LocalCommitConfirmed,
                    current.RemoteCommitConfirmed,
                    current.LocalCommitEvidence,
                    current.RemoteCommitEvidence);
            }
            finally
            {
                Clear(transcriptHash);
                Clear(pairRoot);
            }
        }

        private static ServiceDirectoryListenerAddress RequireListenerAddress(
            string value)
        {
            ServiceDirectoryListenerAddress address;
            if (!ServiceDirectoryListenerAddress.TryCreate(
                    value,
                    out address))
            {
                throw new InvalidOperationException(
                    "The loaded ListenAddress is invalid.");
            }

            return address;
        }

        private static AdminPairingState MapPairingState(
            PairingNegotiationState state)
        {
            switch (state)
            {
                case PairingNegotiationState.PairingWindowOpen:
                    return AdminPairingState.PairingWindowOpen;
                case PairingNegotiationState.Negotiating:
                    return AdminPairingState.Negotiating;
                case PairingNegotiationState.SasPending:
                    return AdminPairingState.SasPending;
                case PairingNegotiationState.BothConfirmed:
                    return AdminPairingState.BothConfirmed;
                case PairingNegotiationState.Unpaired:
                default:
                    return AdminPairingState.Unpaired;
            }
        }

        private static AdminPairingState MapDurableState(
            DurableSynchronizationState state)
        {
            switch (state)
            {
                case DurableSynchronizationState.Unpaired:
                    return AdminPairingState.Unpaired;
                case DurableSynchronizationState.PairedPendingCommit:
                    return AdminPairingState.PairedPendingCommit;
                case DurableSynchronizationState.PairedDisabled:
                    return AdminPairingState.PairedDisabled;
                case DurableSynchronizationState.Enabled:
                    return AdminPairingState.Enabled;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state));
            }
        }

        private static AdminPeerNotificationOperation
            MapNotificationOperation(PeerNotificationOperation operation)
        {
            return (AdminPeerNotificationOperation)(int)operation;
        }

        private static AdminPeerNotificationResult MapNotificationResult(
            PeerNotificationResult result)
        {
            return (AdminPeerNotificationResult)(int)result;
        }

        private static AdminHandlerResult<AdminServerUnitResponse>
            UnitSuccess()
        {
            return AdminHandlerResult<AdminServerUnitResponse>.Success(
                AdminServerUnitResponse.Value);
        }

        private static AdminHandlerResult<AdminServerUnitResponse>
            FailureUnit(AdminServerErrorCode code)
        {
            return AdminHandlerResult<AdminServerUnitResponse>.Failure(code);
        }

        private DateTime GetUtcNow()
        {
            DateTime value = _utcNowProvider().UtcDateTime;
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PeerSynchronizationController));
            }
        }

        private static int NextGeneration(int current)
        {
            return current == int.MaxValue ? 1 : current + 1;
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }

        private static IPeerTlsTrustProvider RequireTlsTrustProvider(
            ICertificateAuthorityPeerSynchronization peerPki)
        {
            if (peerPki == null)
            {
                throw new ArgumentNullException(nameof(peerPki));
            }

            var provider = peerPki as IPeerTlsTrustProvider;
            if (provider == null)
            {
                throw new ArgumentException(
                    "Peer PKI synchronization must also provide pinned TLS trust.",
                    nameof(peerPki));
            }

            return provider;
        }
    }
}
