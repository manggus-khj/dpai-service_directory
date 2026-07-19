using System;
using System.Threading;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public sealed partial class PeerSynchronizationController
    {
        private bool _started;
        private bool _synchronizationRequestedWhileRunning;
        private bool _outboundSynchronizationSuperseded;
        private string _pendingSynchronizationTrigger;

        private sealed class SynchronizationWorkItem
        {
            internal SynchronizationWorkItem(
                string trigger,
                bool initial)
            {
                Trigger = trigger;
                Initial = initial;
            }

            internal string Trigger { get; }

            internal bool Initial { get; }
        }

        public void Start()
        {
            bool queueInitialSynchronization;
            bool queuePairingCommit;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_started)
                {
                    return;
                }

                if (_stopping || _stopped)
                {
                    throw new InvalidOperationException(
                        "Peer synchronization has already been stopped.");
                }

                _started = true;
                ServiceDirectoryConfiguration configuration =
                    _configurationState.GetCurrent();
                queueInitialSynchronization =
                    configuration.Synchronization.State
                        == DurableSynchronizationState.Enabled;
                _initialSynchronizationPending =
                    queueInitialSynchronization;
                queuePairingCommit =
                    configuration.Synchronization.State
                        == DurableSynchronizationState.PairedPendingCommit;
                _periodicTimer.Change(
                    SynchronizationInterval,
                    SynchronizationInterval);
            }

            if (queuePairingCommit)
            {
                QueuePairingCommitAttempt();
            }

            if (queueInitialSynchronization)
            {
                QueueSynchronization("SERVICE_START", true);
            }
        }

        private void OnPeriodicTimer(object state)
        {
            try
            {
                DurableSynchronizationState synchronizationState;
                lock (_gate)
                {
                    if (_disposed || _stopping || !_started)
                    {
                        return;
                    }

                    synchronizationState = _configurationState.GetCurrent()
                        .Synchronization.State;
                }

                if (synchronizationState
                    == DurableSynchronizationState.PairedPendingCommit)
                {
                    QueuePairingCommitAttempt();
                    return;
                }

                if (synchronizationState
                    == DurableSynchronizationState.Enabled)
                {
                    QueueSynchronization("PERIODIC", false);
                }
            }
            catch (Exception exception)
            {
                RecordFatalBackgroundFailure(exception);
            }
        }

        private void QueueSynchronization(string trigger, bool initial)
        {
            bool queue;
            lock (_gate)
            {
                if (_disposed || _stopping || !_started)
                {
                    return;
                }

                bool enabled = _configurationState.GetCurrent()
                    .Synchronization.State
                    == DurableSynchronizationState.Enabled;
                queue = enabled && !_syncRunning;
                if (queue)
                {
                    _syncRunning = true;
                    _outboundSynchronizationSuperseded = false;
                }
                else if (enabled && _syncRunning)
                {
                    // Collapse concurrent automatic triggers into one
                    // follow-up cycle so a directory mutation that lands
                    // during an exchange is not delayed until the next
                    // ten-minute timer tick.
                    _synchronizationRequestedWhileRunning = true;
                    _pendingSynchronizationTrigger = trigger;
                    if (initial)
                    {
                        _initialSynchronizationPending = true;
                    }
                }
            }

            if (queue)
            {
                QueueSynchronizationWorker(trigger, initial);
            }
        }

        private void QueueSynchronizationWorker(
            string trigger,
            bool initial)
        {
            if (!TryQueueBackgroundWork(
                    RunSynchronizationWorker,
                    new SynchronizationWorkItem(trigger, initial)))
            {
                CompleteSynchronizationWorker();
            }
        }

        private void RunSynchronizationWorker(object state)
        {
            var workItem = state as SynchronizationWorkItem;
            if (workItem == null)
            {
                CompleteSynchronizationWorker();
                return;
            }

            PairedPeerCredential credential = null;
            SyncCycleOutcome outcome = SyncCycleOutcome.Failure(
                PeerSyncResponseCode.Internal,
                null);
            bool initial = workItem.Initial;
            bool initialPendingConsumed = false;
            bool shouldPersistOutcome = false;
            int retentionDays =
                ServiceDirectoryConfiguration.DefaultLogRetentionDays;
            try
            {
                lock (_gate)
                {
                    if (_disposed
                        || !_started
                        || _outboundSynchronizationSuperseded)
                    {
                        return;
                    }

                    ServiceDirectoryConfiguration configuration =
                        _configurationState.GetCurrent();
                    if (configuration.Synchronization.State
                        != DurableSynchronizationState.Enabled)
                    {
                        return;
                    }

                    credential = _configurationState.CopyCredential();
                    if (credential == null
                        || credential.State
                            != DurablePeerCredentialState.Enabled
                        || !HasCurrentPeerBindingLocked(
                            credential,
                            DurableSynchronizationState.Enabled))
                    {
                        return;
                    }

                    if (_initialSynchronizationPending)
                    {
                        initial = true;
                        _initialSynchronizationPending = false;
                        initialPendingConsumed = true;
                    }

                    retentionDays = configuration.LogRetentionDays;
                    shouldPersistOutcome = true;
                }

                WriteSynchronizationStartEvent(
                    () =>
                    {
                        if (initial)
                        {
                            _systemLog.WriteSyncInitialStarted(
                                credential.PeerInstanceId,
                                workItem.Trigger,
                                retentionDays);
                            return;
                        }

                        _systemLog.WriteSyncStarted(
                            credential.PeerInstanceId,
                            workItem.Trigger,
                            retentionDays);
                    },
                    initialPendingConsumed
                        ? (Action)RestoreInitialSynchronizationPending
                        : null);

                outcome = PerformSynchronizationCycle(credential);
            }
            catch
            {
                outcome = SyncCycleOutcome.Failure(
                    PeerSyncResponseCode.Internal,
                    outcome.ClockSkewSeconds);
            }
            finally
            {
                try
                {
                    if (credential != null)
                    {
                        try
                        {
                            if (shouldPersistOutcome)
                            {
                                bool persisted =
                                    PersistSynchronizationOutcome(
                                        credential,
                                        outcome);
                                if (persisted && outcome.IsSuccess)
                                {
                                    WriteSynchronizationSucceededAfterPersist(
                                        credential,
                                        workItem.Trigger,
                                        retentionDays,
                                        outcome.ClockSkewSeconds);
                                }
                            }
                        }
                        finally
                        {
                            credential.Dispose();
                        }
                    }
                }
                finally
                {
                    CompleteSynchronizationWorker();
                }
            }
        }

        private bool PersistSynchronizationOutcome(
            PairedPeerCredential credential,
            SyncCycleOutcome outcome)
        {
            lock (_gate)
            {
                if (_disposed
                    || _outboundSynchronizationSuperseded
                    || !HasCurrentPeerBindingLocked(
                        credential,
                        DurableSynchronizationState.Enabled))
                {
                    return false;
                }

                ServiceDirectoryConfiguration configuration =
                    _configurationState.GetCurrent();
                var lastSynchronization = new LastSynchronizationStatus(
                    outcome.ResultCode,
                    GetUtcNow(),
                    outcome.ClockSkewSeconds);
                SynchronizationConfiguration synchronization =
                    SynchronizationConfiguration.Enabled(
                        credential.PeerEndpoint,
                        credential.PeerInstanceId,
                        credential.KeyEpoch,
                        lastSynchronization,
                        configuration.Synchronization
                            .LastPeerNotification);
                using (PairedPeerCredential enabled =
                    ChangeCredentialState(
                        credential,
                        DurablePeerCredentialState.Enabled))
                {
                    return CommitSynchronizationLocked(
                        configuration.LastPeerKeyEpoch,
                        synchronization,
                        enabled);
                }
            }
        }

        private void WriteSynchronizationSucceededAfterPersist(
            PairedPeerCredential credential,
            string trigger,
            int retentionDays,
            long? clockSkewSeconds)
        {
            try
            {
                _systemLog.WriteSyncSucceeded(
                    credential.PeerInstanceId,
                    trigger,
                    retentionDays);
            }
            catch (SystemLogRetentionAfterWriteException)
            {
                // The success event itself is durable; only best-effort
                // retention cleanup failed after the write.
            }
            catch (Exception exception) when (
                exception is System.IO.IOException
                || exception is UnauthorizedAccessException
                || exception is System.Security.SecurityException)
            {
                // The exchange committed, but the required success event did
                // not. Surface that operational failure through LastResult
                // without allowing an unhandled ThreadPool exception.
                PersistSynchronizationOutcome(
                    credential,
                    SyncCycleOutcome.Failure(
                        PeerSyncResponseCode.Internal,
                        clockSkewSeconds));
            }
        }

        internal static void WriteSynchronizationStartEvent(
            Action writeEvent,
            Action restoreInitialPending)
        {
            if (writeEvent == null)
            {
                throw new ArgumentNullException(nameof(writeEvent));
            }

            try
            {
                writeEvent();
            }
            catch (SystemLogRetentionAfterWriteException)
            {
                // The event itself is durable. Only retention cleanup failed,
                // so consuming the one-time initial marker remains valid.
            }
            catch
            {
                restoreInitialPending?.Invoke();
                throw;
            }
        }

        private void RestoreInitialSynchronizationPending()
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _initialSynchronizationPending = true;
                }
            }
        }

        private void CompleteSynchronizationWorker()
        {
            bool queueFollowUp = false;
            string followUpTrigger = null;
            lock (_gate)
            {
                _syncRunning = false;
                _outboundSynchronizationSuperseded = false;
                if (!_disposed
                    && !_stopping
                    && _started
                    && _synchronizationRequestedWhileRunning
                    && _configurationState.GetCurrent()
                        .Synchronization.State
                        == DurableSynchronizationState.Enabled)
                {
                    queueFollowUp = true;
                    followUpTrigger = string.IsNullOrEmpty(
                        _pendingSynchronizationTrigger)
                        ? "COALESCED_CHANGE"
                        : _pendingSynchronizationTrigger;
                    _synchronizationRequestedWhileRunning = false;
                    _pendingSynchronizationTrigger = null;
                    _syncRunning = true;
                    _outboundSynchronizationSuperseded = false;
                }
                else
                {
                    _synchronizationRequestedWhileRunning = false;
                    _pendingSynchronizationTrigger = null;
                }
            }

            if (queueFollowUp)
            {
                QueueSynchronizationWorker(
                    followUpTrigger,
                    false);
            }
        }
    }
}
