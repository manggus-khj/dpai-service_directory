using System;
using System.Collections.Generic;
using System.Net;
using DEEPAi.ServiceDirectory.Domain.Time;

namespace DEEPAi.ServiceDirectory.Domain.Registration
{
    public static class RegistrationStateMachine
    {
        public static SubmissionResult Submit(
            DirectorySnapshot current,
            ServiceDefinition requested,
            IPAddress sourceAddress,
            Guid pendingId,
            DateTime requestedUtc)
        {
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            if (requested == null)
            {
                throw new ArgumentNullException(nameof(requested));
            }

            if (sourceAddress == null)
            {
                throw new ArgumentNullException(nameof(sourceAddress));
            }

            EnsureUtc(requestedUtc, nameof(requestedUtc));

            PendingRegistration existingPending;
            if (current.TryGetPending(requested.ProductCode, out existingPending))
            {
                if (existingPending.Requested.Equals(requested))
                {
                    return SubmissionResult.Success(
                        current,
                        SubmissionStatus.PendingExists,
                        existingPending.Id,
                        false);
                }

                return SubmissionResult.Failure(current, DomainErrorCode.Conflict);
            }

            ServiceRecord existingRecord;
            current.TryGetRecord(requested.ProductCode, out existingRecord);
            if (existingRecord != null
                && !existingRecord.Deleted
                && existingRecord.Definition.Equals(requested))
            {
                return SubmissionResult.Success(
                    current,
                    SubmissionStatus.AlreadyRegistered,
                    null,
                    false);
            }

            if (current.PendingCount >= DirectorySnapshot.PendingRegistrationLimit)
            {
                return SubmissionResult.Failure(current, DomainErrorCode.LimitExceeded);
            }

            if (pendingId == Guid.Empty)
            {
                throw new ArgumentException("Pending ID must not be empty.", nameof(pendingId));
            }

            PendingRegistration pendingWithSameId;
            if (current.TryGetPending(pendingId, out pendingWithSameId))
            {
                return SubmissionResult.Failure(current, DomainErrorCode.Conflict);
            }

            PendingRequestType type = existingRecord == null || existingRecord.Deleted
                ? PendingRequestType.New
                : PendingRequestType.Modify;
            var pending = new PendingRegistration(
                pendingId,
                type,
                requestedUtc,
                sourceAddress.ToString(),
                requested,
                DirectoryBaseRevision.Capture(existingRecord));
            DirectorySnapshot next = AddPending(current, pending);

            return SubmissionResult.Success(
                next,
                type == PendingRequestType.New
                    ? SubmissionStatus.PendingNew
                    : SubmissionStatus.PendingModify,
                pending.Id,
                true);
        }

        public static ApprovalResult Approve(
            DirectorySnapshot current,
            Guid pendingId,
            Guid localInstanceId,
            DateTime utcNow)
        {
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            if (pendingId == Guid.Empty)
            {
                throw new ArgumentException("Pending ID must not be empty.", nameof(pendingId));
            }

            EnsureInstanceId(localInstanceId, nameof(localInstanceId));
            EnsureUtc(utcNow, nameof(utcNow));

            PendingRegistration pending;
            if (!current.TryGetPending(pendingId, out pending))
            {
                return ApprovalResult.Failure(current, DomainErrorCode.NotFound);
            }

            ProductCode productCode = pending.Requested.ProductCode;
            ServiceRecord existingRecord;
            current.TryGetRecord(productCode, out existingRecord);
            if (!pending.BaseRevision.Matches(existingRecord))
            {
                if (existingRecord != null
                    && !existingRecord.Deleted
                    && existingRecord.Definition.Equals(pending.Requested))
                {
                    DirectorySnapshot satisfied = RemovePending(current, pending.Id);
                    return ApprovalResult.Success(
                        satisfied,
                        ApprovalStatus.AlreadySatisfied,
                        productCode,
                        false);
                }

                return ApprovalResult.Failure(current, DomainErrorCode.Conflict);
            }

            if (pending.Type == PendingRequestType.New
                && current.ActiveCount >= DirectorySnapshot.ActiveServiceLimit)
            {
                return ApprovalResult.Failure(current, DomainErrorCode.LimitExceeded);
            }

            ulong logicalVersion = LogicalVersionClock.Next(current.LogicalClock);
            ServiceRecord approvedRecord = ServiceRecord.CreateActive(
                pending.Requested,
                utcNow,
                logicalVersion,
                localInstanceId);
            DirectorySnapshot approved = ReplaceRecordAndRemovePending(
                current,
                approvedRecord,
                pending.Id,
                logicalVersion);

            return ApprovalResult.Success(
                approved,
                pending.Type == PendingRequestType.New
                    ? ApprovalStatus.Created
                    : ApprovalStatus.Updated,
                productCode,
                true);
        }

        public static RejectResult Reject(DirectorySnapshot current, Guid pendingId)
        {
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            if (pendingId == Guid.Empty)
            {
                throw new ArgumentException("Pending ID must not be empty.", nameof(pendingId));
            }

            PendingRegistration pending;
            if (!current.TryGetPending(pendingId, out pending))
            {
                return RejectResult.Failure(current, DomainErrorCode.NotFound);
            }

            return RejectResult.Success(
                RemovePending(current, pendingId),
                pending.Requested.ProductCode);
        }

        public static DeleteResult Delete(
            DirectorySnapshot current,
            ProductCode productCode,
            Guid localInstanceId,
            DateTime utcNow)
        {
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            if (!productCode.IsValid)
            {
                throw new ArgumentException("Product code must be valid.", nameof(productCode));
            }

            EnsureInstanceId(localInstanceId, nameof(localInstanceId));
            EnsureUtc(utcNow, nameof(utcNow));

            ServiceRecord existingRecord;
            if (!current.TryGetRecord(productCode, out existingRecord) || existingRecord.Deleted)
            {
                return DeleteResult.Failure(current, DomainErrorCode.NotFound);
            }

            ulong logicalVersion = LogicalVersionClock.Next(current.LogicalClock);
            ServiceRecord tombstone = existingRecord.MarkDeleted(
                utcNow,
                logicalVersion,
                localInstanceId);
            DirectorySnapshot deleted = ReplaceRecord(
                current,
                tombstone,
                logicalVersion);

            return DeleteResult.Success(deleted, productCode);
        }

        private static DirectorySnapshot AddPending(
            DirectorySnapshot current,
            PendingRegistration pendingToAdd)
        {
            var pending = new List<PendingRegistration>(current.PendingById.Values)
            {
                pendingToAdd
            };
            return new DirectorySnapshot(
                current.Records.Values,
                pending,
                current.LogicalClock);
        }

        private static DirectorySnapshot RemovePending(DirectorySnapshot current, Guid pendingId)
        {
            var pending = new List<PendingRegistration>();
            foreach (PendingRegistration candidate in current.PendingById.Values)
            {
                if (candidate.Id != pendingId)
                {
                    pending.Add(candidate);
                }
            }

            return new DirectorySnapshot(
                current.Records.Values,
                pending,
                current.LogicalClock);
        }

        private static DirectorySnapshot ReplaceRecord(
            DirectorySnapshot current,
            ServiceRecord replacement,
            ulong logicalClock)
        {
            var records = new List<ServiceRecord>();
            foreach (ServiceRecord candidate in current.Records.Values)
            {
                if (candidate.Definition.ProductCode != replacement.Definition.ProductCode)
                {
                    records.Add(candidate);
                }
            }

            records.Add(replacement);
            return new DirectorySnapshot(
                records,
                current.PendingById.Values,
                logicalClock);
        }

        private static DirectorySnapshot ReplaceRecordAndRemovePending(
            DirectorySnapshot current,
            ServiceRecord replacement,
            Guid pendingId,
            ulong logicalClock)
        {
            var records = new List<ServiceRecord>();
            foreach (ServiceRecord candidate in current.Records.Values)
            {
                if (candidate.Definition.ProductCode != replacement.Definition.ProductCode)
                {
                    records.Add(candidate);
                }
            }

            records.Add(replacement);

            var pending = new List<PendingRegistration>();
            foreach (PendingRegistration candidate in current.PendingById.Values)
            {
                if (candidate.Id != pendingId)
                {
                    pending.Add(candidate);
                }
            }

            return new DirectorySnapshot(
                records,
                pending,
                logicalClock);
        }

        private static void EnsureUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("Domain timestamps must use DateTimeKind.Utc.", parameterName);
            }
        }

        private static void EnsureInstanceId(Guid value, string parameterName)
        {
            if (value == Guid.Empty)
            {
                throw new ArgumentException("Instance ID must not be empty.", parameterName);
            }
        }
    }
}
