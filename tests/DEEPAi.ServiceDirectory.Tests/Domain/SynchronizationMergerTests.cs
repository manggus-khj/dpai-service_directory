using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Synchronization;
using DEEPAi.ServiceDirectory.Tests.TestSupport;

namespace DEEPAi.ServiceDirectory.Tests.Domain
{
    [TestClass]
    public sealed class SynchronizationMergerTests
    {
        [TestMethod]
        public void MergeIsCommutativeForDirectoryRecords()
        {
            ServiceDefinition sharedDefinition = TestData.Definition();
            var left = new DirectorySnapshot(
                new[]
                {
                    TestData.ActiveRecord(sharedDefinition, 1UL, TestData.OriginA),
                    ActiveRecord("CD34", "Left only", 3UL, TestData.OriginA)
                },
                new PendingRegistration[0],
                3UL);
            var right = new DirectorySnapshot(
                new[]
                {
                    TestData.ActiveRecord(sharedDefinition, 2UL, TestData.OriginA),
                    ActiveRecord("EF56", "Right only", 5UL, TestData.OriginB)
                },
                new PendingRegistration[0],
                5UL);

            SynchronizationMergeResult leftThenRight = SynchronizationMerger.Merge(
                left,
                ToSynchronizationSnapshot(right));
            SynchronizationMergeResult rightThenLeft = SynchronizationMerger.Merge(
                right,
                ToSynchronizationSnapshot(left));

            Assert.IsTrue(leftThenRight.IsSuccess);
            Assert.IsTrue(rightThenLeft.IsSuccess);
            AssertSnapshotsEquivalent(
                leftThenRight.NextSnapshot,
                rightThenLeft.NextSnapshot);
        }

        [TestMethod]
        public void MergeIsAssociativeForDirectoryRecords()
        {
            ServiceDefinition sharedDefinition = TestData.Definition();
            var first = new DirectorySnapshot(
                new[] { TestData.ActiveRecord(sharedDefinition, 1UL, TestData.OriginA) },
                new PendingRegistration[0],
                1UL);
            var second = new DirectorySnapshot(
                new[]
                {
                    TestData.ActiveRecord(sharedDefinition, 2UL, TestData.OriginA),
                    ActiveRecord("CD34", "Second only", 2UL, TestData.OriginB)
                },
                new PendingRegistration[0],
                2UL);
            var third = new DirectorySnapshot(
                new[]
                {
                    TestData.ActiveRecord(sharedDefinition, 2UL, TestData.OriginB),
                    ActiveRecord("EF56", "Third only", 4UL, TestData.OriginA)
                },
                new PendingRegistration[0],
                4UL);

            SynchronizationMergeResult firstSecond = SynchronizationMerger.Merge(
                first,
                ToSynchronizationSnapshot(second));
            SynchronizationMergeResult leftAssociated = SynchronizationMerger.Merge(
                firstSecond.NextSnapshot,
                ToSynchronizationSnapshot(third));
            SynchronizationMergeResult secondThird = SynchronizationMerger.Merge(
                second,
                ToSynchronizationSnapshot(third));
            SynchronizationMergeResult rightAssociated = SynchronizationMerger.Merge(
                first,
                ToSynchronizationSnapshot(secondThird.NextSnapshot));

            Assert.IsTrue(firstSecond.IsSuccess);
            Assert.IsTrue(leftAssociated.IsSuccess);
            Assert.IsTrue(secondThird.IsSuccess);
            Assert.IsTrue(rightAssociated.IsSuccess);
            AssertSnapshotsEquivalent(
                leftAssociated.NextSnapshot,
                rightAssociated.NextSnapshot);
        }

        [TestMethod]
        public void MergeLawsHoldForSmallDeterministicStateSet()
        {
            DirectorySnapshot[] states = CreateSmallMergeStateSet();
            foreach (DirectorySnapshot first in states)
            {
                SynchronizationMergeResult idempotent =
                    SynchronizationMerger.Merge(
                        first,
                        ToSynchronizationSnapshot(first));
                Assert.IsTrue(idempotent.IsSuccess);
                Assert.IsFalse(idempotent.StateChanged);
                AssertSnapshotsEquivalent(first, idempotent.NextSnapshot);

                foreach (DirectorySnapshot second in states)
                {
                    SynchronizationMergeResult firstSecond =
                        SynchronizationMerger.Merge(
                            first,
                            ToSynchronizationSnapshot(second));
                    SynchronizationMergeResult secondFirst =
                        SynchronizationMerger.Merge(
                            second,
                            ToSynchronizationSnapshot(first));
                    Assert.IsTrue(firstSecond.IsSuccess);
                    Assert.IsTrue(secondFirst.IsSuccess);
                    AssertSnapshotsEquivalent(
                        firstSecond.NextSnapshot,
                        secondFirst.NextSnapshot);

                    foreach (DirectorySnapshot third in states)
                    {
                        SynchronizationMergeResult leftAssociated =
                            SynchronizationMerger.Merge(
                                firstSecond.NextSnapshot,
                                ToSynchronizationSnapshot(third));
                        SynchronizationMergeResult secondThird =
                            SynchronizationMerger.Merge(
                                second,
                                ToSynchronizationSnapshot(third));
                        SynchronizationMergeResult rightAssociated =
                            SynchronizationMerger.Merge(
                                first,
                                ToSynchronizationSnapshot(
                                    secondThird.NextSnapshot));

                        Assert.IsTrue(leftAssociated.IsSuccess);
                        Assert.IsTrue(secondThird.IsSuccess);
                        Assert.IsTrue(rightAssociated.IsSuccess);
                        AssertSnapshotsEquivalent(
                            leftAssociated.NextSnapshot,
                            rightAssociated.NextSnapshot);
                    }
                }
            }
        }

        [TestMethod]
        public void MergeIsIdempotentForAnAlreadyObservedSnapshot()
        {
            var current = new DirectorySnapshot(
                new[] { ActiveRecord("AB12", "Current", 3UL, TestData.OriginA) },
                new PendingRegistration[0],
                4UL);

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                ToSynchronizationSnapshot(current));

            Assert.IsTrue(result.IsSuccess);
            Assert.IsFalse(result.StateChanged);
            Assert.IsFalse(result.RequiresPersistence);
            Assert.AreSame(current, result.NextSnapshot);
        }

        [TestMethod]
        public void MergeIsIdempotentForDistinctEquivalentRecords()
        {
            ServiceRecord localRecord = ActiveRecord(
                "AB12",
                "Equivalent",
                3UL,
                TestData.OriginA);
            ServiceRecord remoteRecord = ActiveRecord(
                "AB12",
                "Equivalent",
                3UL,
                TestData.OriginA);
            var current = new DirectorySnapshot(
                new[] { localRecord },
                new PendingRegistration[0],
                3UL);

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                new SynchronizationSnapshot(
                    new[] { remoteRecord },
                    3UL));

            Assert.AreNotSame(localRecord, remoteRecord);
            Assert.IsTrue(localRecord.Equals(remoteRecord));
            Assert.IsTrue(result.IsSuccess);
            Assert.IsFalse(result.StateChanged);
            Assert.IsFalse(result.RequiresPersistence);
            Assert.AreSame(current, result.NextSnapshot);
        }

        [TestMethod]
        public void MergeUsesCanonicalOriginAsConcurrentRevisionTieBreaker()
        {
            ServiceDefinition definition = TestData.Definition();
            ServiceRecord lowerOrigin = TestData.ActiveRecord(
                definition,
                7UL,
                TestData.OriginA);
            ServiceRecord higherOrigin = TestData.ActiveRecord(
                definition,
                7UL,
                TestData.OriginB);
            var current = new DirectorySnapshot(
                new[] { lowerOrigin },
                new PendingRegistration[0],
                7UL);
            var remote = new SynchronizationSnapshot(
                new[] { higherOrigin },
                7UL);

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                remote);

            Assert.IsTrue(result.IsSuccess);
            ServiceRecord winner;
            Assert.IsTrue(
                result.NextSnapshot.TryGetRecord(
                    definition.ProductCode,
                    out winner));
            Assert.AreSame(higherOrigin, winner);

            var higherOriginCurrent = new DirectorySnapshot(
                new[] { higherOrigin },
                new PendingRegistration[0],
                7UL);
            SynchronizationMergeResult reversed = SynchronizationMerger.Merge(
                higherOriginCurrent,
                new SynchronizationSnapshot(new[] { lowerOrigin }, 7UL));

            Assert.IsTrue(reversed.IsSuccess);
            Assert.IsFalse(reversed.StateChanged);
            Assert.AreSame(higherOriginCurrent, reversed.NextSnapshot);
        }

        [TestMethod]
        public void MergeUsesRevisionOrderingForTombstonesAndPreventsResurrection()
        {
            ServiceRecord approved = ActiveRecord(
                "AB12",
                "Approved",
                5UL,
                TestData.OriginA);
            ServiceRecord newerTombstone = DeletedRecord(
                "AB12",
                "Approved",
                6UL,
                TestData.OriginA);
            var activeCurrent = new DirectorySnapshot(
                new[] { approved },
                new PendingRegistration[0],
                5UL);

            SynchronizationMergeResult deleted = SynchronizationMerger.Merge(
                activeCurrent,
                new SynchronizationSnapshot(
                    new[] { newerTombstone },
                    6UL));

            Assert.IsTrue(deleted.IsSuccess);
            Assert.IsTrue(deleted.StateChanged);
            Assert.AreSame(
                newerTombstone,
                GetRecord(deleted.NextSnapshot, "AB12"));

            var tombstoneCurrent = new DirectorySnapshot(
                new[] { newerTombstone },
                new PendingRegistration[0],
                6UL);
            SynchronizationMergeResult noResurrection =
                SynchronizationMerger.Merge(
                    tombstoneCurrent,
                    new SynchronizationSnapshot(
                        new[] { approved },
                        5UL));

            Assert.IsTrue(noResurrection.IsSuccess);
            Assert.IsFalse(noResurrection.StateChanged);
            Assert.AreSame(tombstoneCurrent, noResurrection.NextSnapshot);
            Assert.IsTrue(GetRecord(
                noResurrection.NextSnapshot,
                "AB12").Deleted);
        }

        [TestMethod]
        public void MergeUsesOriginTieBreakerForConcurrentActiveAndTombstone()
        {
            ServiceRecord active = ActiveRecord(
                "AB12",
                "Concurrent",
                7UL,
                TestData.OriginA);
            ServiceRecord tombstone = DeletedRecord(
                "AB12",
                "Concurrent",
                7UL,
                TestData.OriginB);
            var current = new DirectorySnapshot(
                new[] { active },
                new PendingRegistration[0],
                7UL);

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                new SynchronizationSnapshot(
                    new[] { tombstone },
                    7UL));

            Assert.IsTrue(result.IsSuccess);
            Assert.IsTrue(result.StateChanged);
            Assert.AreSame(tombstone, GetRecord(result.NextSnapshot, "AB12"));
            Assert.IsTrue(GetRecord(result.NextSnapshot, "AB12").Deleted);
        }

        [TestMethod]
        public void MergePropagatesOneSidedTombstone()
        {
            ServiceRecord tombstone = DeletedRecord(
                "AB12",
                "Deleted remotely",
                2UL,
                TestData.OriginA);
            DirectorySnapshot current = DirectorySnapshot.Empty();

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                new SynchronizationSnapshot(
                    new[] { tombstone },
                    2UL));

            Assert.IsTrue(result.IsSuccess);
            Assert.IsTrue(result.StateChanged);
            Assert.AreEqual(0, result.NextSnapshot.ActiveCount);
            Assert.AreEqual(1, result.NextSnapshot.Records.Count);
            Assert.AreSame(tombstone, GetRecord(result.NextSnapshot, "AB12"));
        }

        [TestMethod]
        public void MergeRejectsRevisionCollisionWithoutObservingRemoteState()
        {
            ServiceRecord currentRecord = ActiveRecord(
                "AB12",
                "Current payload",
                7UL,
                TestData.OriginA);
            var current = new DirectorySnapshot(
                new[] { currentRecord },
                new PendingRegistration[0],
                7UL);
            ServiceRecord collidingRecord = ActiveRecord(
                "AB12",
                "Different payload",
                7UL,
                TestData.OriginA);
            var remote = new SynchronizationSnapshot(
                new[]
                {
                    ActiveRecord("CD34", "Remote addition", 8UL, TestData.OriginB),
                    collidingRecord
                },
                9UL);

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                remote);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(
                DomainErrorCode.RevisionCollision,
                result.ErrorCode.Value);
            Assert.IsFalse(result.StateChanged);
            Assert.IsFalse(result.RequiresPersistence);
            Assert.AreSame(current, result.NextSnapshot);
            Assert.AreEqual(7UL, current.LogicalClock);
            Assert.AreEqual(1, current.Records.Count);
        }

        [TestMethod]
        public void MergeRejectsActiveTombstonePayloadCollisionAtSameRevision()
        {
            ServiceRecord active = ActiveRecord(
                "AB12",
                "Same revision",
                7UL,
                TestData.OriginA);
            ServiceRecord tombstone = DeletedRecord(
                "AB12",
                "Same revision",
                7UL,
                TestData.OriginA);
            var current = new DirectorySnapshot(
                new[] { active },
                new PendingRegistration[0],
                7UL);

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                new SynchronizationSnapshot(
                    new[] { tombstone },
                    7UL));

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(
                DomainErrorCode.RevisionCollision,
                result.ErrorCode.Value);
            Assert.IsFalse(result.StateChanged);
            Assert.IsFalse(result.RequiresPersistence);
            Assert.AreSame(current, result.NextSnapshot);
        }

        [TestMethod]
        public void MergeRejectsCandidateCollisionWithPendingBaseRevision()
        {
            ServiceRecord baseRecord = ActiveRecord(
                "AB12",
                "Captured base",
                5UL,
                TestData.OriginA);
            var pending = new PendingRegistration(
                new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                PendingRequestType.Modify,
                TestData.Utc(1),
                "192.0.2.10",
                TestData.Definition(
                    name: "Pending update",
                    productCode: "AB12"),
                DirectoryBaseRevision.Capture(baseRecord));
            var current = new DirectorySnapshot(
                new ServiceRecord[0],
                new[] { pending },
                5UL);
            ServiceRecord collidingRemote = ActiveRecord(
                "AB12",
                "Corrupt peer payload",
                5UL,
                TestData.OriginA);

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                new SynchronizationSnapshot(
                    new[] { collidingRemote },
                    8UL));

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(
                DomainErrorCode.RevisionCollision,
                result.ErrorCode.Value);
            Assert.IsFalse(result.StateChanged);
            Assert.IsFalse(result.RequiresPersistence);
            Assert.AreSame(current, result.NextSnapshot);
            Assert.AreEqual(5UL, current.LogicalClock);
            Assert.AreEqual(0, current.Records.Count);
            Assert.AreEqual(1, current.PendingCount);
        }

        [TestMethod]
        public void MergeAcceptsCandidateMatchingPendingBaseRevision()
        {
            ServiceRecord baseRecord = ActiveRecord(
                "AB12",
                "Captured base",
                5UL,
                TestData.OriginA);
            var pending = new PendingRegistration(
                new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                PendingRequestType.Modify,
                TestData.Utc(1),
                "192.0.2.10",
                TestData.Definition(
                    name: "Pending update",
                    productCode: "AB12"),
                DirectoryBaseRevision.Capture(baseRecord));
            var current = new DirectorySnapshot(
                new ServiceRecord[0],
                new[] { pending },
                5UL);
            ServiceRecord matchingRemote = ActiveRecord(
                "AB12",
                "Captured base",
                5UL,
                TestData.OriginA);

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                new SynchronizationSnapshot(
                    new[] { matchingRemote },
                    8UL));

            Assert.IsTrue(result.IsSuccess);
            Assert.IsTrue(result.StateChanged);
            Assert.IsTrue(result.RequiresPersistence);
            Assert.AreSame(
                matchingRemote,
                GetRecord(result.NextSnapshot, "AB12"));
            Assert.AreEqual(1, result.NextSnapshot.PendingCount);
            Assert.AreEqual(8UL, result.NextSnapshot.LogicalClock);
        }

        [TestMethod]
        public void MergeRejectsCapacityOverflowWithoutChangingOriginalSnapshot()
        {
            List<ServiceRecord> localRecords = ActiveRecords(0, 600);
            List<ServiceRecord> remoteRecords = ActiveRecords(600, 500);
            var current = new DirectorySnapshot(
                localRecords,
                new PendingRegistration[0],
                1UL);
            var remote = new SynchronizationSnapshot(remoteRecords, 10UL);

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                remote);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(
                DomainErrorCode.DirectoryCapacity,
                result.ErrorCode.Value);
            Assert.IsFalse(result.StateChanged);
            Assert.IsFalse(result.RequiresPersistence);
            Assert.AreSame(current, result.NextSnapshot);
            Assert.AreEqual(600, current.ActiveCount);
            Assert.AreEqual(1UL, current.LogicalClock);
        }

        [TestMethod]
        public void MergeAllowsExactlyOneThousandActiveServicesAndExtraTombstones()
        {
            List<ServiceRecord> localRecords = ActiveRecords(0, 999);
            var remoteRecords = new List<ServiceRecord>
            {
                ActiveRecord(
                    ProductCodeFor(999),
                    "Boundary service",
                    1UL,
                    TestData.OriginB),
                DeletedRecord(
                    ProductCodeFor(1000),
                    "Boundary tombstone",
                    2UL,
                    TestData.OriginA)
            };
            var current = new DirectorySnapshot(
                localRecords,
                new PendingRegistration[0],
                1UL);

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                new SynchronizationSnapshot(remoteRecords, 2UL));

            Assert.IsTrue(result.IsSuccess);
            Assert.IsTrue(result.StateChanged);
            Assert.IsTrue(result.RequiresPersistence);
            Assert.AreEqual(1000, result.NextSnapshot.ActiveCount);
            Assert.AreEqual(1001, result.NextSnapshot.Records.Count);
            Assert.IsTrue(GetRecord(
                result.NextSnapshot,
                ProductCodeFor(1000)).Deleted);
            Assert.AreEqual(2UL, result.NextSnapshot.LogicalClock);
        }

        [TestMethod]
        public void MergeObservesRemoteLogicalClockEvenWhenNoRecordWins()
        {
            ServiceRecord currentRecord = ActiveRecord(
                "AB12",
                "Current",
                4UL,
                TestData.OriginB);
            var current = new DirectorySnapshot(
                new[] { currentRecord },
                new PendingRegistration[0],
                5UL);
            var remote = new SynchronizationSnapshot(
                new ServiceRecord[0],
                12UL);

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                remote);

            Assert.IsTrue(result.IsSuccess);
            Assert.IsTrue(result.StateChanged);
            Assert.IsTrue(result.RequiresPersistence);
            Assert.AreEqual(12UL, result.NextSnapshot.LogicalClock);
            ServiceRecord retained;
            Assert.IsTrue(
                result.NextSnapshot.TryGetRecord(
                    currentRecord.Definition.ProductCode,
                    out retained));
            Assert.AreSame(currentRecord, retained);
        }

        [TestMethod]
        public void MergeDoesNotRegressLogicalClockForOlderRemoteSnapshot()
        {
            var current = new DirectorySnapshot(
                new ServiceRecord[0],
                new PendingRegistration[0],
                12UL);

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                new SynchronizationSnapshot(
                    new ServiceRecord[0],
                    5UL));

            Assert.IsTrue(result.IsSuccess);
            Assert.IsFalse(result.StateChanged);
            Assert.IsFalse(result.RequiresPersistence);
            Assert.AreSame(current, result.NextSnapshot);
            Assert.AreEqual(12UL, result.NextSnapshot.LogicalClock);
        }

        [TestMethod]
        public void MergeObservesMaximumRemoteLogicalClockWithoutWrapping()
        {
            DirectorySnapshot current = DirectorySnapshot.Empty();

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                new SynchronizationSnapshot(
                    new ServiceRecord[0],
                    ulong.MaxValue));

            Assert.IsTrue(result.IsSuccess);
            Assert.IsTrue(result.StateChanged);
            Assert.IsTrue(result.RequiresPersistence);
            Assert.AreEqual(
                ulong.MaxValue,
                result.NextSnapshot.LogicalClock);
        }

        [TestMethod]
        public void SynchronizationSnapshotEnforcesRecordAndClockBoundaries()
        {
            ServiceRecord atClock = ActiveRecord(
                "AB12",
                "At clock",
                5UL,
                TestData.OriginA);
            var valid = new SynchronizationSnapshot(
                new[] { atClock },
                5UL);

            Assert.AreEqual(5UL, valid.LogicalClock);
            Assert.AreEqual(1, valid.Records.Count);
            Assert.ThrowsExactly<ArgumentNullException>(
                () => new SynchronizationSnapshot(null, 0UL));
            Assert.ThrowsExactly<ArgumentException>(
                () => new SynchronizationSnapshot(
                    new[] { atClock },
                    4UL));

            ServiceRecord duplicateProduct = ActiveRecord(
                "AB12",
                "Duplicate",
                4UL,
                TestData.OriginB);
            Assert.ThrowsExactly<ArgumentException>(
                () => new SynchronizationSnapshot(
                    new[] { atClock, duplicateProduct },
                    5UL));
            Assert.ThrowsExactly<ArgumentException>(
                () => new SynchronizationSnapshot(
                    new ServiceRecord[] { atClock, null },
                    5UL));
        }

        [TestMethod]
        public void MergePreservesLocalPendingRegistrations()
        {
            ServiceRecord baseRecord = ActiveRecord(
                "AB12",
                "Approved value",
                5UL,
                TestData.OriginA);
            ServiceDefinition requested = TestData.Definition(
                name: "Pending value",
                productCode: "AB12",
                serverAddress: "10.20.30.41");
            Guid pendingId = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var pending = new PendingRegistration(
                pendingId,
                PendingRequestType.Modify,
                TestData.Utc(1),
                "192.0.2.10",
                requested,
                DirectoryBaseRevision.Capture(baseRecord));
            var current = new DirectorySnapshot(
                new[] { baseRecord },
                new[] { pending },
                5UL);
            ServiceRecord remoteWinner = ActiveRecord(
                "AB12",
                "Peer value",
                6UL,
                TestData.OriginB);
            var remote = new SynchronizationSnapshot(
                new[] { remoteWinner },
                8UL);

            SynchronizationMergeResult result = SynchronizationMerger.Merge(
                current,
                remote);

            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual(1, result.NextSnapshot.PendingCount);
            PendingRegistration preserved;
            Assert.IsTrue(result.NextSnapshot.TryGetPending(pendingId, out preserved));
            Assert.AreSame(pending, preserved);
            ServiceRecord winner;
            Assert.IsTrue(
                result.NextSnapshot.TryGetRecord(
                    baseRecord.Definition.ProductCode,
                    out winner));
            Assert.AreSame(remoteWinner, winner);
            Assert.AreEqual(8UL, result.NextSnapshot.LogicalClock);
        }

        private static SynchronizationSnapshot ToSynchronizationSnapshot(
            DirectorySnapshot snapshot)
        {
            return new SynchronizationSnapshot(
                snapshot.Records.Values,
                snapshot.LogicalClock);
        }

        private static ServiceRecord ActiveRecord(
            string productCode,
            string name,
            ulong logicalVersion,
            Guid originInstanceId)
        {
            return TestData.ActiveRecord(
                TestData.Definition(name: name, productCode: productCode),
                logicalVersion,
                originInstanceId);
        }

        private static ServiceRecord DeletedRecord(
            string productCode,
            string name,
            ulong logicalVersion,
            Guid originInstanceId)
        {
            ServiceRecord active = ActiveRecord(
                productCode,
                name,
                1UL,
                originInstanceId);
            return active.MarkDeleted(
                TestData.Utc(2),
                logicalVersion,
                originInstanceId);
        }

        private static DirectorySnapshot[] CreateSmallMergeStateSet()
        {
            ServiceRecord activeFromOriginA = ActiveRecord(
                "AB12",
                "Property state",
                1UL,
                TestData.OriginA);
            ServiceRecord activeFromOriginB = ActiveRecord(
                "AB12",
                "Property state",
                1UL,
                TestData.OriginB);
            ServiceRecord tombstone = DeletedRecord(
                "AB12",
                "Property state",
                2UL,
                TestData.OriginA);
            return new[]
            {
                DirectorySnapshot.Empty(),
                new DirectorySnapshot(
                    new[] { activeFromOriginA },
                    new PendingRegistration[0],
                    1UL),
                new DirectorySnapshot(
                    new[] { activeFromOriginB },
                    new PendingRegistration[0],
                    1UL),
                new DirectorySnapshot(
                    new[] { tombstone },
                    new PendingRegistration[0],
                    2UL)
            };
        }

        private static ServiceRecord GetRecord(
            DirectorySnapshot snapshot,
            string productCode)
        {
            ServiceRecord record;
            Assert.IsTrue(
                snapshot.TryGetRecord(
                    TestData.ProductCode(productCode),
                    out record),
                "The snapshot is missing " + productCode + ".");
            return record;
        }

        private static List<ServiceRecord> ActiveRecords(int start, int count)
        {
            var records = new List<ServiceRecord>(count);
            for (int offset = 0; offset < count; offset++)
            {
                int value = start + offset;
                records.Add(ActiveRecord(
                    ProductCodeFor(value),
                    "Service " + value,
                    1UL,
                    TestData.OriginA));
            }

            return records;
        }

        private static string ProductCodeFor(int value)
        {
            const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            if (value < 0 || value >= 36 * 36 * 36 * 36)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            var characters = new char[4];
            for (int index = characters.Length - 1; index >= 0; index--)
            {
                characters[index] = alphabet[value % alphabet.Length];
                value /= alphabet.Length;
            }

            return new string(characters);
        }

        private static void AssertSnapshotsEquivalent(
            DirectorySnapshot expected,
            DirectorySnapshot actual)
        {
            Assert.AreEqual(expected.LogicalClock, actual.LogicalClock);
            Assert.AreEqual(expected.ActiveCount, actual.ActiveCount);
            Assert.AreEqual(expected.PendingCount, actual.PendingCount);
            Assert.AreEqual(expected.Records.Count, actual.Records.Count);
            foreach (KeyValuePair<ProductCode, ServiceRecord> expectedEntry in expected.Records)
            {
                ServiceRecord actualRecord;
                Assert.IsTrue(
                    actual.TryGetRecord(expectedEntry.Key, out actualRecord),
                    "The merged snapshot is missing " + expectedEntry.Key + ".");
                Assert.IsTrue(
                    expectedEntry.Value.Equals(actualRecord),
                    "The merged record differs for " + expectedEntry.Key + ".");
            }
        }
    }
}
