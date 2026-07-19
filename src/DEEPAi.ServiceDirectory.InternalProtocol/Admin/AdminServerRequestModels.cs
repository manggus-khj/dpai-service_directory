using System;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Admin
{
    public sealed class AdminEnableSyncRequest
    {
        internal AdminEnableSyncRequest(
            string peerEndpoint,
            bool rePair)
        {
            string canonicalEndpoint;
            if (!AdminPeerEndpoint.TryNormalize(
                    peerEndpoint,
                    out canonicalEndpoint)
                || !StringComparer.Ordinal.Equals(
                    peerEndpoint,
                    canonicalEndpoint))
            {
                throw new ArgumentException(
                    "Peer endpoint must use its canonical form.",
                    nameof(peerEndpoint));
            }

            PeerEndpoint = canonicalEndpoint;
            RePair = rePair;
        }

        public string PeerEndpoint { get; }

        public bool RePair { get; }
    }

    public sealed class AdminPairingConfirmationRequest
    {
        internal AdminPairingConfirmationRequest(
            Guid pairingId,
            bool confirmed)
        {
            if (pairingId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Pairing ID cannot be empty.",
                    nameof(pairingId));
            }

            PairingId = pairingId;
            Confirmed = confirmed;
        }

        public Guid PairingId { get; }

        public bool Confirmed { get; }
    }

    public sealed class AdminPairingCancellationRequest
    {
        internal AdminPairingCancellationRequest(Guid pairingId)
        {
            if (pairingId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Pairing ID cannot be empty.",
                    nameof(pairingId));
            }

            PairingId = pairingId;
        }

        public Guid PairingId { get; }
    }

    public sealed class AdminDisableSyncRequest
    {
        internal AdminDisableSyncRequest(bool forgetPeer)
        {
            ForgetPeer = forgetPeer;
        }

        public bool ForgetPeer { get; }
    }

    public sealed class AdminLoggingSettingsRequest
    {
        internal AdminLoggingSettingsRequest(int logRetentionDays)
        {
            if (logRetentionDays < AdminApiContract.MinimumLogRetentionDays
                || logRetentionDays >
                    AdminApiContract.MaximumLogRetentionDays)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(logRetentionDays));
            }

            LogRetentionDays = logRetentionDays;
        }

        public int LogRetentionDays { get; }
    }
}
