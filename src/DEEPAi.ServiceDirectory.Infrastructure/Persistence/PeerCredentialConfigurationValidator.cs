using System;
using System.IO;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal static class PeerCredentialConfigurationValidator
    {
        internal static void Validate(
            ServiceDirectoryConfiguration configuration,
            PairedPeerCredential credential)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            SynchronizationConfiguration synchronization =
                configuration.Synchronization;
            if (synchronization.State
                == DurableSynchronizationState.Unpaired)
            {
                if (credential != null)
                {
                    throw new InvalidDataException(
                        "Unpaired config.xml must not have a peer credential.");
                }

                return;
            }

            if (credential == null)
            {
                throw new InvalidDataException(
                    "A paired config.xml requires a protected peer credential.");
            }

            DurablePeerCredentialState expectedState = MapState(
                synchronization.State);
            if (credential.State != expectedState)
            {
                throw new InvalidDataException(
                    "config.xml and peer.dat pairing states do not match.");
            }

            if (credential.LocalInstanceId != configuration.InstanceId
                || credential.PeerInstanceId
                    != synchronization.PeerInstanceId.Value
                || credential.KeyEpoch != synchronization.KeyEpoch.Value
                || credential.KeyEpoch != configuration.LastPeerKeyEpoch)
            {
                throw new InvalidDataException(
                    "config.xml and peer.dat identity or epoch bindings do not match.");
            }

            string expectedLocalEndpoint = GetLocalEndpoint(configuration);
            if (!StringComparer.Ordinal.Equals(
                    credential.LocalEndpoint,
                    expectedLocalEndpoint)
                || !StringComparer.Ordinal.Equals(
                    credential.PeerEndpoint,
                    synchronization.PeerEndpoint))
            {
                throw new InvalidDataException(
                    "config.xml and peer.dat endpoint bindings do not match.");
            }

            if (synchronization.State
                == DurableSynchronizationState.PairedPendingCommit)
            {
                if (credential.PairingId != synchronization.PairingId.Value
                    || credential.CommitExpiresUtc
                        != synchronization.CommitExpiresUtc.Value
                    || credential.LocalCommitConfirmed
                        != synchronization.LocalCommitConfirmed.Value
                    || credential.RemoteCommitConfirmed
                        != synchronization.RemoteCommitConfirmed.Value)
                {
                    throw new InvalidDataException(
                        "config.xml and peer.dat pending commit state does not match.");
                }
            }
            else if (!credential.LocalCommitConfirmed
                || !credential.RemoteCommitConfirmed)
            {
                throw new InvalidDataException(
                    "A completed paired state requires both durable commit confirmations.");
            }
        }

        private static DurablePeerCredentialState MapState(
            DurableSynchronizationState state)
        {
            switch (state)
            {
                case DurableSynchronizationState.PairedPendingCommit:
                    return DurablePeerCredentialState.PairedPendingCommit;
                case DurableSynchronizationState.PairedDisabled:
                    return DurablePeerCredentialState.PairedDisabled;
                case DurableSynchronizationState.Enabled:
                    return DurablePeerCredentialState.Enabled;
                default:
                    throw new InvalidDataException(
                        "The configuration does not contain a durable peer binding.");
            }
        }

        private static string GetLocalEndpoint(
            ServiceDirectoryConfiguration configuration)
        {
            return "https://"
                + configuration.DirectoryIpv4Address
                + ":21000";
        }
    }
}
