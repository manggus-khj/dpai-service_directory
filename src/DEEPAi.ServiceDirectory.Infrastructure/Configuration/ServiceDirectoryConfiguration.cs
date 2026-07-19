using System;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;

namespace DEEPAi.ServiceDirectory.Infrastructure.Configuration
{
    public enum DurableSynchronizationState
    {
        Unpaired = 1,
        PairedPendingCommit = 2,
        PairedDisabled = 3,
        Enabled = 4
    }

    public enum PeerNotificationOperation
    {
        None = 1,
        Release = 2,
        Revoke = 3
    }

    public enum PeerNotificationResult
    {
        NotRun = 1,
        Confirmed = 2,
        Unconfirmed = 3,
        NotRequired = 4
    }

    public sealed class LastSynchronizationStatus
    {
        public const string NotRunResult = "NOT_RUN";

        public LastSynchronizationStatus(
            string result,
            DateTime? lastSyncUtc,
            long? clockSkewSeconds)
        {
            if (!IsSupportedResult(result))
            {
                throw new ArgumentException(
                    "The last synchronization result is not a supported result code.",
                    nameof(result));
            }

            if (lastSyncUtc.HasValue
                && lastSyncUtc.Value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "The last synchronization time must be UTC.",
                    nameof(lastSyncUtc));
            }

            bool notRun = StringComparer.Ordinal.Equals(
                result,
                NotRunResult);
            if (notRun != !lastSyncUtc.HasValue
                || (notRun && clockSkewSeconds.HasValue))
            {
                throw new ArgumentException(
                    "The last synchronization result, time, and clock skew are inconsistent.",
                    nameof(result));
            }

            Result = result;
            LastSyncUtc = lastSyncUtc;
            ClockSkewSeconds = clockSkewSeconds;
        }

        public string Result { get; }

        public DateTime? LastSyncUtc { get; }

        public long? ClockSkewSeconds { get; }

        public static LastSynchronizationStatus NotRun()
        {
            return new LastSynchronizationStatus(
                NotRunResult,
                null,
                null);
        }

        private static bool IsSupportedResult(string value)
        {
            switch (value)
            {
                case "NOT_RUN":
                case "OK":
                case "BAD_REQUEST":
                case "NOT_FOUND":
                case "CONFLICT":
                case "LIMIT_EXCEEDED":
                case "NOT_PEER":
                case "PEER_MISMATCH":
                case "CLOCK_SKEW":
                case "SYNC_DISABLED":
                case "REVISION_COLLISION":
                case "DIRECTORY_CAPACITY":
                case "LOGICAL_CLOCK_EXHAUSTED":
                case "INTERNAL":
                    return true;
                default:
                    return false;
            }
        }
    }

    public sealed class PeerNotificationStatus
    {
        public PeerNotificationStatus(
            PeerNotificationOperation operation,
            PeerNotificationResult result,
            DateTime? notificationUtc)
        {
            if (!Enum.IsDefined(typeof(PeerNotificationOperation), operation))
            {
                throw new ArgumentOutOfRangeException(nameof(operation));
            }

            if (!Enum.IsDefined(typeof(PeerNotificationResult), result))
            {
                throw new ArgumentOutOfRangeException(nameof(result));
            }

            if (notificationUtc.HasValue
                && notificationUtc.Value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "The peer notification time must be UTC.",
                    nameof(notificationUtc));
            }

            bool initial = operation == PeerNotificationOperation.None
                && result == PeerNotificationResult.NotRun;
            if (initial != !notificationUtc.HasValue
                || ((operation == PeerNotificationOperation.None)
                    != (result == PeerNotificationResult.NotRun)))
            {
                throw new ArgumentException(
                    "The peer notification operation, result, and time are inconsistent.",
                    nameof(result));
            }

            Operation = operation;
            Result = result;
            NotificationUtc = notificationUtc;
        }

        public PeerNotificationOperation Operation { get; }

        public PeerNotificationResult Result { get; }

        public DateTime? NotificationUtc { get; }

        public static PeerNotificationStatus NotRun()
        {
            return new PeerNotificationStatus(
                PeerNotificationOperation.None,
                PeerNotificationResult.NotRun,
                null);
        }
    }

    public sealed class SynchronizationConfiguration
    {
        private SynchronizationConfiguration(
            DurableSynchronizationState state,
            string peerEndpoint,
            Guid? peerInstanceId,
            ulong? keyEpoch,
            Guid? pairingId,
            DateTime? commitExpiresUtc,
            bool? localCommitConfirmed,
            bool? remoteCommitConfirmed,
            LastSynchronizationStatus lastSynchronization,
            PeerNotificationStatus lastPeerNotification)
        {
            if (!Enum.IsDefined(typeof(DurableSynchronizationState), state))
            {
                throw new ArgumentOutOfRangeException(nameof(state));
            }

            if (lastSynchronization == null)
            {
                throw new ArgumentNullException(nameof(lastSynchronization));
            }

            if (lastPeerNotification == null)
            {
                throw new ArgumentNullException(nameof(lastPeerNotification));
            }

            string canonicalPeerEndpoint = null;
            if (peerEndpoint != null
                && (!ConfigurationAddress.TryNormalizePeerEndpoint(
                        peerEndpoint,
                        out canonicalPeerEndpoint)
                    || !StringComparer.Ordinal.Equals(
                        peerEndpoint,
                        canonicalPeerEndpoint)))
            {
                throw new ArgumentException(
                    "The peer endpoint must use the canonical HTTP IP literal form on port 21000.",
                    nameof(peerEndpoint));
            }

            if (peerInstanceId == Guid.Empty || pairingId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Peer and pairing identifiers must be non-empty when present.");
            }

            if (commitExpiresUtc.HasValue
                && commitExpiresUtc.Value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "The pairing commit expiry must be UTC.",
                    nameof(commitExpiresUtc));
            }

            ValidateStateShape(
                state,
                canonicalPeerEndpoint,
                peerInstanceId,
                keyEpoch,
                pairingId,
                commitExpiresUtc,
                localCommitConfirmed,
                remoteCommitConfirmed);

            State = state;
            PeerEndpoint = canonicalPeerEndpoint;
            PeerInstanceId = peerInstanceId;
            KeyEpoch = keyEpoch;
            PairingId = pairingId;
            CommitExpiresUtc = commitExpiresUtc;
            LocalCommitConfirmed = localCommitConfirmed;
            RemoteCommitConfirmed = remoteCommitConfirmed;
            LastSynchronization = lastSynchronization;
            LastPeerNotification = lastPeerNotification;
        }

        public DurableSynchronizationState State { get; }

        public string PeerEndpoint { get; }

        public Guid? PeerInstanceId { get; }

        public ulong? KeyEpoch { get; }

        public Guid? PairingId { get; }

        public DateTime? CommitExpiresUtc { get; }

        public bool? LocalCommitConfirmed { get; }

        public bool? RemoteCommitConfirmed { get; }

        public LastSynchronizationStatus LastSynchronization { get; }

        public PeerNotificationStatus LastPeerNotification { get; }

        public static SynchronizationConfiguration Unpaired(
            LastSynchronizationStatus lastSynchronization,
            PeerNotificationStatus lastPeerNotification)
        {
            return new SynchronizationConfiguration(
                DurableSynchronizationState.Unpaired,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                lastSynchronization,
                lastPeerNotification);
        }

        public static SynchronizationConfiguration PairedPendingCommit(
            string peerEndpoint,
            Guid peerInstanceId,
            ulong keyEpoch,
            Guid pairingId,
            DateTime commitExpiresUtc,
            bool localCommitConfirmed,
            bool remoteCommitConfirmed,
            LastSynchronizationStatus lastSynchronization,
            PeerNotificationStatus lastPeerNotification)
        {
            return new SynchronizationConfiguration(
                DurableSynchronizationState.PairedPendingCommit,
                peerEndpoint,
                peerInstanceId,
                keyEpoch,
                pairingId,
                commitExpiresUtc,
                localCommitConfirmed,
                remoteCommitConfirmed,
                lastSynchronization,
                lastPeerNotification);
        }

        public static SynchronizationConfiguration PairedDisabled(
            string peerEndpoint,
            Guid peerInstanceId,
            ulong keyEpoch,
            LastSynchronizationStatus lastSynchronization,
            PeerNotificationStatus lastPeerNotification)
        {
            return CreatePaired(
                DurableSynchronizationState.PairedDisabled,
                peerEndpoint,
                peerInstanceId,
                keyEpoch,
                lastSynchronization,
                lastPeerNotification);
        }

        public static SynchronizationConfiguration Enabled(
            string peerEndpoint,
            Guid peerInstanceId,
            ulong keyEpoch,
            LastSynchronizationStatus lastSynchronization,
            PeerNotificationStatus lastPeerNotification)
        {
            return CreatePaired(
                DurableSynchronizationState.Enabled,
                peerEndpoint,
                peerInstanceId,
                keyEpoch,
                lastSynchronization,
                lastPeerNotification);
        }

        private static SynchronizationConfiguration CreatePaired(
            DurableSynchronizationState state,
            string peerEndpoint,
            Guid peerInstanceId,
            ulong keyEpoch,
            LastSynchronizationStatus lastSynchronization,
            PeerNotificationStatus lastPeerNotification)
        {
            return new SynchronizationConfiguration(
                state,
                peerEndpoint,
                peerInstanceId,
                keyEpoch,
                null,
                null,
                null,
                null,
                lastSynchronization,
                lastPeerNotification);
        }

        private static void ValidateStateShape(
            DurableSynchronizationState state,
            string peerEndpoint,
            Guid? peerInstanceId,
            ulong? keyEpoch,
            Guid? pairingId,
            DateTime? commitExpiresUtc,
            bool? localCommitConfirmed,
            bool? remoteCommitConfirmed)
        {
            bool hasPeer = peerEndpoint != null
                && peerInstanceId.HasValue
                && keyEpoch.HasValue
                && keyEpoch.Value > 0;
            bool hasNoPeer = peerEndpoint == null
                && !peerInstanceId.HasValue
                && !keyEpoch.HasValue;
            bool hasCommit = pairingId.HasValue
                && commitExpiresUtc.HasValue
                && localCommitConfirmed.HasValue
                && remoteCommitConfirmed.HasValue;
            bool hasNoCommit = !pairingId.HasValue
                && !commitExpiresUtc.HasValue
                && !localCommitConfirmed.HasValue
                && !remoteCommitConfirmed.HasValue;

            bool valid;
            switch (state)
            {
                case DurableSynchronizationState.Unpaired:
                    valid = hasNoPeer && hasNoCommit;
                    break;
                case DurableSynchronizationState.PairedPendingCommit:
                    valid = hasPeer && hasCommit;
                    break;
                case DurableSynchronizationState.PairedDisabled:
                case DurableSynchronizationState.Enabled:
                    valid = hasPeer && hasNoCommit;
                    break;
                default:
                    valid = false;
                    break;
            }

            if (!valid)
            {
                throw new ArgumentException(
                    "The synchronization fields do not match the durable state.",
                    nameof(state));
            }
        }
    }

    public sealed class ServiceDirectoryConfiguration
    {
        public const int MinimumLogRetentionDays =
            SystemFileLogger.MinimumRetentionDays;
        public const int DefaultLogRetentionDays =
            SystemFileLogger.DefaultRetentionDays;
        public const int MaximumLogRetentionDays =
            SystemFileLogger.MaximumRetentionDays;

        public ServiceDirectoryConfiguration(
            string listenAddress,
            Guid instanceId,
            ulong lastPeerKeyEpoch,
            int logRetentionDays,
            SynchronizationConfiguration synchronization)
        {
            ServiceDirectoryListenerAddress normalizedAddress;
            if (!ServiceDirectoryListenerAddress.TryCreate(
                    listenAddress,
                    out normalizedAddress)
                || !StringComparer.Ordinal.Equals(
                    listenAddress,
                    normalizedAddress.CanonicalAddress))
            {
                throw new ArgumentException(
                    "ListenAddress must be a canonical supported IP literal.",
                    nameof(listenAddress));
            }

            if (instanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The installation instance identifier must be non-empty.",
                    nameof(instanceId));
            }

            if (logRetentionDays < MinimumLogRetentionDays
                || logRetentionDays > MaximumLogRetentionDays)
            {
                throw new ArgumentOutOfRangeException(nameof(logRetentionDays));
            }

            if (synchronization == null)
            {
                throw new ArgumentNullException(nameof(synchronization));
            }

            if (synchronization.KeyEpoch.HasValue
                && synchronization.KeyEpoch.Value != lastPeerKeyEpoch)
            {
                throw new ArgumentException(
                    "A durable peer key epoch must equal LastPeerKeyEpoch.",
                    nameof(lastPeerKeyEpoch));
            }

            ListenAddress = listenAddress;
            InstanceId = instanceId;
            LastPeerKeyEpoch = lastPeerKeyEpoch;
            LogRetentionDays = logRetentionDays;
            Synchronization = synchronization;
        }

        public string ListenAddress { get; }

        public Guid InstanceId { get; }

        public ulong LastPeerKeyEpoch { get; }

        public int LogRetentionDays { get; }

        public SynchronizationConfiguration Synchronization { get; }

        public static ServiceDirectoryConfiguration CreateInitial(
            string listenAddress,
            Guid instanceId)
        {
            return new ServiceDirectoryConfiguration(
                listenAddress,
                instanceId,
                0UL,
                DefaultLogRetentionDays,
                SynchronizationConfiguration.Unpaired(
                    LastSynchronizationStatus.NotRun(),
                    PeerNotificationStatus.NotRun()));
        }

        public ServiceDirectoryConfiguration WithLogRetentionDays(
            int logRetentionDays)
        {
            return new ServiceDirectoryConfiguration(
                ListenAddress,
                InstanceId,
                LastPeerKeyEpoch,
                logRetentionDays,
                Synchronization);
        }

        public ServiceDirectoryConfiguration WithSynchronization(
            ulong lastPeerKeyEpoch,
            SynchronizationConfiguration synchronization)
        {
            if (lastPeerKeyEpoch < LastPeerKeyEpoch)
            {
                throw new ArgumentException(
                    "LastPeerKeyEpoch must not decrease.",
                    nameof(lastPeerKeyEpoch));
            }

            return new ServiceDirectoryConfiguration(
                ListenAddress,
                InstanceId,
                lastPeerKeyEpoch,
                LogRetentionDays,
                synchronization);
        }

        internal ServiceDirectoryConfiguration WithListenAddressForRepair(
            string listenAddress)
        {
            return new ServiceDirectoryConfiguration(
                listenAddress,
                InstanceId,
                LastPeerKeyEpoch,
                LogRetentionDays,
                Synchronization);
        }
    }

    internal static class ConfigurationAddress
    {
        internal static bool TryNormalizePeerEndpoint(
            string value,
            out string canonicalEndpoint)
        {
            canonicalEndpoint = null;
            if (string.IsNullOrEmpty(value)
                || value.Length > 80
                || !value.StartsWith("http://", StringComparison.Ordinal))
            {
                return false;
            }

            const string portSuffix = ":21000";
            string authority = value.Substring("http://".Length);
            string addressLiteral;
            if (authority.StartsWith("[", StringComparison.Ordinal))
            {
                int closingBracket = authority.IndexOf(']');
                if (closingBracket <= 1
                    || !StringComparer.Ordinal.Equals(
                        authority.Substring(closingBracket + 1),
                        portSuffix))
                {
                    return false;
                }

                addressLiteral = authority.Substring(
                    1,
                    closingBracket - 1);
            }
            else
            {
                int portSeparator = authority.LastIndexOf(':');
                if (portSeparator <= 0
                    || authority.IndexOf(':') != portSeparator
                    || !StringComparer.Ordinal.Equals(
                        authority.Substring(portSeparator),
                        portSuffix))
                {
                    return false;
                }

                addressLiteral = authority.Substring(0, portSeparator);
            }

            ServiceDirectoryListenerAddress address;
            if (!ServiceDirectoryListenerAddress.TryCreate(
                    addressLiteral,
                    out address))
            {
                return false;
            }

            canonicalEndpoint = address.HttpPrefix.Substring(
                0,
                address.HttpPrefix.Length - 1);
            return StringComparer.Ordinal.Equals(value, canonicalEndpoint);
        }
    }
}
