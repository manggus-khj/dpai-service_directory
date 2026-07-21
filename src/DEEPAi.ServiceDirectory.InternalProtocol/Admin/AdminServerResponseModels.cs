using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Admin
{
    public sealed class AdminServerServiceDefinition
    {
        public AdminServerServiceDefinition(
            string name,
            string productCode,
            string serviceHostName,
            string serviceIpv4Address,
            int port)
        {
            ServiceEndpointIdentity endpointIdentity;
            EndpointIdentityValidationError identityError;
            if (!ServiceEndpointIdentity.TryCreate(
                    serviceHostName,
                    serviceIpv4Address,
                    out endpointIdentity,
                    out identityError)
                || !StringComparer.Ordinal.Equals(
                    serviceHostName,
                    endpointIdentity.ServiceHostName)
                || !StringComparer.Ordinal.Equals(
                    serviceIpv4Address,
                    endpointIdentity.ServiceIpv4Address)
                || !ServiceDefinition.TryCreate(
                    name,
                    productCode,
                    endpointIdentity,
                    port,
                    out ServiceDefinition definition,
                    out ServiceDefinitionValidationError definitionError)
                || !StringComparer.Ordinal.Equals(name, definition.Name)
                || !StringComparer.Ordinal.Equals(
                    productCode,
                    definition.ProductCode.Value))
            {
                throw new ArgumentException(
                    "The Admin service definition is invalid or non-canonical.",
                    nameof(name));
            }

            Name = definition.Name;
            ProductCode = definition.ProductCode.Value;
            ServiceHostName = endpointIdentity.ServiceHostName;
            ServiceIpv4Address = endpointIdentity.ServiceIpv4Address;
            Port = definition.Port;
        }

        public string Name { get; }

        public string ProductCode { get; }

        public string ServiceHostName { get; }

        public string ServiceIpv4Address { get; }

        public int Port { get; }
    }

    public sealed class AdminServerServiceItem
    {
        public AdminServerServiceItem(
            AdminServerServiceDefinition definition,
            DateTime lastModifiedUtc,
            bool deleted,
            DateTime? deletedUtc)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            AdminServerResponseValidation.EnsureUtc(
                lastModifiedUtc,
                nameof(lastModifiedUtc));
            if (deleted != deletedUtc.HasValue)
            {
                throw new ArgumentException(
                    "DeletedUtc must be present only for a deleted service.",
                    nameof(deletedUtc));
            }

            if (deletedUtc.HasValue)
            {
                AdminServerResponseValidation.EnsureUtc(
                    deletedUtc.Value,
                    nameof(deletedUtc));
            }

            Definition = definition;
            LastModifiedUtc = lastModifiedUtc;
            Deleted = deleted;
            DeletedUtc = deletedUtc;
        }

        public AdminServerServiceDefinition Definition { get; }

        public DateTime LastModifiedUtc { get; }

        public bool Deleted { get; }

        public DateTime? DeletedUtc { get; }
    }

    public sealed class AdminServerServicesResponse
    {
        public AdminServerServicesResponse(
            IReadOnlyList<AdminServerServiceItem> items,
            int totalCount,
            string nextCursor)
        {
            Items = AdminServerResponseValidation.CopyAndValidatePage(
                items,
                totalCount,
                nextCursor,
                CompareServiceItems,
                out string validatedCursor);
            TotalCount = totalCount;
            NextCursor = validatedCursor;
        }

        public IReadOnlyList<AdminServerServiceItem> Items { get; }

        public int TotalCount { get; }

        public string NextCursor { get; }

        private static int CompareServiceItems(
            AdminServerServiceItem left,
            AdminServerServiceItem right)
        {
            return string.CompareOrdinal(
                left.Definition.ProductCode,
                right.Definition.ProductCode);
        }
    }

    public sealed class AdminServerSyncStatusResponse
    {
        private readonly AdminSyncStatus _value;

        public AdminServerSyncStatusResponse(
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
            if (!Enum.IsDefined(typeof(AdminPairingState), pairingState))
            {
                throw new ArgumentOutOfRangeException(nameof(pairingState));
            }

            if (!Enum.IsDefined(
                    typeof(AdminPeerNotificationOperation),
                    lastPeerNotificationOperation))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lastPeerNotificationOperation));
            }

            if (!Enum.IsDefined(
                    typeof(AdminPeerNotificationResult),
                    lastPeerNotificationResult))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lastPeerNotificationResult));
            }

            AdminServerResponseValidation.EnsureOptionalNonEmptyGuid(
                peerInstanceId,
                nameof(peerInstanceId));
            AdminServerResponseValidation.EnsureOptionalNonEmptyGuid(
                pairingId,
                nameof(pairingId));
            AdminServerResponseValidation.ValidateStatusName(
                lastResult,
                nameof(lastResult));
            AdminServerResponseValidation.ValidateNotificationOutcome(
                lastPeerNotificationOperation,
                lastPeerNotificationResult,
                nameof(lastPeerNotificationResult));

            _value = new AdminSyncStatus(
                enabled,
                pairingState,
                peerEndpoint,
                peerInstanceId,
                keyEpoch,
                lastSyncUtc,
                lastResult,
                clockSkewSeconds,
                pairingId,
                sas,
                pairingExpiresUtc,
                pairingRemainingSeconds,
                localConfirmed,
                remoteConfirmed,
                commitExpiresUtc,
                localCommitConfirmed,
                remoteCommitConfirmed,
                lastPeerNotificationOperation,
                lastPeerNotificationResult,
                lastPeerNotificationUtc);
        }

        public bool Enabled => _value.Enabled;

        public AdminPairingState PairingState => _value.PairingState;

        public string PeerEndpoint => _value.PeerEndpoint;

        public Guid? PeerInstanceId => _value.PeerInstanceId;

        public ulong? KeyEpoch => _value.KeyEpoch;

        public Guid? PairingId => _value.PairingId;

        public DateTime? PairingExpiresUtc => _value.PairingExpiresUtc;

        public int? PairingRemainingSeconds =>
            _value.PairingRemainingSeconds;

        public string Sas => _value.Sas;

        public bool? LocalConfirmed => _value.LocalConfirmed;

        public bool? RemoteConfirmed => _value.RemoteConfirmed;

        public DateTime? CommitExpiresUtc => _value.CommitExpiresUtc;

        public bool? LocalCommitConfirmed => _value.LocalCommitConfirmed;

        public bool? RemoteCommitConfirmed => _value.RemoteCommitConfirmed;

        public DateTime? LastSyncUtc => _value.LastSyncUtc;

        public string LastResult => _value.LastResult;

        public long? ClockSkewSeconds => _value.ClockSkewSeconds;

        public AdminPeerNotificationOperation LastPeerNotificationOperation =>
            _value.LastPeerNotificationOperation;

        public AdminPeerNotificationResult LastPeerNotificationResult =>
            _value.LastPeerNotificationResult;

        public DateTime? LastPeerNotificationUtc =>
            _value.LastPeerNotificationUtc;
    }

    public sealed class AdminServerSyncDisableResponse
    {
        private readonly AdminSyncDisableResult _value;

        public AdminServerSyncDisableResponse(
            AdminPairingState localPairingState,
            AdminPeerNotificationOperation peerNotificationOperation,
            AdminPeerNotificationResult peerNotificationResult,
            DateTime peerNotificationUtc)
        {
            if (!Enum.IsDefined(
                    typeof(AdminPeerNotificationOperation),
                    peerNotificationOperation))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(peerNotificationOperation));
            }

            if (!Enum.IsDefined(
                    typeof(AdminPeerNotificationResult),
                    peerNotificationResult))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(peerNotificationResult));
            }

            if (localPairingState != AdminPairingState.PairedDisabled
                && localPairingState != AdminPairingState.Unpaired)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(localPairingState));
            }

            bool isRelease =
                peerNotificationOperation ==
                    AdminPeerNotificationOperation.Release;
            if (isRelease !=
                (localPairingState == AdminPairingState.PairedDisabled))
            {
                throw new ArgumentException(
                    "The peer notification operation does not match the "
                    + "resulting local pairing state.",
                    nameof(peerNotificationOperation));
            }

            AdminServerResponseValidation.ValidateNotificationOutcome(
                peerNotificationOperation,
                peerNotificationResult,
                nameof(peerNotificationResult));

            _value = new AdminSyncDisableResult(
                localPairingState,
                peerNotificationOperation,
                peerNotificationResult,
                peerNotificationUtc);
        }

        public AdminPairingState LocalPairingState =>
            _value.LocalPairingState;

        public AdminPeerNotificationOperation PeerNotificationOperation =>
            _value.PeerNotificationOperation;

        public AdminPeerNotificationResult PeerNotificationResult =>
            _value.PeerNotificationResult;

        public DateTime PeerNotificationUtc =>
            _value.PeerNotificationUtc;
    }

    public sealed class AdminServerLoggingResponse
    {
        private readonly AdminLoggingSettings _value;

        public AdminServerLoggingResponse(int logRetentionDays)
        {
            _value = new AdminLoggingSettings(logRetentionDays);
        }

        public int LogRetentionDays => _value.LogRetentionDays;
    }

    public sealed class AdminServerUnitResponse
    {
        private AdminServerUnitResponse()
        {
        }

        public static AdminServerUnitResponse Value { get; } =
            new AdminServerUnitResponse();
    }

    public enum AdminServerErrorCode
    {
        BadRequest = 1000,
        NotFound = 1001,
        Conflict = 1002,
        LimitExceeded = 1004,
        NotPeer = 2001,
        PeerMismatch = 2002,
        ClockSkew = 2003,
        SyncDisabled = 2004,
        RevisionCollision = 2005,
        DirectoryCapacity = 2006,
        LogicalClockExhausted = 2007,
        Internal = 3000
    }

    public sealed class AdminServerErrorResponse
    {
        public AdminServerErrorResponse(AdminServerErrorCode code)
        {
            if (!Enum.IsDefined(typeof(AdminServerErrorCode), code))
            {
                throw new ArgumentOutOfRangeException(nameof(code));
            }

            Code = code;
            Message = GetSafeMessage(code);
        }

        public AdminServerErrorCode Code { get; }

        public int NumericCode => (int)Code;

        public string Message { get; }

        private static string GetSafeMessage(AdminServerErrorCode code)
        {
            switch (code)
            {
                case AdminServerErrorCode.BadRequest:
                    return "The request is invalid.";
                case AdminServerErrorCode.NotFound:
                    return "The requested item was not found.";
                case AdminServerErrorCode.Conflict:
                    return "The request conflicts with the current state.";
                case AdminServerErrorCode.LimitExceeded:
                    return "The request limit was exceeded.";
                case AdminServerErrorCode.NotPeer:
                    return "The caller is not the configured peer.";
                case AdminServerErrorCode.PeerMismatch:
                    return "The peer configuration does not match.";
                case AdminServerErrorCode.ClockSkew:
                    return "The peer clock is outside the allowed range.";
                case AdminServerErrorCode.SyncDisabled:
                    return "Synchronization is disabled.";
                case AdminServerErrorCode.RevisionCollision:
                    return "A synchronization revision conflict was detected.";
                case AdminServerErrorCode.DirectoryCapacity:
                    return "The directory capacity was exceeded.";
                case AdminServerErrorCode.LogicalClockExhausted:
                    return "The logical clock is exhausted.";
                case AdminServerErrorCode.Internal:
                    return "The service directory could not process the request.";
                default:
                    throw new ArgumentOutOfRangeException(nameof(code));
            }
        }
    }

    internal static class AdminServerResponseValidation
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        internal static IReadOnlyList<T> CopyAndValidatePage<T>(
            IReadOnlyList<T> items,
            int totalCount,
            string nextCursor,
            Comparison<T> comparison,
            out string validatedCursor)
            where T : class
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            if (comparison == null)
            {
                throw new ArgumentNullException(nameof(comparison));
            }

            if (items.Count > AdminApiContract.PageSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(items),
                    "An Admin page cannot contain more than 250 items.");
            }

            if (totalCount < 0 || totalCount < items.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(totalCount));
            }

            var copy = new List<T>(items.Count);
            T previous = null;
            for (int index = 0; index < items.Count; index++)
            {
                T current = items[index];
                if (current == null)
                {
                    throw new ArgumentException(
                        "Admin pages cannot contain null items.",
                        nameof(items));
                }

                if (previous != null && comparison(previous, current) >= 0)
                {
                    throw new ArgumentException(
                        "Admin page items are not in the required strict order.",
                        nameof(items));
                }

                copy.Add(current);
                previous = current;
            }

            if (items.Count == 0 && totalCount != 0)
            {
                throw new ArgumentException(
                    "An empty Admin page must have a zero total count.",
                    nameof(items));
            }

            validatedCursor = ValidateCursor(nextCursor);
            if (validatedCursor != null && totalCount == items.Count)
            {
                throw new ArgumentException(
                    "A complete Admin page cannot include a next cursor.",
                    nameof(nextCursor));
            }

            return copy.AsReadOnly();
        }

        internal static void EnsureUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Admin response timestamps must use DateTimeKind.Utc.",
                    parameterName);
            }
        }

        internal static void EnsureOptionalNonEmptyGuid(
            Guid? value,
            string parameterName)
        {
            if (value.HasValue && value.Value == Guid.Empty)
            {
                throw new ArgumentException(
                    "Admin response GUID values cannot be empty.",
                    parameterName);
            }
        }

        internal static void ValidateNotificationOutcome(
            AdminPeerNotificationOperation operation,
            AdminPeerNotificationResult result,
            string parameterName)
        {
            if (result == AdminPeerNotificationResult.NotRequired
                && operation != AdminPeerNotificationOperation.Release)
            {
                throw new ArgumentException(
                    "NOT_REQUIRED is valid only for an unnecessary release.",
                    parameterName);
            }
        }

        internal static void ValidateStatusName(
            string value,
            string parameterName)
        {
            if (value == null || value.Length < 2 || value.Length > 64)
            {
                throw new ArgumentException(
                    "Admin status names must contain 2 to 64 ASCII characters.",
                    parameterName);
            }

            if (value[0] < 'A' || value[0] > 'Z')
            {
                throw new ArgumentException(
                    "Admin status names must start with an uppercase ASCII letter.",
                    parameterName);
            }

            for (int index = 1; index < value.Length; index++)
            {
                char current = value[index];
                bool upper = current >= 'A' && current <= 'Z';
                bool digit = current >= '0' && current <= '9';
                if (!upper && !digit && current != '_')
                {
                    throw new ArgumentException(
                        "Admin status names contain an invalid character.",
                        parameterName);
                }
            }
        }

        private static string ValidateCursor(string cursor)
        {
            if (cursor == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(cursor)
                || cursor.Length > 2048)
            {
                throw new ArgumentException(
                    "The Admin cursor is invalid.",
                    nameof(cursor));
            }

            try
            {
                XmlConvert.VerifyXmlChars(cursor);
            }
            catch (XmlException exception)
            {
                throw new ArgumentException(
                    "The Admin cursor contains invalid XML characters.",
                    nameof(cursor),
                    exception);
            }

            if (StrictUtf8.GetByteCount(cursor) >
                AdminApiContract.MaximumBodyBytes)
            {
                throw new ArgumentException(
                    "The Admin cursor is invalid.",
                    nameof(cursor));
            }

            return cursor;
        }
    }
}
