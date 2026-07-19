using System;
using System.Collections.Generic;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.InternalProtocol.Peer
{
    public abstract class PeerSyncDataBatch
    {
        protected PeerSyncDataBatch(
            Guid instanceId,
            Guid snapshotId,
            ulong logicalClock,
            uint batchIndex,
            ulong totalCount,
            bool isLastBatch,
            IReadOnlyList<PeerSyncServiceItem> items)
        {
            if (instanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Instance ID cannot be empty.",
                    nameof(instanceId));
            }

            if (snapshotId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Snapshot ID cannot be empty.",
                    nameof(snapshotId));
            }

            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            int itemCount = items.Count;
            if (itemCount > PeerSyncContract.MaximumBatchItemCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(items),
                    "A Peer sync batch cannot exceed 1,000 items.");
            }

            if (totalCount < (ulong)itemCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(totalCount),
                    "TotalCount cannot be smaller than the current batch.");
            }

            var itemCopy = new List<PeerSyncServiceItem>(itemCount);
            string previousProductCode = null;
            for (int index = 0; index < itemCount; index++)
            {
                PeerSyncServiceItem item = items[index];
                if (item == null)
                {
                    throw new ArgumentException(
                        "A Peer sync batch cannot contain a null item.",
                        nameof(items));
                }

                if (item.LogicalVersion > logicalClock)
                {
                    throw new ArgumentException(
                        "A Peer sync item version cannot exceed LogicalClock.",
                        nameof(items));
                }

                if (previousProductCode != null
                    && string.CompareOrdinal(
                        previousProductCode,
                        item.ProductCode) >= 0)
                {
                    throw new ArgumentException(
                        "Peer sync ProductCode values must be strictly Ordinal ascending.",
                        nameof(items));
                }

                previousProductCode = item.ProductCode;
                itemCopy.Add(item);
            }

            InstanceId = instanceId;
            SnapshotId = snapshotId;
            LogicalClock = logicalClock;
            BatchIndex = batchIndex;
            TotalCount = totalCount;
            IsLastBatch = isLastBatch;
            Items = itemCopy.AsReadOnly();
        }

        public Guid InstanceId { get; }

        public Guid SnapshotId { get; }

        public ulong LogicalClock { get; }

        public uint BatchIndex { get; }

        public ulong TotalCount { get; }

        public bool IsLastBatch { get; }

        public IReadOnlyList<PeerSyncServiceItem> Items { get; }
    }

    public sealed class PeerPushExchangeRequest : PeerSyncDataBatch
    {
        public PeerPushExchangeRequest(
            Guid instanceId,
            Guid snapshotId,
            ulong logicalClock,
            uint batchIndex,
            ulong totalCount,
            bool isLastBatch,
            IReadOnlyList<PeerSyncServiceItem> items)
            : base(
                instanceId,
                snapshotId,
                logicalClock,
                batchIndex,
                totalCount,
                isLastBatch,
                items)
        {
        }
    }

    public sealed class PeerPullExchangeBatch : PeerSyncDataBatch
    {
        public PeerPullExchangeBatch(
            Guid instanceId,
            Guid snapshotId,
            ulong logicalClock,
            uint batchIndex,
            ulong totalCount,
            bool isLastBatch,
            IReadOnlyList<PeerSyncServiceItem> items)
            : base(
                instanceId,
                snapshotId,
                logicalClock,
                batchIndex,
                totalCount,
                isLastBatch,
                items)
        {
        }
    }

    public sealed class PeerSyncServiceItem
    {
        public PeerSyncServiceItem(
            string name,
            string productCode,
            string serverAddress,
            int port,
            DateTime lastModifiedUtc,
            bool deleted,
            DateTime? deletedUtc,
            ulong logicalVersion,
            Guid originInstanceId)
        {
            ServiceDefinition definition;
            ServiceDefinitionValidationError validationError;
            if (!ServiceDefinition.TryCreate(
                name,
                productCode,
                serverAddress,
                port,
                out definition,
                out validationError)
                || !StringComparer.Ordinal.Equals(
                    name,
                    definition.Name)
                || !StringComparer.Ordinal.Equals(
                    productCode,
                    definition.ProductCode.Value)
                || !StringComparer.Ordinal.Equals(
                    serverAddress,
                    definition.ServerAddress))
            {
                throw new ArgumentException(
                    "The Peer sync service definition is invalid or non-canonical.",
                    nameof(name));
            }

            if (lastModifiedUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Last modified time must be UTC.",
                    nameof(lastModifiedUtc));
            }

            if (deleted != deletedUtc.HasValue)
            {
                throw new ArgumentException(
                    "Deleted and DeletedUtc must describe the same state.",
                    nameof(deletedUtc));
            }

            if (deletedUtc.HasValue
                && deletedUtc.Value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Deleted time must be UTC.",
                    nameof(deletedUtc));
            }

            if (logicalVersion == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(logicalVersion));
            }

            if (originInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Origin instance ID cannot be empty.",
                    nameof(originInstanceId));
            }

            Name = definition.Name;
            ProductCode = definition.ProductCode.Value;
            ServerAddress = definition.ServerAddress;
            Port = definition.Port;
            LastModifiedUtc = lastModifiedUtc;
            Deleted = deleted;
            DeletedUtc = deletedUtc;
            LogicalVersion = logicalVersion;
            OriginInstanceId = originInstanceId;
        }

        public string Name { get; }

        public string ProductCode { get; }

        public string ServerAddress { get; }

        public int Port { get; }

        public DateTime LastModifiedUtc { get; }

        public bool Deleted { get; }

        public DateTime? DeletedUtc { get; }

        public ulong LogicalVersion { get; }

        public Guid OriginInstanceId { get; }
    }

    public sealed class PeerPullExchangeRequest
    {
        public PeerPullExchangeRequest(Guid snapshotId, uint batchIndex)
        {
            if (snapshotId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Snapshot ID cannot be empty.",
                    nameof(snapshotId));
            }

            SnapshotId = snapshotId;
            BatchIndex = batchIndex;
        }

        public Guid SnapshotId { get; }

        public uint BatchIndex { get; }
    }

    public sealed class PeerExchangeAcknowledgement
    {
        public PeerExchangeAcknowledgement(
            Guid snapshotId,
            uint batchIndex,
            Guid? serverSnapshotId)
        {
            if (snapshotId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Snapshot ID cannot be empty.",
                    nameof(snapshotId));
            }

            if (serverSnapshotId.HasValue
                && serverSnapshotId.Value == Guid.Empty)
            {
                throw new ArgumentException(
                    "Server snapshot ID cannot be empty when present.",
                    nameof(serverSnapshotId));
            }

            SnapshotId = snapshotId;
            BatchIndex = batchIndex;
            ServerSnapshotId = serverSnapshotId;
        }

        public Guid SnapshotId { get; }

        public uint BatchIndex { get; }

        public Guid? ServerSnapshotId { get; }
    }

    public enum PeerSyncResponseCode : uint
    {
        Ok = 0,
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

    public enum PeerExchangeResponseKind
    {
        PushAcknowledgement = 1,
        PullBatch = 2,
        Error = 3
    }

    public sealed class PeerExchangeResponse
    {
        private PeerExchangeResponse(
            PeerExchangeResponseKind kind,
            PeerSyncResponseCode code,
            string message,
            PeerExchangeAcknowledgement acknowledgement,
            PeerPullExchangeBatch pullBatch)
        {
            if (!Enum.IsDefined(typeof(PeerSyncResponseCode), code))
            {
                throw new ArgumentOutOfRangeException(nameof(code));
            }

            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (message.Length > 512)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(message),
                    "A Peer response message cannot exceed 512 characters.");
            }

            bool isError = kind == PeerExchangeResponseKind.Error;
            if (isError != (code != PeerSyncResponseCode.Ok))
            {
                throw new ArgumentException(
                    "Peer response kind and code are inconsistent.",
                    nameof(code));
            }

            if (kind == PeerExchangeResponseKind.PushAcknowledgement)
            {
                if (acknowledgement == null || pullBatch != null)
                {
                    throw new ArgumentException(
                        "A Push response requires only an acknowledgement.",
                        nameof(acknowledgement));
                }
            }
            else if (kind == PeerExchangeResponseKind.PullBatch)
            {
                if (pullBatch == null || acknowledgement != null)
                {
                    throw new ArgumentException(
                        "A Pull response requires only a sync batch.",
                        nameof(pullBatch));
                }
            }
            else if (kind == PeerExchangeResponseKind.Error)
            {
                if (acknowledgement != null || pullBatch != null)
                {
                    throw new ArgumentException(
                        "An error response cannot contain an exchange payload.",
                        nameof(kind));
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }

            Kind = kind;
            Code = code;
            Message = message;
            Acknowledgement = acknowledgement;
            PullBatch = pullBatch;
        }

        public PeerExchangeResponseKind Kind { get; }

        public string Result => IsSuccess ? "OK" : "ERROR";

        public PeerSyncResponseCode Code { get; }

        public string Message { get; }

        public PeerExchangeAcknowledgement Acknowledgement { get; }

        public PeerPullExchangeBatch PullBatch { get; }

        public bool IsSuccess => Code == PeerSyncResponseCode.Ok;

        public static PeerExchangeResponse CreatePushSuccess(
            PeerExchangeAcknowledgement acknowledgement)
        {
            return new PeerExchangeResponse(
                PeerExchangeResponseKind.PushAcknowledgement,
                PeerSyncResponseCode.Ok,
                string.Empty,
                acknowledgement,
                null);
        }

        public static PeerExchangeResponse CreatePullSuccess(
            PeerPullExchangeBatch pullBatch)
        {
            return new PeerExchangeResponse(
                PeerExchangeResponseKind.PullBatch,
                PeerSyncResponseCode.Ok,
                string.Empty,
                null,
                pullBatch);
        }

        internal static PeerExchangeResponse CreateParsedPushSuccess(
            PeerExchangeAcknowledgement acknowledgement,
            string message)
        {
            return new PeerExchangeResponse(
                PeerExchangeResponseKind.PushAcknowledgement,
                PeerSyncResponseCode.Ok,
                message,
                acknowledgement,
                null);
        }

        internal static PeerExchangeResponse CreateParsedPullSuccess(
            PeerPullExchangeBatch pullBatch,
            string message)
        {
            return new PeerExchangeResponse(
                PeerExchangeResponseKind.PullBatch,
                PeerSyncResponseCode.Ok,
                message,
                null,
                pullBatch);
        }

        // Outbound error messages are deliberately empty so an exception,
        // request body, header, nonce or other secret cannot be reflected.
        public static PeerExchangeResponse CreateError(
            PeerSyncResponseCode code)
        {
            if (code == PeerSyncResponseCode.Ok)
            {
                throw new ArgumentOutOfRangeException(nameof(code));
            }

            return new PeerExchangeResponse(
                PeerExchangeResponseKind.Error,
                code,
                string.Empty,
                null,
                null);
        }

        internal static PeerExchangeResponse CreateParsedError(
            PeerSyncResponseCode code,
            string message)
        {
            return new PeerExchangeResponse(
                PeerExchangeResponseKind.Error,
                code,
                message,
                null,
                null);
        }
    }
}
