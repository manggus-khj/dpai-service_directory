using System;
using System.Collections.Generic;
using DEEPAi.ServiceDirectory.Domain.Registration;

namespace DEEPAi.ServiceDirectory.Domain.Synchronization
{
    public sealed class SynchronizationMergeResult : StateTransitionResult
    {
        private SynchronizationMergeResult(
            bool isSuccess,
            DomainErrorCode? errorCode,
            DirectorySnapshot nextSnapshot,
            bool stateChanged)
            : base(
                isSuccess,
                errorCode,
                nextSnapshot,
                stateChanged,
                stateChanged,
                false)
        {
            if ((isSuccess && errorCode.HasValue)
                || (!isSuccess
                    && errorCode != DomainErrorCode.RevisionCollision
                    && errorCode != DomainErrorCode.DirectoryCapacity)
                || (!isSuccess && stateChanged))
            {
                throw new ArgumentException("The synchronization merge result is inconsistent.");
            }
        }

        internal static SynchronizationMergeResult Success(
            DirectorySnapshot nextSnapshot,
            bool stateChanged)
        {
            return new SynchronizationMergeResult(
                true,
                null,
                nextSnapshot,
                stateChanged);
        }

        internal static SynchronizationMergeResult Failure(
            DirectorySnapshot current,
            DomainErrorCode errorCode)
        {
            return new SynchronizationMergeResult(
                false,
                errorCode,
                current,
                false);
        }
    }

    public static class SynchronizationMerger
    {
        public static SynchronizationMergeResult Merge(
            DirectorySnapshot local,
            SynchronizationSnapshot remote)
        {
            if (local == null)
            {
                throw new ArgumentNullException(nameof(local));
            }

            if (remote == null)
            {
                throw new ArgumentNullException(nameof(remote));
            }

            var candidateRecords = new Dictionary<ProductCode, ServiceRecord>();
            foreach (KeyValuePair<ProductCode, ServiceRecord> localEntry in local.Records)
            {
                candidateRecords.Add(localEntry.Key, localEntry.Value);
            }

            bool recordsChanged = false;
            foreach (KeyValuePair<ProductCode, ServiceRecord> remoteEntry in remote.Records)
            {
                ServiceRecord localRecord;
                if (!candidateRecords.TryGetValue(remoteEntry.Key, out localRecord))
                {
                    candidateRecords.Add(remoteEntry.Key, remoteEntry.Value);
                    recordsChanged = true;
                    continue;
                }

                int revisionComparison;
                try
                {
                    revisionComparison = ServiceRecordRevisionComparer.Instance.Compare(
                        remoteEntry.Value,
                        localRecord);
                }
                catch (RevisionCollisionException)
                {
                    return SynchronizationMergeResult.Failure(
                        local,
                        DomainErrorCode.RevisionCollision);
                }

                if (revisionComparison > 0)
                {
                    candidateRecords[remoteEntry.Key] = remoteEntry.Value;
                    recordsChanged = true;
                }
            }

            if (HasPendingBaseRevisionCollision(
                    candidateRecords,
                    local.PendingById.Values))
            {
                return SynchronizationMergeResult.Failure(
                    local,
                    DomainErrorCode.RevisionCollision);
            }

            int activeCount = 0;
            foreach (ServiceRecord candidate in candidateRecords.Values)
            {
                if (!candidate.Deleted)
                {
                    activeCount++;
                }
            }

            if (activeCount > DirectorySnapshot.ActiveServiceLimit)
            {
                return SynchronizationMergeResult.Failure(
                    local,
                    DomainErrorCode.DirectoryCapacity);
            }

            ulong mergedLogicalClock = Math.Max(
                local.LogicalClock,
                remote.LogicalClock);
            bool stateChanged = recordsChanged
                || mergedLogicalClock != local.LogicalClock;
            if (!stateChanged)
            {
                return SynchronizationMergeResult.Success(local, false);
            }

            var merged = new DirectorySnapshot(
                candidateRecords.Values,
                local.PendingById.Values,
                mergedLogicalClock);
            return SynchronizationMergeResult.Success(merged, true);
        }

        private static bool HasPendingBaseRevisionCollision(
            IReadOnlyDictionary<ProductCode, ServiceRecord> candidateRecords,
            IEnumerable<PendingRegistration> pendingRegistrations)
        {
            foreach (PendingRegistration pending in pendingRegistrations)
            {
                ServiceRecord baseRecord = pending.BaseRevision.Record;
                if (baseRecord == null)
                {
                    continue;
                }

                ServiceRecord candidate;
                if (!candidateRecords.TryGetValue(
                        pending.Requested.ProductCode,
                        out candidate))
                {
                    continue;
                }

                try
                {
                    ServiceRecordRevisionComparer.Instance.Compare(
                        candidate,
                        baseRecord);
                }
                catch (RevisionCollisionException)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
