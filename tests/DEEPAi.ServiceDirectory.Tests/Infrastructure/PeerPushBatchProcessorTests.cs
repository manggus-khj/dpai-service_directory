using System;
using System.Collections.Generic;
using System.Globalization;
using DEEPAi.ServiceDirectory.Application.Persistence;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Application.Synchronization;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Synchronization;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class PeerPushBatchProcessorTests
    {
        private static readonly Guid ExpectedPeerInstanceId = new Guid(
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private static readonly Guid OtherPeerInstanceId = new Guid(
            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        private static readonly Guid FirstSnapshotId = new Guid(
            "cccccccc-cccc-cccc-cccc-cccccccccccc");
        private static readonly Guid SecondSnapshotId = new Guid(
            "dddddddd-dddd-dddd-dddd-dddddddddddd");

        [TestMethod]
        public void NonFinalBatchDoesNotPublishPersistOrAdvanceClock()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(initial);
            StateMutationCoordinator coordinator = Open(store);
            var processor = new PeerPushBatchProcessor(
                ExpectedPeerInstanceId,
                coordinator);

            PeerPushBatchProcessingResult result = processor.Process(
                Request(
                    ExpectedPeerInstanceId,
                    FirstSnapshotId,
                    5UL,
                    0U,
                    2UL,
                    false,
                    Item("Active A", "AB12", 4UL)));

            Assert.AreEqual(
                PeerPushBatchProcessingStatus.AcceptedAwaitingMoreBatches,
                result.Status);
            Assert.IsTrue(result.IsAccepted);
            Assert.IsFalse(result.IsCompleted);
            Assert.AreEqual(PeerSyncResponseCode.Ok, result.ResponseCode);
            Assert.IsFalse(result.SnapshotPublished);
            Assert.IsNull(result.CompletedRemoteSnapshotId);
            Assert.IsNull(result.CompletedRemoteSnapshot);
            Assert.IsNull(result.CurrentDirectorySnapshot);
            Assert.IsNull(result.CurrentOutboundSnapshot);
            Assert.AreEqual(0, store.CommitCallCount);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
            Assert.AreEqual(0UL, coordinator.CurrentSnapshot.LogicalClock);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Collecting,
                processor.StagingState);
        }

        [TestMethod]
        public void FinalBatchMergesAndReturnsInboundCurrentAndOutboundSnapshots()
        {
            var pending = new PendingRegistration(
                new Guid("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                PendingRequestType.New,
                TestData.Utc(1),
                "192.0.2.20",
                TestData.Definition(
                    name: "Pending local",
                    productCode: "EF56"),
                DirectoryBaseRevision.Capture(null));
            var initial = new DirectorySnapshot(
                new ServiceRecord[0],
                new[] { pending },
                0UL);
            var store = new FakeStateStore(initial);
            StateMutationCoordinator coordinator = Open(store);
            var processor = new PeerPushBatchProcessor(
                ExpectedPeerInstanceId,
                coordinator);

            PeerPushBatchProcessingResult first = processor.Process(
                Request(
                    ExpectedPeerInstanceId,
                    FirstSnapshotId,
                    5UL,
                    0U,
                    2UL,
                    false,
                    Item("Active A", "AB12", 4UL)));
            PeerPushBatchProcessingResult completed = processor.Process(
                Request(
                    ExpectedPeerInstanceId,
                    FirstSnapshotId,
                    5UL,
                    1U,
                    2UL,
                    true,
                    Item(
                        "Deleted C",
                        "CD34",
                        5UL,
                        true)));

            Assert.AreEqual(
                PeerPushBatchProcessingStatus.AcceptedAwaitingMoreBatches,
                first.Status);
            Assert.AreEqual(
                PeerPushBatchProcessingStatus.Completed,
                completed.Status);
            Assert.IsTrue(completed.IsAccepted);
            Assert.IsTrue(completed.IsCompleted);
            Assert.AreEqual(
                PeerSyncResponseCode.Ok,
                completed.ResponseCode);
            Assert.IsTrue(completed.SnapshotPublished);
            Assert.AreEqual(
                FirstSnapshotId,
                completed.CompletedRemoteSnapshotId.Value);
            Assert.AreEqual(2, completed.CompletedRemoteSnapshot.Records.Count);
            Assert.AreEqual(5UL, completed.CompletedRemoteSnapshot.LogicalClock);
            Assert.AreSame(
                coordinator.CurrentSnapshot,
                completed.CurrentDirectorySnapshot);
            Assert.AreEqual(1, completed.CurrentDirectorySnapshot.PendingCount);
            Assert.AreEqual(2, completed.CurrentOutboundSnapshot.Records.Count);
            Assert.AreEqual(5UL, completed.CurrentOutboundSnapshot.LogicalClock);
            Assert.AreEqual(1, store.CommitCallCount);
            Assert.AreSame(initial, store.LastExpectedSnapshot);
            Assert.AreSame(
                completed.CurrentDirectorySnapshot,
                store.LastNextSnapshot);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Completed,
                processor.StagingState);

            ServiceRecord tombstone = GetRecord(
                completed.CurrentOutboundSnapshot,
                "CD34");
            Assert.IsTrue(tombstone.Deleted);
            Assert.AreEqual(TestData.Utc(2), tombstone.DeletedUtc.Value);
            Assert.AreEqual(TestData.OriginB, tombstone.OriginInstanceId);
        }

        [TestMethod]
        public void PeerMismatchDiscardsAllPreviouslyStagedBatches()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(initial);
            StateMutationCoordinator coordinator = Open(store);
            var processor = new PeerPushBatchProcessor(
                ExpectedPeerInstanceId,
                coordinator);

            processor.Process(
                Request(
                    ExpectedPeerInstanceId,
                    FirstSnapshotId,
                    3UL,
                    0U,
                    2UL,
                    false,
                    Item("Active A", "AB12", 2UL)));
            PeerPushBatchProcessingResult mismatch = processor.Process(
                Request(
                    OtherPeerInstanceId,
                    FirstSnapshotId,
                    3UL,
                    1U,
                    2UL,
                    true,
                    Item("Active C", "CD34", 3UL)));

            Assert.AreEqual(
                PeerPushBatchProcessingStatus.PeerMismatch,
                mismatch.Status);
            Assert.AreEqual(
                PeerSyncResponseCode.NotPeer,
                mismatch.ResponseCode);
            Assert.IsFalse(mismatch.IsAccepted);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Discarded,
                processor.StagingState);
            Assert.AreEqual(0, store.CommitCallCount);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);

            PeerPushBatchProcessingResult retry = processor.Process(
                Request(
                    ExpectedPeerInstanceId,
                    FirstSnapshotId,
                    3UL,
                    1U,
                    2UL,
                    true,
                    Item("Active C", "CD34", 3UL)));
            Assert.AreEqual(
                PeerPushBatchProcessingStatus.StagingRejected,
                retry.Status);
            Assert.AreEqual(
                InboundSynchronizationStagingError.StagingNotCollecting,
                retry.StagingError);
        }

        [TestMethod]
        public void CrossBatchOrderFailureDiscardsWithoutMutation()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(initial);
            StateMutationCoordinator coordinator = Open(store);
            var processor = new PeerPushBatchProcessor(
                ExpectedPeerInstanceId,
                coordinator);

            processor.Process(
                Request(
                    ExpectedPeerInstanceId,
                    FirstSnapshotId,
                    2UL,
                    0U,
                    2UL,
                    false,
                    Item("Active C", "CD34", 1UL)));
            PeerPushBatchProcessingResult rejected = processor.Process(
                Request(
                    ExpectedPeerInstanceId,
                    FirstSnapshotId,
                    2UL,
                    1U,
                    2UL,
                    true,
                    Item("Active A", "AB12", 2UL)));

            Assert.AreEqual(
                PeerPushBatchProcessingStatus.StagingRejected,
                rejected.Status);
            Assert.AreEqual(
                InboundSynchronizationStagingError.ProductCodeOrderMismatch,
                rejected.StagingError);
            Assert.AreEqual(
                PeerSyncResponseCode.BadRequest,
                rejected.ResponseCode);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Discarded,
                processor.StagingState);
            Assert.AreEqual(0, store.CommitCallCount);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
        }

        [TestMethod]
        public void RevisionCollisionReturnsTypedDomainFailureAndDiscards()
        {
            ServiceRecord local = TestData.ActiveRecord(
                TestData.Definition(name: "Local value"),
                5UL,
                TestData.OriginA);
            var initial = new DirectorySnapshot(
                new[] { local },
                new PendingRegistration[0],
                5UL);
            var store = new FakeStateStore(initial);
            StateMutationCoordinator coordinator = Open(store);
            var processor = new PeerPushBatchProcessor(
                ExpectedPeerInstanceId,
                coordinator);

            PeerPushBatchProcessingResult result = processor.Process(
                Request(
                    ExpectedPeerInstanceId,
                    FirstSnapshotId,
                    9UL,
                    0U,
                    1UL,
                    true,
                    Item(
                        "Conflicting value",
                        "AB12",
                        5UL,
                        false,
                        TestData.OriginA)));

            Assert.AreEqual(
                PeerPushBatchProcessingStatus.DomainRejected,
                result.Status);
            Assert.AreEqual(
                DEEPAi.ServiceDirectory.Domain.DomainErrorCode
                    .RevisionCollision,
                result.DomainErrorCode.Value);
            Assert.AreEqual(
                PeerSyncResponseCode.RevisionCollision,
                result.ResponseCode);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Discarded,
                processor.StagingState);
            Assert.IsNull(result.CompletedRemoteSnapshot);
            Assert.AreEqual(0, store.CommitCallCount);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
            Assert.AreEqual(5UL, coordinator.CurrentSnapshot.LogicalClock);
        }

        [TestMethod]
        public void DirectoryCapacityReturnsExactDomainCodeWithoutMutation()
        {
            List<ServiceRecord> localRecords = ActiveRecords(
                DirectorySnapshot.ActiveServiceLimit);
            var initial = new DirectorySnapshot(
                localRecords,
                new PendingRegistration[0],
                1UL);
            var store = new FakeStateStore(initial);
            StateMutationCoordinator coordinator = Open(store);
            var processor = new PeerPushBatchProcessor(
                ExpectedPeerInstanceId,
                coordinator);

            PeerPushBatchProcessingResult result = processor.Process(
                Request(
                    ExpectedPeerInstanceId,
                    FirstSnapshotId,
                    2UL,
                    0U,
                    1UL,
                    true,
                    Item("Remote extra", "ZZZZ", 2UL)));

            Assert.AreEqual(
                PeerPushBatchProcessingStatus.DomainRejected,
                result.Status);
            Assert.AreEqual(
                DomainErrorCode.DirectoryCapacity,
                result.DomainErrorCode.Value);
            Assert.AreEqual(
                PeerSyncResponseCode.DirectoryCapacity,
                result.ResponseCode);
            Assert.IsFalse(result.SnapshotPublished);
            Assert.IsNull(result.CurrentDirectorySnapshot);
            Assert.IsNull(result.CurrentOutboundSnapshot);
            Assert.AreEqual(0, store.CommitCallCount);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
            Assert.AreEqual(1UL, coordinator.CurrentSnapshot.LogicalClock);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Discarded,
                processor.StagingState);
        }

        [TestMethod]
        public void PersistenceFailureReturnsTypedFailureWithoutPublishing()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(initial)
            {
                CommitResult = StateCommitResult.Failure(
                    StateCommitFailureCode.IoFailure)
            };
            StateMutationCoordinator coordinator = Open(store);
            var processor = new PeerPushBatchProcessor(
                ExpectedPeerInstanceId,
                coordinator);

            PeerPushBatchProcessingResult result = processor.Process(
                Request(
                    ExpectedPeerInstanceId,
                    FirstSnapshotId,
                    1UL,
                    0U,
                    1UL,
                    true,
                    Item("Active A", "AB12", 1UL)));

            Assert.AreEqual(
                PeerPushBatchProcessingStatus.PersistenceFailed,
                result.Status);
            Assert.AreEqual(
                StateCommitFailureCode.IoFailure,
                result.CommitFailureCode);
            Assert.AreEqual(
                PeerSyncResponseCode.Internal,
                result.ResponseCode);
            Assert.IsFalse(result.SnapshotPublished);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Discarded,
                processor.StagingState);
            Assert.AreEqual(1, store.CommitCallCount);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
            Assert.IsTrue(coordinator.IsReady);
        }

        [TestMethod]
        public void CommitExceptionDiscardsAndLeavesSnapshotUnpublished()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(initial)
            {
                CommitException = new InvalidOperationException(
                    "Simulated commit failure.")
            };
            StateMutationCoordinator coordinator = Open(store);
            var processor = new PeerPushBatchProcessor(
                ExpectedPeerInstanceId,
                coordinator);

            Assert.ThrowsExactly<InvalidOperationException>(
                () => processor.Process(
                    Request(
                        ExpectedPeerInstanceId,
                        FirstSnapshotId,
                        1UL,
                        0U,
                        1UL,
                        true,
                        Item("Active A", "AB12", 1UL))));

            Assert.AreEqual(1, store.CommitCallCount);
            Assert.AreSame(initial, store.LastExpectedSnapshot);
            Assert.IsNotNull(store.LastNextSnapshot);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
            Assert.AreEqual(0UL, coordinator.CurrentSnapshot.LogicalClock);
            Assert.AreEqual(
                StateCoordinatorStatus.RecoveryRequired,
                coordinator.Status);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Discarded,
                processor.StagingState);
        }

        [TestMethod]
        public void RecoveryRequiredAndBlockedResultsAreTypedAndDiscarded()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(initial)
            {
                CommitResult = StateCommitResult.Failure(
                    StateCommitFailureCode.RecoveryRequired)
            };
            StateMutationCoordinator coordinator = Open(store);
            var firstProcessor = new PeerPushBatchProcessor(
                ExpectedPeerInstanceId,
                coordinator);

            PeerPushBatchProcessingResult requiresRecovery =
                firstProcessor.Process(
                    Request(
                        ExpectedPeerInstanceId,
                        FirstSnapshotId,
                        1UL,
                        0U,
                        1UL,
                        true,
                        Item("Active A", "AB12", 1UL)));

            Assert.AreEqual(
                PeerPushBatchProcessingStatus.RecoveryRequired,
                requiresRecovery.Status);
            Assert.AreEqual(
                StateCommitFailureCode.RecoveryRequired,
                requiresRecovery.CommitFailureCode);
            Assert.AreEqual(
                PeerSyncResponseCode.Internal,
                requiresRecovery.ResponseCode);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Discarded,
                firstProcessor.StagingState);
            Assert.AreEqual(StateCoordinatorStatus.RecoveryRequired,
                coordinator.Status);

            var blockedProcessor = new PeerPushBatchProcessor(
                ExpectedPeerInstanceId,
                coordinator);
            PeerPushBatchProcessingResult blocked = blockedProcessor.Process(
                Request(
                    ExpectedPeerInstanceId,
                    SecondSnapshotId,
                    1UL,
                    0U,
                    2UL,
                    false,
                    Item("Active A", "AB12", 1UL)));

            Assert.AreEqual(
                PeerPushBatchProcessingStatus.BlockedForRecovery,
                blocked.Status);
            Assert.AreEqual(StateCommitFailureCode.None,
                blocked.CommitFailureCode);
            Assert.AreEqual(
                PeerSyncResponseCode.Internal,
                blocked.ResponseCode);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Discarded,
                blockedProcessor.StagingState);
            Assert.AreEqual(1, store.CommitCallCount);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
        }

        [TestMethod]
        public void UnchangedFinalSnapshotStillReturnsHostOwnedOutboundView()
        {
            ServiceRecord existing = TestData.ActiveRecord(
                TestData.Definition(
                    name: "Unchanged",
                    serverAddress: "service.internal"),
                3UL,
                TestData.OriginB);
            var initial = new DirectorySnapshot(
                new[] { existing },
                new PendingRegistration[0],
                3UL);
            var store = new FakeStateStore(initial);
            StateMutationCoordinator coordinator = Open(store);
            var processor = new PeerPushBatchProcessor(
                ExpectedPeerInstanceId,
                coordinator);

            PeerPushBatchProcessingResult result = processor.Process(
                Request(
                    ExpectedPeerInstanceId,
                    FirstSnapshotId,
                    3UL,
                    0U,
                    1UL,
                    true,
                    Item("Unchanged", "AB12", 3UL)));

            Assert.AreEqual(
                PeerPushBatchProcessingStatus.Completed,
                result.Status);
            Assert.AreEqual(PeerSyncResponseCode.Ok, result.ResponseCode);
            Assert.IsFalse(result.SnapshotPublished);
            Assert.AreSame(initial, result.CurrentDirectorySnapshot);
            Assert.AreEqual(1, result.CurrentOutboundSnapshot.Records.Count);
            Assert.AreSame(
                existing,
                GetRecord(result.CurrentOutboundSnapshot, "AB12"));
            Assert.AreEqual(0, store.CommitCallCount);
            Assert.AreEqual(
                InboundSynchronizationStagingState.Completed,
                processor.StagingState);
        }

        [TestMethod]
        public void FinalBatchObservesRemoteClockWithoutReplacingLocalWinner()
        {
            ServiceRecord existing = TestData.ActiveRecord(
                TestData.Definition(
                    name: "Unchanged",
                    serverAddress: "service.internal"),
                3UL,
                TestData.OriginB);
            var initial = new DirectorySnapshot(
                new[] { existing },
                new PendingRegistration[0],
                3UL);
            var store = new FakeStateStore(initial);
            StateMutationCoordinator coordinator = Open(store);
            var processor = new PeerPushBatchProcessor(
                ExpectedPeerInstanceId,
                coordinator);

            PeerPushBatchProcessingResult result = processor.Process(
                Request(
                    ExpectedPeerInstanceId,
                    FirstSnapshotId,
                    9UL,
                    0U,
                    1UL,
                    true,
                    Item("Unchanged", "AB12", 3UL)));

            Assert.AreEqual(
                PeerPushBatchProcessingStatus.Completed,
                result.Status);
            Assert.IsTrue(result.SnapshotPublished);
            Assert.AreEqual(1, store.CommitCallCount);
            Assert.AreEqual(9UL, coordinator.CurrentSnapshot.LogicalClock);
            Assert.AreEqual(9UL, result.CurrentOutboundSnapshot.LogicalClock);
            Assert.AreSame(
                existing,
                GetRecord(result.CurrentOutboundSnapshot, "AB12"));
        }

        [TestMethod]
        public void RequestItemArrayIsDefensivelyCopiedBeforeProcessing()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(initial);
            StateMutationCoordinator coordinator = Open(store);
            var processor = new PeerPushBatchProcessor(
                ExpectedPeerInstanceId,
                coordinator);
            var sourceItems = new[]
            {
                Item("Active A", "AB12", 1UL)
            };
            PeerPushExchangeRequest request = Request(
                ExpectedPeerInstanceId,
                FirstSnapshotId,
                1UL,
                0U,
                1UL,
                true,
                sourceItems);

            sourceItems[0] = Item("Changed source", "CD34", 1UL);
            PeerPushBatchProcessingResult result = processor.Process(request);

            Assert.AreEqual(
                PeerPushBatchProcessingStatus.Completed,
                result.Status);
            Assert.AreEqual("AB12", request.Items[0].ProductCode);
            Assert.AreEqual(
                "Active A",
                GetRecord(result.CurrentOutboundSnapshot, "AB12")
                    .Definition.Name);
            Assert.IsFalse(result.CurrentOutboundSnapshot.Records.ContainsKey(
                TestData.ProductCode("CD34")));
        }

        private static PeerPushExchangeRequest Request(
            Guid instanceId,
            Guid snapshotId,
            ulong logicalClock,
            uint batchIndex,
            ulong totalCount,
            bool isLastBatch,
            params PeerSyncServiceItem[] items)
        {
            return new PeerPushExchangeRequest(
                instanceId,
                snapshotId,
                logicalClock,
                batchIndex,
                totalCount,
                isLastBatch,
                items);
        }

        private static PeerSyncServiceItem Item(
            string name,
            string productCode,
            ulong logicalVersion,
            bool deleted = false,
            Guid? originInstanceId = null)
        {
            return new PeerSyncServiceItem(
                name,
                productCode,
                "service.internal",
                21000,
                TestData.Utc(0),
                deleted,
                deleted ? TestData.Utc(2) : (DateTime?)null,
                logicalVersion,
                originInstanceId ?? TestData.OriginB);
        }

        private static ServiceRecord GetRecord(
            SynchronizationSnapshot snapshot,
            string productCode)
        {
            ServiceRecord record;
            Assert.IsTrue(snapshot.Records.TryGetValue(
                TestData.ProductCode(productCode),
                out record));
            return record;
        }

        private static List<ServiceRecord> ActiveRecords(int count)
        {
            var records = new List<ServiceRecord>(count);
            for (int index = 0; index < count; index++)
            {
                string productCode = index.ToString(
                    "D4",
                    CultureInfo.InvariantCulture);
                records.Add(TestData.ActiveRecord(
                    TestData.Definition(
                        name: "Local " + productCode,
                        productCode: productCode,
                        serverAddress: "service.internal"),
                    1UL,
                    TestData.OriginA));
            }

            return records;
        }

        private static StateMutationCoordinator Open(FakeStateStore store)
        {
            StateCoordinatorOpenResult openResult =
                StateMutationCoordinator.Open(store);
            Assert.IsTrue(openResult.IsSuccess);
            return openResult.Coordinator;
        }

        private sealed class FakeStateStore : IServiceDirectoryStateStore
        {
            private readonly DirectorySnapshot _initialSnapshot;

            internal FakeStateStore(DirectorySnapshot initialSnapshot)
            {
                _initialSnapshot = initialSnapshot
                    ?? throw new ArgumentNullException(nameof(initialSnapshot));
                CommitResult = StateCommitResult.Success();
            }

            internal StateCommitResult CommitResult { get; set; }

            internal Exception CommitException { get; set; }

            internal int CommitCallCount { get; private set; }

            internal DirectorySnapshot LastExpectedSnapshot { get; private set; }

            internal DirectorySnapshot LastNextSnapshot { get; private set; }

            public StateLoadResult Load()
            {
                return StateLoadResult.Success(_initialSnapshot);
            }

            public StateCommitResult Commit(
                DirectorySnapshot expectedSnapshot,
                DirectorySnapshot nextSnapshot)
            {
                CommitCallCount++;
                LastExpectedSnapshot = expectedSnapshot;
                LastNextSnapshot = nextSnapshot;
                if (CommitException != null)
                {
                    throw CommitException;
                }

                return CommitResult;
            }
        }
    }
}
