using System;
using System.Threading;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    public sealed partial class PeerSynchronizationController
    {
        private static readonly TimeSpan PairingCommitLifetime =
            TimeSpan.FromHours(24);

        private bool _pairingCommitWorkerQueued;

        private bool PersistBothConfirmedLocked()
        {
            ServiceDirectoryConfiguration current =
                _configurationState.GetCurrent();
            if (current.Synchronization.State
                    == DurableSynchronizationState.PairedPendingCommit)
            {
                bool samePairing = _pairing != null
                    && _pairing.CurrentPairingId
                        == current.Synchronization.PairingId;
                if (samePairing)
                {
                    QueuePairingCommitAttempt();
                }

                return samePairing;
            }

            if (current.Synchronization.State
                    != DurableSynchronizationState.Unpaired
                || _pairing == null
                || _pairing.State
                    != PairingNegotiationState.BothConfirmed
                || !_pairing.LocalRole.HasValue)
            {
                return false;
            }

            byte[] transcriptHash = null;
            byte[] pairRoot = null;
            using (PairingBothConfirmedMaterial material =
                _pairing.CreateBothConfirmedMaterial())
            {
                try
                {
                    transcriptHash = material.CopyTranscriptHash();
                    pairRoot = material.CopyPairRoot();
                    DateTime commitExpiresUtc = GetUtcNow().Add(
                        PairingCommitLifetime);
                    using (var credential = new PairedPeerCredential(
                        DurablePeerCredentialState.PairedPendingCommit,
                        ToDurablePairingRole(_pairing.LocalRole.Value),
                        material.PairingId,
                        material.LocalInstanceId,
                        material.PeerInstanceId,
                        material.LocalEndpoint,
                        material.PeerEndpoint,
                        material.KeyEpoch,
                        transcriptHash,
                        pairRoot,
                        commitExpiresUtc,
                        false,
                        false,
                        null,
                        null))
                    {
                        var synchronization =
                            SynchronizationConfiguration
                                .PairedPendingCommit(
                                    material.PeerEndpoint,
                                    material.PeerInstanceId,
                                    material.KeyEpoch,
                                    material.PairingId,
                                    commitExpiresUtc,
                                    false,
                                    false,
                                    current.Synchronization
                                        .LastSynchronization,
                                    current.Synchronization
                                        .LastPeerNotification);
                        if (!CommitSynchronizationLocked(
                                material.KeyEpoch,
                                synchronization,
                                credential))
                        {
                            return false;
                        }
                    }
                }
                finally
                {
                    Clear(transcriptHash);
                    Clear(pairRoot);
                }
            }

            QueuePairingCommitAttempt();
            return true;
        }

        private void QueuePairingCommitAttempt()
        {
            lock (_gate)
            {
                if (_disposed
                    || _stopping
                    || _pairingCommitWorkerQueued)
                {
                    return;
                }

                ServiceDirectoryConfiguration configuration =
                    _configurationState.GetCurrent();
                if (configuration.Synchronization.State
                        != DurableSynchronizationState
                            .PairedPendingCommit
                    || configuration.Synchronization
                        .LocalCommitConfirmed == true
                    || !configuration.Synchronization.CommitExpiresUtc
                        .HasValue
                    || GetUtcNow() >= configuration.Synchronization
                        .CommitExpiresUtc.Value)
                {
                    return;
                }

                _pairingCommitWorkerQueued = true;
            }

            if (!TryQueueBackgroundWork(
                    _ => RunPairingCommitWorker(),
                    null))
            {
                lock (_gate)
                {
                    _pairingCommitWorkerQueued = false;
                }
            }
        }

        private void RunPairingCommitWorker()
        {
            try
            {
                SendLocalPairingCommit();
            }
            catch
            {
                // The durable PairedPendingCommit record is retained for the
                // next bounded retry, service restart, or operator action.
            }
            finally
            {
                lock (_gate)
                {
                    _pairingCommitWorkerQueued = false;
                }
            }
        }

        private bool PersistPairingCommitEvidenceLocked(
            bool localEvidence,
            PairingCommitEvidence evidence)
        {
            if (evidence == null)
            {
                throw new ArgumentNullException(nameof(evidence));
            }

            ServiceDirectoryConfiguration current =
                _configurationState.GetCurrent();
            if (current.Synchronization.State
                != DurableSynchronizationState.PairedPendingCommit)
            {
                return false;
            }

            using (PairedPeerCredential currentCredential =
                _configurationState.CopyCredential())
            {
                if (currentCredential == null
                    || currentCredential.State
                        != DurablePeerCredentialState
                            .PairedPendingCommit)
                {
                    return false;
                }

                bool localConfirmed = localEvidence
                    || currentCredential.LocalCommitConfirmed;
                bool remoteConfirmed = !localEvidence
                    || currentCredential.RemoteCommitConfirmed;
                PairingCommitEvidence nextLocalEvidence = localEvidence
                    ? evidence
                    : currentCredential.LocalCommitEvidence;
                PairingCommitEvidence nextRemoteEvidence = localEvidence
                    ? currentCredential.RemoteCommitEvidence
                    : evidence;
                bool completed = localConfirmed && remoteConfirmed;

                byte[] transcriptHash = null;
                byte[] pairRoot = null;
                try
                {
                    transcriptHash = currentCredential
                        .CopyTranscriptHash();
                    pairRoot = currentCredential.CopyPairRoot();
                    using (var nextCredential = new PairedPeerCredential(
                        completed
                            ? DurablePeerCredentialState.PairedDisabled
                            : DurablePeerCredentialState
                                .PairedPendingCommit,
                        currentCredential.LocalRole,
                        currentCredential.PairingId,
                        currentCredential.LocalInstanceId,
                        currentCredential.PeerInstanceId,
                        currentCredential.LocalEndpoint,
                        currentCredential.PeerEndpoint,
                        currentCredential.KeyEpoch,
                        transcriptHash,
                        pairRoot,
                        currentCredential.CommitExpiresUtc,
                        localConfirmed,
                        remoteConfirmed,
                        nextLocalEvidence,
                        nextRemoteEvidence))
                    {
                        SynchronizationConfiguration synchronization =
                            completed
                                ? SynchronizationConfiguration
                                    .PairedDisabled(
                                        currentCredential.PeerEndpoint,
                                        currentCredential.PeerInstanceId,
                                        currentCredential.KeyEpoch,
                                        current.Synchronization
                                            .LastSynchronization,
                                        current.Synchronization
                                            .LastPeerNotification)
                                : SynchronizationConfiguration
                                    .PairedPendingCommit(
                                        currentCredential.PeerEndpoint,
                                        currentCredential.PeerInstanceId,
                                        currentCredential.KeyEpoch,
                                        currentCredential.PairingId,
                                        currentCredential.CommitExpiresUtc,
                                        localConfirmed,
                                        remoteConfirmed,
                                        current.Synchronization
                                            .LastSynchronization,
                                        current.Synchronization
                                            .LastPeerNotification);
                        if (!CommitSynchronizationLocked(
                                current.LastPeerKeyEpoch,
                                synchronization,
                                nextCredential))
                        {
                            return false;
                        }
                    }
                }
                finally
                {
                    Clear(transcriptHash);
                    Clear(pairRoot);
                }

                if (completed)
                {
                    DisposeTransientPairingLocked();
                }

                return true;
            }
        }

        private static PairingRole ToDurablePairingRole(
            PeerPairingRole role)
        {
            switch (role)
            {
                case PeerPairingRole.Initiator:
                    return PairingRole.Initiator;
                case PeerPairingRole.Responder:
                    return PairingRole.Responder;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role));
            }
        }

        private static PeerPairingRole ToWirePairingRole(
            PairingRole role)
        {
            switch (role)
            {
                case PairingRole.Initiator:
                    return PeerPairingRole.Initiator;
                case PairingRole.Responder:
                    return PeerPairingRole.Responder;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role));
            }
        }

        private static PairingConfirmationDirection
            ToPairingConfirmationDirection(PairingRole role)
        {
            switch (role)
            {
                case PairingRole.Initiator:
                    return PairingConfirmationDirection.Initiator;
                case PairingRole.Responder:
                    return PairingConfirmationDirection.Responder;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role));
            }
        }

        private static PairingRole OppositePairingRole(PairingRole role)
        {
            switch (role)
            {
                case PairingRole.Initiator:
                    return PairingRole.Responder;
                case PairingRole.Responder:
                    return PairingRole.Initiator;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role));
            }
        }
    }
}
