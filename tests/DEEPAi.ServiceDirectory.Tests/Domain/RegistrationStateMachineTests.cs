using System;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Registration;
using DEEPAi.ServiceDirectory.Tests.TestSupport;

namespace DEEPAi.ServiceDirectory.Tests.Domain
{
    [TestClass]
    public sealed class RegistrationStateMachineTests
    {
        [TestMethod]
        public void SubmitCreatesPendingRequestAndReusesIdenticalRequest()
        {
            DirectorySnapshot current = DirectorySnapshot.Empty();
            ServiceDefinition requested = TestData.Definition();
            Guid pendingId = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

            SubmissionResult created = RegistrationStateMachine.Submit(
                current,
                requested,
                IPAddress.Parse("192.0.2.10"),
                pendingId,
                TestData.Utc(1));

            Assert.IsTrue(created.IsSuccess);
            Assert.AreEqual(SubmissionStatus.PendingNew, created.Status.Value);
            Assert.AreEqual(pendingId, created.PendingId.Value);
            Assert.IsTrue(created.StateChanged);
            Assert.IsTrue(created.RequiresPersistence);
            Assert.IsFalse(created.ScheduleSync);
            Assert.AreEqual(1, created.NextSnapshot.PendingCount);

            SubmissionResult duplicate = RegistrationStateMachine.Submit(
                created.NextSnapshot,
                requested,
                IPAddress.Parse("192.0.2.11"),
                Guid.NewGuid(),
                TestData.Utc(2));

            Assert.IsTrue(duplicate.IsSuccess);
            Assert.AreEqual(SubmissionStatus.PendingExists, duplicate.Status.Value);
            Assert.AreEqual(pendingId, duplicate.PendingId.Value);
            Assert.IsFalse(duplicate.StateChanged);
            Assert.IsFalse(duplicate.RequiresPersistence);
            Assert.AreSame(created.NextSnapshot, duplicate.NextSnapshot);
        }

        [TestMethod]
        public void SubmitRejectsDifferentRequestWhileProductCodeIsPending()
        {
            ServiceDefinition firstDefinition = TestData.Definition();
            SubmissionResult first = RegistrationStateMachine.Submit(
                DirectorySnapshot.Empty(),
                firstDefinition,
                IPAddress.Loopback,
                Guid.NewGuid(),
                TestData.Utc(1));
            ServiceDefinition conflictingDefinition = TestData.Definition(
                name: "Changed Directory",
                serviceIpv4Address: "10.20.30.41");

            SubmissionResult conflict = RegistrationStateMachine.Submit(
                first.NextSnapshot,
                conflictingDefinition,
                IPAddress.Loopback,
                Guid.NewGuid(),
                TestData.Utc(2));

            Assert.IsFalse(conflict.IsSuccess);
            Assert.AreEqual(DomainErrorCode.Conflict, conflict.ErrorCode.Value);
            Assert.IsFalse(conflict.RequiresPersistence);
            Assert.AreSame(first.NextSnapshot, conflict.NextSnapshot);
        }

        [TestMethod]
        public void ApproveCreatesActiveRecordWithNextLogicalVersion()
        {
            Guid pendingId = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            ServiceDefinition requested = TestData.Definition();
            SubmissionResult submitted = RegistrationStateMachine.Submit(
                DirectorySnapshot.Empty(),
                requested,
                IPAddress.Parse("192.0.2.10"),
                pendingId,
                TestData.Utc(1));

            ApprovalResult approved = RegistrationStateMachine.Approve(
                submitted.NextSnapshot,
                pendingId,
                TestData.OriginA,
                TestData.Utc(2));

            Assert.IsTrue(approved.IsSuccess);
            Assert.AreEqual(ApprovalStatus.Created, approved.Status.Value);
            Assert.IsTrue(approved.RequiresPersistence);
            Assert.IsTrue(approved.ScheduleSync);
            Assert.AreEqual(0, approved.NextSnapshot.PendingCount);
            Assert.AreEqual(1UL, approved.NextSnapshot.LogicalClock);

            ServiceRecord record;
            Assert.IsTrue(
                approved.NextSnapshot.TryGetActiveRecord(
                    requested.ProductCode,
                    out record));
            Assert.AreSame(requested, record.Definition);
            Assert.AreEqual(1UL, record.LogicalVersion);
            Assert.AreEqual(TestData.OriginA, record.OriginInstanceId);
            Assert.AreEqual(TestData.Utc(2), record.LastModifiedUtc);
        }

        [TestMethod]
        public void ApproveRetainsPendingRequestWhenBaseRevisionChanged()
        {
            ServiceDefinition original = TestData.Definition(name: "Original");
            ServiceRecord originalRecord = TestData.ActiveRecord(
                original,
                1UL,
                TestData.OriginA);
            var initial = new DirectorySnapshot(
                new[] { originalRecord },
                new PendingRegistration[0],
                1UL);
            ServiceDefinition requested = TestData.Definition(
                name: "Requested",
                serviceIpv4Address: "10.20.30.41");
            Guid pendingId = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            SubmissionResult submitted = RegistrationStateMachine.Submit(
                initial,
                requested,
                IPAddress.Parse("192.0.2.10"),
                pendingId,
                TestData.Utc(1));
            ServiceDefinition concurrentDefinition = TestData.Definition(
                name: "Concurrent",
                serviceIpv4Address: "10.20.30.42");
            ServiceRecord concurrentRecord = ServiceRecord.CreateActive(
                concurrentDefinition,
                TestData.Utc(2),
                2UL,
                TestData.OriginB);
            var changed = new DirectorySnapshot(
                new[] { concurrentRecord },
                submitted.NextSnapshot.PendingById.Values,
                2UL);

            ApprovalResult conflict = RegistrationStateMachine.Approve(
                changed,
                pendingId,
                TestData.OriginA,
                TestData.Utc(3));

            Assert.IsFalse(conflict.IsSuccess);
            Assert.AreEqual(DomainErrorCode.Conflict, conflict.ErrorCode.Value);
            Assert.IsFalse(conflict.RequiresPersistence);
            Assert.AreSame(changed, conflict.NextSnapshot);
            Assert.AreEqual(1, conflict.NextSnapshot.PendingCount);
        }

        [TestMethod]
        public void DeleteCreatesTombstoneAndAdvancesLogicalClock()
        {
            ServiceDefinition definition = TestData.Definition();
            ServiceRecord active = TestData.ActiveRecord(
                definition,
                4UL,
                TestData.OriginA);
            var current = new DirectorySnapshot(
                new[] { active },
                new PendingRegistration[0],
                4UL);

            DeleteResult deleted = RegistrationStateMachine.Delete(
                current,
                definition.ProductCode,
                TestData.OriginB,
                TestData.Utc(5));

            Assert.IsTrue(deleted.IsSuccess);
            Assert.IsTrue(deleted.RequiresPersistence);
            Assert.IsTrue(deleted.ScheduleSync);
            Assert.AreEqual(5UL, deleted.NextSnapshot.LogicalClock);
            Assert.AreEqual(0, deleted.NextSnapshot.ActiveCount);

            ServiceRecord tombstone;
            Assert.IsTrue(
                deleted.NextSnapshot.TryGetRecord(
                    definition.ProductCode,
                    out tombstone));
            Assert.IsTrue(tombstone.Deleted);
            Assert.AreEqual(TestData.Utc(5), tombstone.DeletedUtc.Value);
            Assert.AreEqual(5UL, tombstone.LogicalVersion);
            Assert.AreEqual(TestData.OriginB, tombstone.OriginInstanceId);
            Assert.AreEqual(active.LastModifiedUtc, tombstone.LastModifiedUtc);
        }

        [TestMethod]
        public void ApproveFailsWithoutChangingPendingWhenLogicalClockIsExhausted()
        {
            Guid pendingId = new Guid(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            ServiceDefinition requested = TestData.Definition();
            var pending = new PendingRegistration(
                pendingId,
                PendingRequestType.New,
                TestData.Utc(1),
                "192.0.2.10",
                requested,
                DirectoryBaseRevision.Capture(null));
            var current = new DirectorySnapshot(
                new ServiceRecord[0],
                new[] { pending },
                ulong.MaxValue);

            ApprovalResult result = RegistrationStateMachine.Approve(
                current,
                pendingId,
                TestData.OriginA,
                TestData.Utc(2));

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(
                DomainErrorCode.LogicalClockExhausted,
                result.ErrorCode.Value);
            Assert.IsFalse(result.StateChanged);
            Assert.IsFalse(result.RequiresPersistence);
            Assert.IsFalse(result.ScheduleSync);
            Assert.AreSame(current, result.NextSnapshot);
            Assert.AreEqual(1, result.NextSnapshot.PendingCount);
        }

        [TestMethod]
        public void DeleteFailsWithoutTombstoneWhenLogicalClockIsExhausted()
        {
            ServiceDefinition definition = TestData.Definition();
            ServiceRecord active = TestData.ActiveRecord(
                definition,
                ulong.MaxValue,
                TestData.OriginA);
            var current = new DirectorySnapshot(
                new[] { active },
                new PendingRegistration[0],
                ulong.MaxValue);

            DeleteResult result = RegistrationStateMachine.Delete(
                current,
                definition.ProductCode,
                TestData.OriginB,
                TestData.Utc(5));

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(
                DomainErrorCode.LogicalClockExhausted,
                result.ErrorCode.Value);
            Assert.IsFalse(result.StateChanged);
            Assert.IsFalse(result.RequiresPersistence);
            Assert.IsFalse(result.ScheduleSync);
            Assert.AreSame(current, result.NextSnapshot);
            ServiceRecord unchanged;
            Assert.IsTrue(
                result.NextSnapshot.TryGetActiveRecord(
                    definition.ProductCode,
                    out unchanged));
            Assert.AreSame(active, unchanged);
        }
    }
}
