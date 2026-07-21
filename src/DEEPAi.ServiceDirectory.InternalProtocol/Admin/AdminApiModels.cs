using System;
using System.Collections.Generic;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Admin
{
    public sealed class AdminResponse<T>
    {
        internal AdminResponse(string result, int code, string message, T payload)
        {
            if (string.IsNullOrWhiteSpace(result))
            {
                throw new ArgumentException("Result is required.", nameof(result));
            }

            Result = result;
            Code = code;
            Message = message ?? string.Empty;
            Payload = payload;
        }

        public string Result { get; }

        public int Code { get; }

        public string Message { get; }

        public T Payload { get; }

        public bool IsSuccess => Code == 0
            && StringComparer.Ordinal.Equals(Result, "OK");
    }

    public sealed class AdminUnit
    {
        private AdminUnit()
        {
        }

        public static AdminUnit Value { get; } = new AdminUnit();
    }

    public sealed class AdminPage<T>
    {
        internal AdminPage(
            IReadOnlyList<T> items,
            int totalCount,
            string nextCursor)
        {
            Items = items ?? throw new ArgumentNullException(nameof(items));
            if (totalCount < 0 || totalCount < items.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(totalCount),
                    "Total count cannot be negative or smaller than the current page.");
            }

            TotalCount = totalCount;
            NextCursor = string.IsNullOrEmpty(nextCursor) ? null : nextCursor;
        }

        public IReadOnlyList<T> Items { get; }

        public int TotalCount { get; }

        public string NextCursor { get; }
    }

    public sealed class AdminServiceDefinition
    {
        internal AdminServiceDefinition(
            string name,
            string productCode,
            string serviceHostName,
            string serviceIpv4Address,
            int port)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            ProductCode = productCode ?? throw new ArgumentNullException(nameof(productCode));
            ServiceHostName = serviceHostName
                ?? throw new ArgumentNullException(nameof(serviceHostName));
            ServiceIpv4Address = serviceIpv4Address
                ?? throw new ArgumentNullException(nameof(serviceIpv4Address));
            Port = port;
        }

        public string Name { get; }

        public string ProductCode { get; }

        public string ServiceHostName { get; }

        public string ServiceIpv4Address { get; }

        public int Port { get; }
    }

    public sealed class AdminServiceItem
    {
        internal AdminServiceItem(
            AdminServiceDefinition definition,
            DateTime lastModifiedUtc,
            bool deleted,
            DateTime? deletedUtc)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            if (lastModifiedUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("Last modified time must be UTC.", nameof(lastModifiedUtc));
            }

            if (deleted != deletedUtc.HasValue)
            {
                throw new ArgumentException(
                    "DeletedUtc must be present only for a deleted service.",
                    nameof(deletedUtc));
            }

            if (deletedUtc.HasValue && deletedUtc.Value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("Deleted time must be UTC.", nameof(deletedUtc));
            }

            LastModifiedUtc = lastModifiedUtc;
            Deleted = deleted;
            DeletedUtc = deletedUtc;
        }

        public AdminServiceDefinition Definition { get; }

        public string Name => Definition.Name;

        public string ProductCode => Definition.ProductCode;

        public string ServiceHostName => Definition.ServiceHostName;

        public string ServiceIpv4Address =>
            Definition.ServiceIpv4Address;

        public int Port => Definition.Port;

        public DateTime LastModifiedUtc { get; }

        public bool Deleted { get; }

        public DateTime? DeletedUtc { get; }
    }

    public enum AdminPairingState
    {
        Unpaired = 1,
        PairingWindowOpen = 2,
        Negotiating = 3,
        SasPending = 4,
        BothConfirmed = 5,
        PairedPendingCommit = 6,
        PairedDisabled = 7,
        Enabled = 8
    }

    public enum AdminPeerNotificationOperation
    {
        None = 1,
        Release = 2,
        Revoke = 3
    }

    public enum AdminPeerNotificationResult
    {
        NotRun = 1,
        Confirmed = 2,
        Unconfirmed = 3,
        NotRequired = 4
    }

    public sealed class AdminSyncStatus
    {
        internal AdminSyncStatus(
            bool enabled,
            AdminPairingState pairingState,
            string peerEndpoint,
            Guid? peerInstanceId,
            ulong? keyEpoch,
            DateTime? lastSyncUtc,
            string lastResult,
            long? clockSkewSeconds,
            Guid? pairingId,
            string sas,
            DateTime? pairingExpiresUtc,
            int? pairingRemainingSeconds,
            bool? localConfirmed,
            bool? remoteConfirmed,
            DateTime? commitExpiresUtc,
            bool? localCommitConfirmed,
            bool? remoteCommitConfirmed,
            AdminPeerNotificationOperation lastPeerNotificationOperation,
            AdminPeerNotificationResult lastPeerNotificationResult,
            DateTime? lastPeerNotificationUtc)
        {
            if (string.IsNullOrWhiteSpace(lastResult))
            {
                throw new ArgumentException("Last result is required.", nameof(lastResult));
            }

            string canonicalPeerEndpoint = null;
            if (peerEndpoint != null
                && (!AdminPeerEndpoint.TryNormalize(
                        peerEndpoint,
                        out canonicalPeerEndpoint)
                    || !StringComparer.Ordinal.Equals(
                        peerEndpoint,
                        canonicalPeerEndpoint)))
            {
                throw new ArgumentException(
                    "Peer endpoint is not canonical.",
                    nameof(peerEndpoint));
            }

            if (lastSyncUtc.HasValue && lastSyncUtc.Value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("Last sync time must be UTC.", nameof(lastSyncUtc));
            }

            if (pairingExpiresUtc.HasValue
                && pairingExpiresUtc.Value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Pairing expiry must be UTC.",
                    nameof(pairingExpiresUtc));
            }

            if (commitExpiresUtc.HasValue
                && commitExpiresUtc.Value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Commit expiry must be UTC.",
                    nameof(commitExpiresUtc));
            }

            if (lastPeerNotificationUtc.HasValue
                && lastPeerNotificationUtc.Value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Peer notification time must be UTC.",
                    nameof(lastPeerNotificationUtc));
            }

            if (sas != null
                && (sas.Length != 8 || !IsAsciiDigits(sas)))
            {
                throw new ArgumentException("SAS must contain exactly eight digits.", nameof(sas));
            }

            ValidateLastResult(lastResult, lastSyncUtc, clockSkewSeconds);
            ValidateNotification(
                lastPeerNotificationOperation,
                lastPeerNotificationResult,
                lastPeerNotificationUtc);
            ValidatePairingShape(
                enabled,
                pairingState,
                canonicalPeerEndpoint,
                peerInstanceId,
                keyEpoch,
                pairingId,
                sas,
                pairingExpiresUtc,
                pairingRemainingSeconds,
                localConfirmed,
                remoteConfirmed,
                commitExpiresUtc,
                localCommitConfirmed,
                remoteCommitConfirmed);

            Enabled = enabled;
            PairingState = pairingState;
            PeerEndpoint = canonicalPeerEndpoint;
            PeerInstanceId = peerInstanceId;
            KeyEpoch = keyEpoch;
            LastSyncUtc = lastSyncUtc;
            LastResult = lastResult;
            ClockSkewSeconds = clockSkewSeconds;
            PairingId = pairingId;
            Sas = sas;
            PairingExpiresUtc = pairingExpiresUtc;
            PairingRemainingSeconds = pairingRemainingSeconds;
            LocalConfirmed = localConfirmed;
            RemoteConfirmed = remoteConfirmed;
            CommitExpiresUtc = commitExpiresUtc;
            LocalCommitConfirmed = localCommitConfirmed;
            RemoteCommitConfirmed = remoteCommitConfirmed;
            LastPeerNotificationOperation = lastPeerNotificationOperation;
            LastPeerNotificationResult = lastPeerNotificationResult;
            LastPeerNotificationUtc = lastPeerNotificationUtc;
        }

        public bool Enabled { get; }

        public AdminPairingState PairingState { get; }

        public string PairingStateText => PairingState.ToString();

        public string PeerEndpoint { get; }

        public Guid? PeerInstanceId { get; }

        public ulong? KeyEpoch { get; }

        public DateTime? LastSyncUtc { get; }

        public string LastResult { get; }

        public long? ClockSkewSeconds { get; }

        public Guid? PairingId { get; }

        public string Sas { get; }

        public DateTime? PairingExpiresUtc { get; }

        public int? PairingRemainingSeconds { get; }

        public bool? LocalConfirmed { get; }

        public bool? RemoteConfirmed { get; }

        public DateTime? CommitExpiresUtc { get; }

        public bool? LocalCommitConfirmed { get; }

        public bool? RemoteCommitConfirmed { get; }

        public AdminPeerNotificationOperation LastPeerNotificationOperation { get; }

        public AdminPeerNotificationResult LastPeerNotificationResult { get; }

        public DateTime? LastPeerNotificationUtc { get; }

        public bool HasUnconfirmedPeerNotification =>
            LastPeerNotificationResult == AdminPeerNotificationResult.Unconfirmed;

        private static void ValidateLastResult(
            string lastResult,
            DateTime? lastSyncUtc,
            long? clockSkewSeconds)
        {
            bool notRun = StringComparer.Ordinal.Equals(lastResult, "NOT_RUN");
            if (notRun && (lastSyncUtc.HasValue || clockSkewSeconds.HasValue))
            {
                throw new ArgumentException(
                    "NOT_RUN cannot include sync time or clock skew.",
                    nameof(lastResult));
            }

            if (!notRun && !lastSyncUtc.HasValue)
            {
                throw new ArgumentException(
                    "A sync result requires the last attempt time.",
                    nameof(lastResult));
            }
        }

        private static void ValidateNotification(
            AdminPeerNotificationOperation operation,
            AdminPeerNotificationResult result,
            DateTime? notificationUtc)
        {
            bool initial = operation == AdminPeerNotificationOperation.None
                && result == AdminPeerNotificationResult.NotRun;
            if (initial != !notificationUtc.HasValue)
            {
                throw new ArgumentException(
                    "Peer notification operation, result, and time are inconsistent.",
                    nameof(notificationUtc));
            }

            if ((operation == AdminPeerNotificationOperation.None)
                != (result == AdminPeerNotificationResult.NotRun))
            {
                throw new ArgumentException(
                    "Peer notification initial values are inconsistent.",
                    nameof(result));
            }
        }

        private static void ValidatePairingShape(
            bool enabled,
            AdminPairingState state,
            string peerEndpoint,
            Guid? peerInstanceId,
            ulong? keyEpoch,
            Guid? pairingId,
            string sas,
            DateTime? pairingExpiresUtc,
            int? pairingRemainingSeconds,
            bool? localConfirmed,
            bool? remoteConfirmed,
            DateTime? commitExpiresUtc,
            bool? localCommitConfirmed,
            bool? remoteCommitConfirmed)
        {
            bool hasEndpoint = !string.IsNullOrWhiteSpace(peerEndpoint);
            bool hasPositiveEpoch = keyEpoch.HasValue && keyEpoch.Value > 0;
            bool hasPairingExpiry = pairingExpiresUtc.HasValue
                && pairingRemainingSeconds.HasValue
                && pairingRemainingSeconds.Value >= 0
                && pairingRemainingSeconds.Value <= 300;
            bool hasNoPairingExpiry = !pairingExpiresUtc.HasValue
                && !pairingRemainingSeconds.HasValue;
            bool noConfirmFields = !localConfirmed.HasValue
                && !remoteConfirmed.HasValue;
            bool noCommitFields = !commitExpiresUtc.HasValue
                && !localCommitConfirmed.HasValue
                && !remoteCommitConfirmed.HasValue;

            bool valid;
            switch (state)
            {
                case AdminPairingState.Unpaired:
                    valid = !enabled
                        && !hasEndpoint
                        && !peerInstanceId.HasValue
                        && !keyEpoch.HasValue
                        && !pairingId.HasValue
                        && sas == null
                        && hasNoPairingExpiry
                        && noConfirmFields
                        && noCommitFields;
                    break;
                case AdminPairingState.PairingWindowOpen:
                    valid = !enabled
                        && hasEndpoint
                        && !peerInstanceId.HasValue
                        && !keyEpoch.HasValue
                        && !pairingId.HasValue
                        && sas == null
                        && hasPairingExpiry
                        && noConfirmFields
                        && noCommitFields;
                    break;
                case AdminPairingState.Negotiating:
                    valid = !enabled
                        && hasEndpoint
                        && !keyEpoch.HasValue
                        && pairingId.HasValue
                        && sas == null
                        && hasPairingExpiry
                        && noConfirmFields
                        && noCommitFields;
                    break;
                case AdminPairingState.SasPending:
                    valid = !enabled
                        && hasEndpoint
                        && peerInstanceId.HasValue
                        && !keyEpoch.HasValue
                        && pairingId.HasValue
                        && hasPairingExpiry
                        && localConfirmed.HasValue
                        && remoteConfirmed.HasValue
                        && (sas != null) == !localConfirmed.Value
                        && noCommitFields;
                    break;
                case AdminPairingState.BothConfirmed:
                    valid = !enabled
                        && hasEndpoint
                        && peerInstanceId.HasValue
                        && !keyEpoch.HasValue
                        && pairingId.HasValue
                        && sas == null
                        && hasPairingExpiry
                        && localConfirmed == true
                        && remoteConfirmed == true
                        && noCommitFields;
                    break;
                case AdminPairingState.PairedPendingCommit:
                    valid = !enabled
                        && hasEndpoint
                        && peerInstanceId.HasValue
                        && hasPositiveEpoch
                        && pairingId.HasValue
                        && sas == null
                        && hasNoPairingExpiry
                        && noConfirmFields
                        && commitExpiresUtc.HasValue
                        && localCommitConfirmed.HasValue
                        && remoteCommitConfirmed.HasValue;
                    break;
                case AdminPairingState.PairedDisabled:
                    valid = !enabled
                        && hasEndpoint
                        && peerInstanceId.HasValue
                        && hasPositiveEpoch
                        && !pairingId.HasValue
                        && sas == null
                        && hasNoPairingExpiry
                        && noConfirmFields
                        && noCommitFields;
                    break;
                case AdminPairingState.Enabled:
                    valid = enabled
                        && hasEndpoint
                        && peerInstanceId.HasValue
                        && hasPositiveEpoch
                        && !pairingId.HasValue
                        && sas == null
                        && hasNoPairingExpiry
                        && noConfirmFields
                        && noCommitFields;
                    break;
                default:
                    valid = false;
                    break;
            }

            if (!valid)
            {
                throw new ArgumentException(
                    "Sync status fields do not match the exact pairing state contract.",
                    nameof(state));
            }
        }

        private static bool IsAsciiDigits(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] < '0' || value[index] > '9')
                {
                    return false;
                }
            }

            return true;
        }
    }

    public sealed class AdminSyncDisableResult
    {
        internal AdminSyncDisableResult(
            AdminPairingState localPairingState,
            AdminPeerNotificationOperation peerNotificationOperation,
            AdminPeerNotificationResult peerNotificationResult,
            DateTime peerNotificationUtc)
        {
            if (localPairingState != AdminPairingState.PairedDisabled
                && localPairingState != AdminPairingState.Unpaired)
            {
                throw new ArgumentOutOfRangeException(nameof(localPairingState));
            }

            if (peerNotificationOperation == AdminPeerNotificationOperation.None
                || peerNotificationResult == AdminPeerNotificationResult.NotRun)
            {
                throw new ArgumentException(
                    "A disable result requires a completed peer notification outcome.");
            }

            if (peerNotificationUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Peer notification time must be UTC.",
                    nameof(peerNotificationUtc));
            }

            LocalPairingState = localPairingState;
            PeerNotificationOperation = peerNotificationOperation;
            PeerNotificationResult = peerNotificationResult;
            PeerNotificationUtc = peerNotificationUtc;
        }

        public AdminPairingState LocalPairingState { get; }

        public AdminPeerNotificationOperation PeerNotificationOperation { get; }

        public AdminPeerNotificationResult PeerNotificationResult { get; }

        public DateTime PeerNotificationUtc { get; }

        public bool IsPeerConfirmationUnconfirmed =>
            PeerNotificationResult == AdminPeerNotificationResult.Unconfirmed;
    }

    public sealed class AdminLoggingSettings
    {
        internal AdminLoggingSettings(int logRetentionDays)
        {
            if (logRetentionDays < AdminApiContract.MinimumLogRetentionDays
                || logRetentionDays > AdminApiContract.MaximumLogRetentionDays)
            {
                throw new ArgumentOutOfRangeException(nameof(logRetentionDays));
            }

            LogRetentionDays = logRetentionDays;
        }

        public int LogRetentionDays { get; }
    }
}
