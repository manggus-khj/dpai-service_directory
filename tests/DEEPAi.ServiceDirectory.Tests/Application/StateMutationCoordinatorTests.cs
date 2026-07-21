using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DEEPAi.ServiceDirectory.Application.Persistence;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Registration;
using DEEPAi.ServiceDirectory.Domain.Synchronization;
using DEEPAi.ServiceDirectory.Tests.TestSupport;

namespace DEEPAi.ServiceDirectory.Tests.Application
{
    [TestClass]
    public sealed class StateMutationCoordinatorTests
    {
        [TestMethod]
        public void SuccessfulCommitPublishesCommittedSnapshot()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(StateLoadResult.Success(initial));
            StateMutationCoordinator coordinator = Open(store);

            StateMutationResult<SubmissionResult> result = coordinator.Submit(
                TestData.Definition(),
                IPAddress.Parse("192.0.2.10"),
                Guid.NewGuid(),
                TestData.Utc(1));

            Assert.AreEqual(StateMutationStatus.Completed, result.Status);
            Assert.IsTrue(result.IsSuccessful);
            Assert.IsTrue(result.SnapshotPublished);
            Assert.AreEqual(1, store.CommitCallCount);
            Assert.AreSame(initial, store.LastExpectedSnapshot);
            Assert.AreSame(store.LastNextSnapshot, coordinator.CurrentSnapshot);
            Assert.AreSame(result.DomainTransition.NextSnapshot, coordinator.CurrentSnapshot);
            Assert.IsTrue(coordinator.IsReady);
        }

        [TestMethod]
        public void ExternalMutationReloadsBaselineBeforePublishing()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            DirectorySnapshot committed = new DirectorySnapshot(
                new[]
                {
                    TestData.ActiveRecord(
                        TestData.Definition(),
                        1UL,
                        TestData.OriginA)
                },
                new PendingRegistration[0],
                1UL);
            var store = new FakeStateStore(StateLoadResult.Success(initial));
            StateMutationCoordinator coordinator = Open(store);
            int durableCommitCalls = 0;

            bool succeeded = coordinator.TryCommitExternalMutation(
                initial,
                () =>
                {
                    durableCommitCalls++;
                    store.LoadResult = StateLoadResult.Success(committed);
                });

            Assert.IsTrue(succeeded);
            Assert.AreEqual(1, durableCommitCalls);
            Assert.AreEqual(2, store.LoadCallCount);
            Assert.AreSame(committed, coordinator.CurrentSnapshot);
            Assert.AreEqual(StateCoordinatorStatus.Ready, coordinator.Status);
        }

        [TestMethod]
        public void ExternalMutationRejectsStaleSnapshotWithoutCommit()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(StateLoadResult.Success(initial));
            StateMutationCoordinator coordinator = Open(store);
            int durableCommitCalls = 0;

            bool succeeded = coordinator.TryCommitExternalMutation(
                DirectorySnapshot.Empty(),
                () => durableCommitCalls++);

            Assert.IsFalse(succeeded);
            Assert.AreEqual(0, durableCommitCalls);
            Assert.AreEqual(1, store.LoadCallCount);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
            Assert.AreEqual(StateCoordinatorStatus.Ready, coordinator.Status);
        }

        [TestMethod]
        public void ExternalMutationFailureRequiresVerifiedReload()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(StateLoadResult.Success(initial));
            StateMutationCoordinator coordinator = Open(store);

            Assert.ThrowsExactly<StateRecoveryRequiredException>(() =>
                coordinator.TryCommitExternalMutation(
                    initial,
                    () => throw new StateRecoveryRequiredException(
                        "Injected durable commit failure.",
                        null)));

            Assert.AreEqual(
                StateCoordinatorStatus.RecoveryRequired,
                coordinator.Status);
            Assert.IsFalse(coordinator.TryGetReadySnapshot(out _));

            StateLoadResult recovered = coordinator.Recover();

            Assert.IsTrue(recovered.IsSuccess);
            Assert.AreSame(initial, recovered.Snapshot);
            Assert.AreEqual(StateCoordinatorStatus.Ready, coordinator.Status);
        }

        [TestMethod]
        public void ExternalMutationValidationFailureKeepsCoordinatorReady()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(StateLoadResult.Success(initial));
            StateMutationCoordinator coordinator = Open(store);

            Assert.ThrowsExactly<InvalidDataException>(() =>
                coordinator.TryCommitExternalMutation(
                    initial,
                    () => throw new InvalidDataException(
                        "Injected candidate validation failure.")));

            Assert.AreSame(initial, coordinator.CurrentSnapshot);
            Assert.AreEqual(StateCoordinatorStatus.Ready, coordinator.Status);
            Assert.AreEqual(1, store.LoadCallCount);
        }

        [TestMethod]
        public void ExternalMutationReloadFailureDoesNotPublishCandidate()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(StateLoadResult.Success(initial));
            StateMutationCoordinator coordinator = Open(store);

            bool succeeded = coordinator.TryCommitExternalMutation(
                initial,
                () => store.LoadResult = StateLoadResult.Failure(
                    StateLoadFailureCode.InvalidData));

            Assert.IsFalse(succeeded);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
            Assert.AreEqual(
                StateCoordinatorStatus.RecoveryRequired,
                coordinator.Status);
        }

        [TestMethod]
        public void OrdinaryPersistenceFailureDoesNotPublishCandidateSnapshot()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(StateLoadResult.Success(initial))
            {
                CommitResult = StateCommitResult.Failure(StateCommitFailureCode.IoFailure)
            };
            StateMutationCoordinator coordinator = Open(store);

            StateMutationResult<SubmissionResult> result = coordinator.Submit(
                TestData.Definition(),
                IPAddress.Parse("192.0.2.10"),
                Guid.NewGuid(),
                TestData.Utc(1));

            Assert.AreEqual(StateMutationStatus.PersistenceFailed, result.Status);
            Assert.IsFalse(result.HasDomainTransition);
            Assert.IsFalse(result.SnapshotPublished);
            Assert.AreEqual(StateCommitFailureCode.IoFailure, result.CommitFailureCode);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
            Assert.AreNotSame(initial, store.LastNextSnapshot);
            Assert.IsTrue(coordinator.IsReady);
        }

        [TestMethod]
        public void RecoveryRequiredBlocksMutationUntilVerifiedReloadSucceeds()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(StateLoadResult.Success(initial))
            {
                CommitResult = StateCommitResult.Failure(
                    StateCommitFailureCode.RecoveryRequired)
            };
            StateMutationCoordinator coordinator = Open(store);
            ServiceDefinition requested = TestData.Definition();

            StateMutationResult<SubmissionResult> failed = coordinator.Submit(
                requested,
                IPAddress.Parse("192.0.2.10"),
                Guid.NewGuid(),
                TestData.Utc(1));
            StateMutationResult<SubmissionResult> blocked = coordinator.Submit(
                requested,
                IPAddress.Parse("192.0.2.11"),
                Guid.NewGuid(),
                TestData.Utc(2));

            Assert.AreEqual(StateMutationStatus.RecoveryRequired, failed.Status);
            Assert.AreEqual(StateMutationStatus.BlockedForRecovery, blocked.Status);
            Assert.AreEqual(StateCoordinatorStatus.RecoveryRequired, coordinator.Status);
            Assert.AreEqual(1, store.CommitCallCount);
            Assert.IsFalse(coordinator.TryGetReadySnapshot(out _));

            ServiceDefinition recoveredDefinition = TestData.Definition(
                name: "Recovered",
                productCode: "CD34");
            DirectorySnapshot recovered = new DirectorySnapshot(
                new[]
                {
                    TestData.ActiveRecord(
                        recoveredDefinition,
                        1UL,
                        TestData.OriginA)
                },
                new PendingRegistration[0],
                1UL);
            store.LoadResult = StateLoadResult.Success(recovered);

            StateLoadResult recoveryResult = coordinator.Recover();

            Assert.IsTrue(recoveryResult.IsSuccess);
            Assert.AreSame(recovered, recoveryResult.Snapshot);
            Assert.AreSame(recovered, coordinator.CurrentSnapshot);
            Assert.AreEqual(StateCoordinatorStatus.Ready, coordinator.Status);
            Assert.AreEqual(2, store.LoadCallCount);
        }

        [TestMethod]
        public void VerifiedSynchronizationCommitPublishesMergedSnapshot()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(StateLoadResult.Success(initial));
            StateMutationCoordinator coordinator = Open(store);
            ServiceRecord remoteRecord = TestData.ActiveRecord(
                TestData.Definition(name: "Remote service"),
                7UL,
                TestData.OriginB);
            var remote = new SynchronizationSnapshot(
                new[] { remoteRecord },
                9UL);

            StateMutationResult<SynchronizationMergeResult> result =
                coordinator.MergeVerifiedSynchronizationSnapshot(remote);

            Assert.AreEqual(StateMutationStatus.Completed, result.Status);
            Assert.IsTrue(result.IsSuccessful);
            Assert.IsTrue(result.SnapshotPublished);
            Assert.IsFalse(result.ShouldScheduleSync);
            Assert.AreEqual(1, store.CommitCallCount);
            Assert.AreSame(initial, store.LastExpectedSnapshot);
            Assert.AreSame(store.LastNextSnapshot, coordinator.CurrentSnapshot);
            Assert.AreSame(
                result.DomainTransition.NextSnapshot,
                coordinator.CurrentSnapshot);
            Assert.AreEqual(9UL, coordinator.CurrentSnapshot.LogicalClock);
            Assert.AreSame(
                remoteRecord,
                GetRecord(coordinator.CurrentSnapshot, "AB12"));
        }

        [TestMethod]
        public void VerifiedSynchronizationNoChangeSkipsPersistence()
        {
            ServiceRecord localRecord = TestData.ActiveRecord(
                TestData.Definition(),
                5UL,
                TestData.OriginA);
            var initial = new DirectorySnapshot(
                new[] { localRecord },
                new PendingRegistration[0],
                5UL);
            var store = new FakeStateStore(StateLoadResult.Success(initial));
            StateMutationCoordinator coordinator = Open(store);
            var remote = new SynchronizationSnapshot(
                new[] { localRecord },
                5UL);

            StateMutationResult<SynchronizationMergeResult> result =
                coordinator.MergeVerifiedSynchronizationSnapshot(remote);

            Assert.AreEqual(StateMutationStatus.Completed, result.Status);
            Assert.IsTrue(result.IsSuccessful);
            Assert.IsFalse(result.SnapshotPublished);
            Assert.IsFalse(result.ShouldScheduleSync);
            Assert.AreEqual(0, store.CommitCallCount);
            Assert.AreSame(initial, result.DomainTransition.NextSnapshot);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
        }

        [TestMethod]
        public void VerifiedSynchronizationCollisionDoesNotPersistOrObserveClock()
        {
            ServiceRecord localRecord = TestData.ActiveRecord(
                TestData.Definition(name: "Local value"),
                5UL,
                TestData.OriginA);
            var initial = new DirectorySnapshot(
                new[] { localRecord },
                new PendingRegistration[0],
                5UL);
            var store = new FakeStateStore(StateLoadResult.Success(initial));
            StateMutationCoordinator coordinator = Open(store);
            ServiceRecord conflictingRemote = TestData.ActiveRecord(
                TestData.Definition(name: "Conflicting value"),
                5UL,
                TestData.OriginA);
            var remote = new SynchronizationSnapshot(
                new[] { conflictingRemote },
                12UL);

            StateMutationResult<SynchronizationMergeResult> result =
                coordinator.MergeVerifiedSynchronizationSnapshot(remote);

            Assert.AreEqual(StateMutationStatus.Completed, result.Status);
            Assert.IsFalse(result.IsSuccessful);
            Assert.IsTrue(result.HasDomainTransition);
            Assert.AreEqual(
                DomainErrorCode.RevisionCollision,
                result.DomainTransition.ErrorCode.Value);
            Assert.IsFalse(result.SnapshotPublished);
            Assert.AreEqual(0, store.CommitCallCount);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
            Assert.AreEqual(5UL, coordinator.CurrentSnapshot.LogicalClock);
        }

        [TestMethod]
        public void VerifiedSynchronizationCapacityFailureDoesNotPersistOrObserveClock()
        {
            List<ServiceRecord> localRecords = ActiveRecords(1000);
            var initial = new DirectorySnapshot(
                localRecords,
                new PendingRegistration[0],
                1UL);
            var store = new FakeStateStore(StateLoadResult.Success(initial));
            StateMutationCoordinator coordinator = Open(store);
            ServiceRecord extraRemote = TestData.ActiveRecord(
                TestData.Definition(
                    name: "Capacity overflow",
                    productCode: ProductCodeFor(1000)),
                2UL,
                TestData.OriginB);
            var remote = new SynchronizationSnapshot(
                new[] { extraRemote },
                12UL);

            StateMutationResult<SynchronizationMergeResult> result =
                coordinator.MergeVerifiedSynchronizationSnapshot(remote);

            Assert.AreEqual(StateMutationStatus.Completed, result.Status);
            Assert.IsFalse(result.IsSuccessful);
            Assert.AreEqual(
                DomainErrorCode.DirectoryCapacity,
                result.DomainTransition.ErrorCode.Value);
            Assert.IsFalse(result.SnapshotPublished);
            Assert.AreEqual(0, store.CommitCallCount);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
            Assert.AreEqual(1UL, coordinator.CurrentSnapshot.LogicalClock);
        }

        [TestMethod]
        public void VerifiedSynchronizationPersistenceFailureDoesNotPublishCandidate()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(StateLoadResult.Success(initial))
            {
                CommitResult = StateCommitResult.Failure(
                    StateCommitFailureCode.IoFailure)
            };
            StateMutationCoordinator coordinator = Open(store);
            ServiceRecord remoteRecord = TestData.ActiveRecord(
                TestData.Definition(),
                3UL,
                TestData.OriginB);

            StateMutationResult<SynchronizationMergeResult> result =
                coordinator.MergeVerifiedSynchronizationSnapshot(
                    new SynchronizationSnapshot(
                        new[] { remoteRecord },
                        8UL));

            Assert.AreEqual(StateMutationStatus.PersistenceFailed, result.Status);
            Assert.IsFalse(result.HasDomainTransition);
            Assert.IsFalse(result.SnapshotPublished);
            Assert.AreEqual(
                StateCommitFailureCode.IoFailure,
                result.CommitFailureCode);
            Assert.AreEqual(1, store.CommitCallCount);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
            Assert.AreEqual(0UL, coordinator.CurrentSnapshot.LogicalClock);
            Assert.IsTrue(coordinator.IsReady);
        }

        [TestMethod]
        public void VerifiedSynchronizationUsesLatestSnapshotInsideMutationGate()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(StateLoadResult.Success(initial));
            StateMutationCoordinator coordinator = Open(store);
            StateMutationResult<SubmissionResult> submission = coordinator.Submit(
                TestData.Definition(name: "Pending local request"),
                IPAddress.Parse("192.0.2.10"),
                Guid.NewGuid(),
                TestData.Utc(1));
            DirectorySnapshot latestBeforeMerge = coordinator.CurrentSnapshot;
            ServiceRecord remoteRecord = TestData.ActiveRecord(
                TestData.Definition(
                    name: "Peer service",
                    productCode: "CD34"),
                4UL,
                TestData.OriginB);

            StateMutationResult<SynchronizationMergeResult> result =
                coordinator.MergeVerifiedSynchronizationSnapshot(
                    new SynchronizationSnapshot(
                        new[] { remoteRecord },
                        6UL));

            Assert.IsTrue(submission.IsSuccessful);
            Assert.IsTrue(result.IsSuccessful);
            Assert.AreEqual(2, store.CommitCallCount);
            Assert.AreSame(latestBeforeMerge, store.LastExpectedSnapshot);
            Assert.AreEqual(1, coordinator.CurrentSnapshot.PendingCount);
            Assert.AreSame(
                remoteRecord,
                GetRecord(coordinator.CurrentSnapshot, "CD34"));
            Assert.AreEqual(6UL, coordinator.CurrentSnapshot.LogicalClock);
        }

        [TestMethod]
        public void VerifiedSynchronizationRecoveryRequiredBlocksFurtherMerges()
        {
            DirectorySnapshot initial = DirectorySnapshot.Empty();
            var store = new FakeStateStore(StateLoadResult.Success(initial))
            {
                CommitResult = StateCommitResult.Failure(
                    StateCommitFailureCode.RecoveryRequired)
            };
            StateMutationCoordinator coordinator = Open(store);
            ServiceRecord remoteRecord = TestData.ActiveRecord(
                TestData.Definition(),
                1UL,
                TestData.OriginB);
            var remote = new SynchronizationSnapshot(
                new[] { remoteRecord },
                1UL);

            StateMutationResult<SynchronizationMergeResult> failed =
                coordinator.MergeVerifiedSynchronizationSnapshot(remote);
            StateMutationResult<SynchronizationMergeResult> blocked =
                coordinator.MergeVerifiedSynchronizationSnapshot(remote);

            Assert.AreEqual(StateMutationStatus.RecoveryRequired, failed.Status);
            Assert.AreEqual(StateMutationStatus.BlockedForRecovery, blocked.Status);
            Assert.AreEqual(StateCoordinatorStatus.RecoveryRequired, coordinator.Status);
            Assert.AreEqual(1, store.CommitCallCount);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
        }

        [TestMethod]
        public void VerifiedSynchronizationRejectsNullBeforeEnteringMutationPipeline()
        {
            var store = new FakeStateStore(
                StateLoadResult.Success(DirectorySnapshot.Empty()));
            StateMutationCoordinator coordinator = Open(store);

            Assert.ThrowsExactly<ArgumentNullException>(
                () => coordinator.MergeVerifiedSynchronizationSnapshot(null));

            Assert.AreEqual(0, store.CommitCallCount);
            Assert.IsTrue(coordinator.IsReady);
        }

        [TestMethod]
        public void LogicalClockExhaustionIsDomainFailureAndSkipsPersistence()
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
            var initial = new DirectorySnapshot(
                new ServiceRecord[0],
                new[] { pending },
                ulong.MaxValue);
            var store = new FakeStateStore(StateLoadResult.Success(initial));
            StateMutationCoordinator coordinator = Open(store);

            StateMutationResult<ApprovalResult> result = coordinator.Approve(
                pendingId,
                TestData.OriginA,
                TestData.Utc(2));

            Assert.AreEqual(StateMutationStatus.Completed, result.Status);
            Assert.IsFalse(result.IsSuccessful);
            Assert.IsTrue(result.HasDomainTransition);
            Assert.AreEqual(
                DomainErrorCode.LogicalClockExhausted,
                result.DomainTransition.ErrorCode.Value);
            Assert.IsFalse(result.SnapshotPublished);
            Assert.AreEqual(0, store.CommitCallCount);
            Assert.AreSame(initial, coordinator.CurrentSnapshot);
            Assert.IsTrue(coordinator.IsReady);
        }

        private static ServiceRecord GetRecord(
            DirectorySnapshot snapshot,
            string productCode)
        {
            ServiceRecord record;
            Assert.IsTrue(
                snapshot.TryGetRecord(
                    TestData.ProductCode(productCode),
                    out record));
            return record;
        }

        private static List<ServiceRecord> ActiveRecords(int count)
        {
            var records = new List<ServiceRecord>(count);
            for (int value = 0; value < count; value++)
            {
                records.Add(TestData.ActiveRecord(
                    TestData.Definition(
                        name: "Service " + value,
                        productCode: ProductCodeFor(value)),
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

        private static StateMutationCoordinator Open(FakeStateStore store)
        {
            StateCoordinatorOpenResult openResult = StateMutationCoordinator.Open(store);
            Assert.IsTrue(openResult.IsSuccess);
            Assert.IsNotNull(openResult.Coordinator);
            return openResult.Coordinator;
        }

        private sealed class FakeStateStore : IServiceDirectoryStateStore
        {
            internal FakeStateStore(StateLoadResult loadResult)
            {
                LoadResult = loadResult;
                CommitResult = StateCommitResult.Success();
            }

            internal StateLoadResult LoadResult { get; set; }

            internal StateCommitResult CommitResult { get; set; }

            internal int LoadCallCount { get; private set; }

            internal int CommitCallCount { get; private set; }

            internal DirectorySnapshot LastExpectedSnapshot { get; private set; }

            internal DirectorySnapshot LastNextSnapshot { get; private set; }

            public StateLoadResult Load()
            {
                LoadCallCount++;
                return LoadResult;
            }

            public StateCommitResult Commit(
                DirectorySnapshot expectedSnapshot,
                DirectorySnapshot nextSnapshot)
            {
                CommitCallCount++;
                LastExpectedSnapshot = expectedSnapshot;
                LastNextSnapshot = nextSnapshot;
                return CommitResult;
            }
        }
    }
}
