using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DEEPAi.ServiceDirectory.Application.Synchronization;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Synchronization;
using DEEPAi.ServiceDirectory.Tests.TestSupport;

namespace DEEPAi.ServiceDirectory.Tests.Application
{
    [TestClass]
    public sealed class InboundSynchronizationStagingTests
    {
        private static readonly Guid SnapshotA =
            new Guid("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        private static readonly Guid SnapshotB =
            new Guid("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

        [TestMethod]
        public void SingleBatchCompletesAndExposesExactSnapshot()
        {
            ServiceRecord record = ActiveRecord(1, 5UL);
            var accumulator = new InboundSynchronizationStagingAccumulator();

            InboundSynchronizationStagingResult result = accumulator.Append(
                Batch(SnapshotA, 5UL, 0U, 1UL, true, record));

            Assert.IsTrue(result.IsAccepted);
            Assert.IsTrue(result.IsCompleted);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Completed,
                accumulator.State);
            Assert.IsTrue(accumulator.TryGetCompletedSnapshot(
                out Guid snapshotId,
                out SynchronizationSnapshot snapshot));
            Assert.AreEqual(SnapshotA, snapshotId);
            Assert.AreEqual(5UL, snapshot.LogicalClock);
            Assert.AreEqual(1, snapshot.Records.Count);
            Assert.AreSame(
                record,
                snapshot.Records[record.Definition.ProductCode]);
        }

        [TestMethod]
        public void PartialBatchDoesNotExposeSnapshotBeforeExactLastBatch()
        {
            ServiceRecord firstRecord = ActiveRecord(1, 3UL);
            ServiceRecord secondRecord = ActiveRecord(2, 4UL);
            var accumulator = new InboundSynchronizationStagingAccumulator();

            InboundSynchronizationStagingResult first = accumulator.Append(
                Batch(SnapshotA, 4UL, 0U, 2UL, false, firstRecord));

            Assert.IsTrue(first.IsAccepted);
            Assert.IsFalse(first.IsCompleted);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Collecting,
                accumulator.State);
            Assert.IsFalse(accumulator.TryGetCompletedSnapshot(
                out Guid partialId,
                out SynchronizationSnapshot partialSnapshot));
            Assert.AreEqual(Guid.Empty, partialId);
            Assert.IsNull(partialSnapshot);

            InboundSynchronizationStagingResult second = accumulator.Append(
                Batch(SnapshotA, 4UL, 1U, 2UL, true, secondRecord));

            Assert.IsTrue(second.IsAccepted);
            Assert.IsTrue(second.IsCompleted);
            Assert.IsTrue(accumulator.TryGetCompletedSnapshot(
                out Guid completedId,
                out SynchronizationSnapshot completedSnapshot));
            Assert.AreEqual(SnapshotA, completedId);
            Assert.AreEqual(4UL, completedSnapshot.LogicalClock);
            Assert.AreEqual(2, completedSnapshot.Records.Count);
        }

        [TestMethod]
        public void EmptySnapshotRequiresAndAcceptsOneLastBatch()
        {
            var accumulator = new InboundSynchronizationStagingAccumulator();

            InboundSynchronizationStagingResult result = accumulator.Append(
                Batch(
                    SnapshotA,
                    0UL,
                    0U,
                    0UL,
                    true,
                    new ServiceRecord[0]));

            Assert.IsTrue(result.IsAccepted);
            Assert.IsTrue(result.IsCompleted);
            Assert.IsTrue(accumulator.TryGetCompletedSnapshot(
                out _,
                out SynchronizationSnapshot snapshot));
            Assert.AreEqual(0UL, snapshot.LogicalClock);
            Assert.AreEqual(0, snapshot.Records.Count);
        }

        [TestMethod]
        public void FirstBatchIndexMustBeZero()
        {
            AssertRejectedAndDiscarded(
                Batch(SnapshotA, 1UL, 1U, 1UL, true, ActiveRecord(1, 1UL)),
                InboundSynchronizationStagingError.BatchIndexMismatch);
        }

        [TestMethod]
        public void SnapshotIdMustNotBeEmpty()
        {
            AssertRejectedAndDiscarded(
                Batch(Guid.Empty, 1UL, 0U, 1UL, true, ActiveRecord(1, 1UL)),
                InboundSynchronizationStagingError.InvalidSnapshotId);
        }

        [TestMethod]
        public void BatchIndexesMustBeContinuous()
        {
            var accumulator = StartedAccumulator();

            InboundSynchronizationStagingResult result = accumulator.Append(
                Batch(SnapshotA, 2UL, 2U, 2UL, true, ActiveRecord(2, 2UL)));

            AssertDiscarded(
                accumulator,
                result,
                InboundSynchronizationStagingError.BatchIndexMismatch);
        }

        [TestMethod]
        public void SnapshotIdMustRemainConstantAcrossBatches()
        {
            var accumulator = StartedAccumulator();

            InboundSynchronizationStagingResult result = accumulator.Append(
                Batch(SnapshotB, 2UL, 1U, 2UL, true, ActiveRecord(2, 2UL)));

            AssertDiscarded(
                accumulator,
                result,
                InboundSynchronizationStagingError.SnapshotIdMismatch);
        }

        [TestMethod]
        public void LogicalClockMustRemainConstantAcrossBatches()
        {
            var accumulator = StartedAccumulator();

            InboundSynchronizationStagingResult result = accumulator.Append(
                Batch(SnapshotA, 3UL, 1U, 2UL, true, ActiveRecord(2, 2UL)));

            AssertDiscarded(
                accumulator,
                result,
                InboundSynchronizationStagingError.LogicalClockMismatch);
        }

        [TestMethod]
        public void TotalCountMustRemainConstantAcrossBatches()
        {
            var accumulator = StartedAccumulator();

            InboundSynchronizationStagingResult result = accumulator.Append(
                Batch(SnapshotA, 2UL, 1U, 3UL, false, ActiveRecord(2, 2UL)));

            AssertDiscarded(
                accumulator,
                result,
                InboundSynchronizationStagingError.TotalCountMismatch);
        }

        [TestMethod]
        public void BatchCannotContainMoreThanOneThousandRecords()
        {
            List<ServiceRecord> records = Records(1001, false);
            var accumulator = new InboundSynchronizationStagingAccumulator();

            InboundSynchronizationStagingResult result = accumulator.Append(
                new InboundSynchronizationBatch(
                    SnapshotA,
                    1001UL,
                    0U,
                    1001UL,
                    true,
                    records));

            AssertDiscarded(
                accumulator,
                result,
                InboundSynchronizationStagingError.BatchSizeExceeded);
        }

        [TestMethod]
        public void NullRecordDiscardsEntireStagingSnapshot()
        {
            AssertRejectedAndDiscarded(
                Batch(SnapshotA, 1UL, 0U, 1UL, true, (ServiceRecord)null),
                InboundSynchronizationStagingError.NullRecord);
        }

        [TestMethod]
        public void RecordVersionCannotExceedSnapshotClock()
        {
            AssertRejectedAndDiscarded(
                Batch(SnapshotA, 4UL, 0U, 1UL, true, ActiveRecord(1, 5UL)),
                InboundSynchronizationStagingError
                    .RecordVersionExceedsLogicalClock);
        }

        [TestMethod]
        public void DuplicateProductCodeAcrossBatchesDiscardsPartialSnapshot()
        {
            ServiceRecord first = ActiveRecord(1, 1UL);
            ServiceRecord duplicate = ActiveRecord(1, 2UL);
            var accumulator = new InboundSynchronizationStagingAccumulator();
            InboundSynchronizationStagingResult accepted = accumulator.Append(
                Batch(SnapshotA, 2UL, 0U, 2UL, false, first));
            Assert.IsTrue(accepted.IsAccepted);

            InboundSynchronizationStagingResult result = accumulator.Append(
                Batch(SnapshotA, 2UL, 1U, 2UL, true, duplicate));

            AssertDiscarded(
                accumulator,
                result,
                InboundSynchronizationStagingError.DuplicateProductCode);
        }

        [TestMethod]
        public void DuplicateProductCodeWithinBatchIsRejected()
        {
            AssertRejectedAndDiscarded(
                Batch(
                    SnapshotA,
                    2UL,
                    0U,
                    2UL,
                    true,
                    ActiveRecord(1, 1UL),
                    ActiveRecord(1, 2UL)),
                InboundSynchronizationStagingError.DuplicateProductCode);
        }

        [TestMethod]
        public void ProductCodesMustBeStrictlyAscendingWithinBatch()
        {
            AssertRejectedAndDiscarded(
                Batch(
                    SnapshotA,
                    2UL,
                    0U,
                    2UL,
                    true,
                    ActiveRecord(2, 2UL),
                    ActiveRecord(1, 1UL)),
                InboundSynchronizationStagingError
                    .ProductCodeOrderMismatch);
        }

        [TestMethod]
        public void ProductCodesMustRemainAscendingAcrossBatchBoundary()
        {
            var accumulator = new InboundSynchronizationStagingAccumulator();
            InboundSynchronizationStagingResult first = accumulator.Append(
                Batch(
                    SnapshotA,
                    2UL,
                    0U,
                    2UL,
                    false,
                    ActiveRecord(2, 2UL)));
            Assert.IsTrue(first.IsAccepted);

            InboundSynchronizationStagingResult result = accumulator.Append(
                Batch(
                    SnapshotA,
                    2UL,
                    1U,
                    2UL,
                    true,
                    ActiveRecord(1, 1UL)));

            AssertDiscarded(
                accumulator,
                result,
                InboundSynchronizationStagingError
                    .ProductCodeOrderMismatch);
        }

        [TestMethod]
        public void LastBatchCannotBeMarkedBeforeTotalCountIsReceived()
        {
            AssertRejectedAndDiscarded(
                Batch(SnapshotA, 2UL, 0U, 2UL, true, ActiveRecord(1, 1UL)),
                InboundSynchronizationStagingError.LastBatchMismatch);
        }

        [TestMethod]
        public void ExactTotalCountMustBeMarkedAsLastBatch()
        {
            AssertRejectedAndDiscarded(
                Batch(SnapshotA, 1UL, 0U, 1UL, false, ActiveRecord(1, 1UL)),
                InboundSynchronizationStagingError.LastBatchMismatch);
        }

        [TestMethod]
        public void BatchCannotExceedDeclaredTotalCount()
        {
            AssertRejectedAndDiscarded(
                Batch(SnapshotA, 1UL, 0U, 0UL, true, ActiveRecord(1, 1UL)),
                InboundSynchronizationStagingError.RecordCountExceedsTotal);
        }

        [TestMethod]
        public void RejectedStagingCannotBeReused()
        {
            var accumulator = new InboundSynchronizationStagingAccumulator();
            InboundSynchronizationStagingResult rejected = accumulator.Append(
                Batch(SnapshotA, 1UL, 1U, 1UL, true, ActiveRecord(1, 1UL)));
            Assert.IsFalse(rejected.IsAccepted);

            InboundSynchronizationStagingResult retry = accumulator.Append(
                Batch(SnapshotA, 1UL, 0U, 1UL, true, ActiveRecord(1, 1UL)));

            Assert.IsFalse(retry.IsAccepted);
            Assert.AreEqual(
                InboundSynchronizationStagingError.StagingNotCollecting,
                retry.Error);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Discarded,
                accumulator.State);
            Assert.IsFalse(accumulator.TryGetCompletedSnapshot(out _, out _));
        }

        [TestMethod]
        public void ExplicitDiscardRemovesPartialStagingSnapshot()
        {
            var accumulator = StartedAccumulator();

            accumulator.Discard();

            Assert.AreEqual(
                InboundSynchronizationStagingState.Discarded,
                accumulator.State);
            Assert.IsFalse(accumulator.TryGetCompletedSnapshot(out _, out _));
        }

        [TestMethod]
        public void OverallTombstoneCountIsNotArbitrarilyCappedAtOneThousand()
        {
            List<ServiceRecord> tombstones = Records(1001, true);
            var firstBatch = tombstones.GetRange(0, 1000);
            var secondBatch = tombstones.GetRange(1000, 1);
            var accumulator = new InboundSynchronizationStagingAccumulator();

            InboundSynchronizationStagingResult first = accumulator.Append(
                new InboundSynchronizationBatch(
                    SnapshotA,
                    1001UL,
                    0U,
                    1001UL,
                    false,
                    firstBatch));
            InboundSynchronizationStagingResult second = accumulator.Append(
                new InboundSynchronizationBatch(
                    SnapshotA,
                    1001UL,
                    1U,
                    1001UL,
                    true,
                    secondBatch));

            Assert.IsTrue(first.IsAccepted);
            Assert.IsFalse(first.IsCompleted);
            Assert.IsTrue(second.IsAccepted);
            Assert.IsTrue(second.IsCompleted);
            Assert.IsTrue(accumulator.TryGetCompletedSnapshot(
                out _,
                out SynchronizationSnapshot snapshot));
            Assert.AreEqual(1001, snapshot.Records.Count);
            foreach (ServiceRecord record in snapshot.Records.Values)
            {
                Assert.IsTrue(record.Deleted);
            }
        }

        [TestMethod]
        public void AdditionalBatchAfterCompletionDiscardsCompletedStagingResult()
        {
            var accumulator = new InboundSynchronizationStagingAccumulator();
            InboundSynchronizationStagingResult completed = accumulator.Append(
                Batch(SnapshotA, 1UL, 0U, 1UL, true, ActiveRecord(1, 1UL)));
            Assert.IsTrue(completed.IsCompleted);

            InboundSynchronizationStagingResult extra = accumulator.Append(
                Batch(SnapshotA, 2UL, 1U, 2UL, true, ActiveRecord(2, 2UL)));

            Assert.IsFalse(extra.IsAccepted);
            Assert.AreEqual(
                InboundSynchronizationStagingError.StagingNotCollecting,
                extra.Error);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Discarded,
                accumulator.State);
            Assert.IsFalse(accumulator.TryGetCompletedSnapshot(out _, out _));
        }

        private static InboundSynchronizationStagingAccumulator
            StartedAccumulator()
        {
            var accumulator = new InboundSynchronizationStagingAccumulator();
            InboundSynchronizationStagingResult result = accumulator.Append(
                Batch(SnapshotA, 2UL, 0U, 2UL, false, ActiveRecord(1, 1UL)));
            Assert.IsTrue(result.IsAccepted);
            Assert.IsFalse(result.IsCompleted);
            return accumulator;
        }

        private static void AssertRejectedAndDiscarded(
            InboundSynchronizationBatch batch,
            InboundSynchronizationStagingError expectedError)
        {
            var accumulator = new InboundSynchronizationStagingAccumulator();

            InboundSynchronizationStagingResult result = accumulator.Append(batch);

            AssertDiscarded(accumulator, result, expectedError);
        }

        private static void AssertDiscarded(
            InboundSynchronizationStagingAccumulator accumulator,
            InboundSynchronizationStagingResult result,
            InboundSynchronizationStagingError expectedError)
        {
            Assert.IsFalse(result.IsAccepted);
            Assert.IsFalse(result.IsCompleted);
            Assert.AreEqual(expectedError, result.Error);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Discarded,
                accumulator.State);
            Assert.IsFalse(accumulator.TryGetCompletedSnapshot(out _, out _));
        }

        private static InboundSynchronizationBatch Batch(
            Guid snapshotId,
            ulong logicalClock,
            uint batchIndex,
            ulong totalCount,
            bool isLastBatch,
            params ServiceRecord[] records)
        {
            return new InboundSynchronizationBatch(
                snapshotId,
                logicalClock,
                batchIndex,
                totalCount,
                isLastBatch,
                records);
        }

        private static List<ServiceRecord> Records(int count, bool deleted)
        {
            var records = new List<ServiceRecord>(count);
            for (int index = 0; index < count; index++)
            {
                ulong logicalVersion = (ulong)index + 1UL;
                ServiceDefinition definition = TestData.Definition(
                    name: "Service " + index,
                    productCode: ProductCodeFor(index));
                if (deleted)
                {
                    records.Add(new ServiceRecord(
                        definition,
                        TestData.Utc(0),
                        true,
                        TestData.Utc(1),
                        logicalVersion,
                        TestData.OriginA));
                }
                else
                {
                    records.Add(TestData.ActiveRecord(
                        definition,
                        logicalVersion,
                        TestData.OriginA));
                }
            }

            return records;
        }

        private static ServiceRecord ActiveRecord(int ordinal, ulong version)
        {
            return TestData.ActiveRecord(
                TestData.Definition(
                    name: "Service " + ordinal,
                    productCode: ProductCodeFor(ordinal)),
                version,
                TestData.OriginA);
        }

        private static string ProductCodeFor(int value)
        {
            const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            var characters = new char[4];
            int remaining = value;
            for (int index = characters.Length - 1; index >= 0; index--)
            {
                characters[index] = alphabet[remaining % alphabet.Length];
                remaining /= alphabet.Length;
            }

            if (remaining != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            return new string(characters);
        }
    }
}
