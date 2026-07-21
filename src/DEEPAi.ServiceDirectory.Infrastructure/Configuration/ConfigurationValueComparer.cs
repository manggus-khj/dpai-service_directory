using System;

namespace DEEPAi.ServiceDirectory.Infrastructure.Configuration
{
    internal static class ConfigurationValueComparer
    {
        internal static bool Equals(
            ServiceDirectoryConfiguration left,
            ServiceDirectoryConfiguration right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            return StringComparer.Ordinal.Equals(
                    left.ListenAddress,
                    right.ListenAddress)
                && left.DirectoryEndpointIdentity.Equals(
                    right.DirectoryEndpointIdentity)
                && left.InstanceId == right.InstanceId
                && left.LastPeerKeyEpoch == right.LastPeerKeyEpoch
                && left.LogRetentionDays == right.LogRetentionDays
                && SynchronizationEquals(
                    left.Synchronization,
                    right.Synchronization);
        }

        internal static bool EqualsExceptDirectoryIdentity(
            ServiceDirectoryConfiguration left,
            ServiceDirectoryConfiguration right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            return left.InstanceId == right.InstanceId
                && left.LastPeerKeyEpoch == right.LastPeerKeyEpoch
                && left.LogRetentionDays == right.LogRetentionDays
                && SynchronizationEquals(
                    left.Synchronization,
                    right.Synchronization);
        }

        private static bool SynchronizationEquals(
            SynchronizationConfiguration left,
            SynchronizationConfiguration right)
        {
            return ReferenceEquals(left, right)
                || (left != null
                    && right != null
                    && left.State == right.State
                    && StringComparer.Ordinal.Equals(
                        left.PeerEndpoint,
                        right.PeerEndpoint)
                    && left.PeerInstanceId == right.PeerInstanceId
                    && left.KeyEpoch == right.KeyEpoch
                    && left.PairingId == right.PairingId
                    && left.CommitExpiresUtc == right.CommitExpiresUtc
                    && left.LocalCommitConfirmed == right.LocalCommitConfirmed
                    && left.RemoteCommitConfirmed == right.RemoteCommitConfirmed
                    && LastSynchronizationEquals(
                        left.LastSynchronization,
                        right.LastSynchronization)
                    && PeerNotificationEquals(
                        left.LastPeerNotification,
                        right.LastPeerNotification));
        }

        private static bool LastSynchronizationEquals(
            LastSynchronizationStatus left,
            LastSynchronizationStatus right)
        {
            return ReferenceEquals(left, right)
                || (left != null
                    && right != null
                    && StringComparer.Ordinal.Equals(
                        left.Result,
                        right.Result)
                    && left.LastSyncUtc == right.LastSyncUtc
                    && left.ClockSkewSeconds == right.ClockSkewSeconds);
        }

        private static bool PeerNotificationEquals(
            PeerNotificationStatus left,
            PeerNotificationStatus right)
        {
            return ReferenceEquals(left, right)
                || (left != null
                    && right != null
                    && left.Operation == right.Operation
                    && left.Result == right.Result
                    && left.NotificationUtc == right.NotificationUtc);
        }
    }
}
