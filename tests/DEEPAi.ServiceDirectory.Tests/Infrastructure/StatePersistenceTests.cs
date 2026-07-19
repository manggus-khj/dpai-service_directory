using System;
using System.IO;
using System.Text;
using DEEPAi.ServiceDirectory.Application.Persistence;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using DEEPAi.ServiceDirectory.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class StatePersistenceTests
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void StateXmlCodecRoundTripsSchemaVersionOneState()
        {
            DirectorySnapshot expected = CreateSnapshotWithPending();
            var codec = new StateXmlCodec();

            byte[] directoryContents = codec.SerializeDirectory(expected);
            byte[] pendingContents = codec.SerializePending(expected);
            DirectorySnapshot actual = codec.DeserializeSnapshot(
                directoryContents,
                pendingContents);

            StringAssert.Contains(
                StrictUtf8.GetString(directoryContents),
                "SchemaVersion=\"1\"");
            StringAssert.Contains(
                StrictUtf8.GetString(pendingContents),
                "SchemaVersion=\"1\"");
            Assert.IsTrue(
                DirectorySnapshotValueComparer.Equals(expected, actual));
        }

        [TestMethod]
        public void StateXmlCodecRejectsUnsupportedSchemaVersion()
        {
            DirectorySnapshot snapshot = DirectorySnapshot.Empty();
            var codec = new StateXmlCodec();
            string directoryXml = StrictUtf8.GetString(
                codec.SerializeDirectory(snapshot));
            byte[] unsupportedDirectory = StrictUtf8.GetBytes(
                directoryXml.Replace(
                    "SchemaVersion=\"1\"",
                    "SchemaVersion=\"2\""));

            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeSnapshot(
                    unsupportedDirectory,
                    codec.SerializePending(snapshot)));
        }

        [TestMethod]
        public void StoreCreatesBothDocumentsAndReloadsCommittedSnapshot()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                var store = new XmlServiceDirectoryStateStore(
                    stateDirectory);
                StateLoadResult initial = store.Load();
                DirectorySnapshot next = CreateActiveSnapshot();

                StateCommitResult commit = store.Commit(
                    initial.Snapshot,
                    next);
                StateLoadResult reloaded =
                    new XmlServiceDirectoryStateStore(
                        stateDirectory).Load();

                Assert.IsTrue(initial.IsSuccess);
                Assert.IsTrue(commit.IsSuccess);
                Assert.IsTrue(reloaded.IsSuccess);
                Assert.IsTrue(
                    DirectorySnapshotValueComparer.Equals(
                        next,
                        reloaded.Snapshot));
                Assert.IsTrue(File.Exists(
                    Path.Combine(stateDirectory, "directory.xml")));
                Assert.IsTrue(File.Exists(
                    Path.Combine(stateDirectory, "pending.xml")));
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void PreparedTransactionRollsBackOnNextLoad()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                var injector = new ThrowOnceFaultInjector(
                    RecoveryJournalFaultPoint.TargetApplied,
                    StateFileTarget.Directory);
                var store = new XmlServiceDirectoryStateStore(
                    stateDirectory,
                    injector);
                StateLoadResult initial = store.Load();

                StateCommitResult commit = store.Commit(
                    initial.Snapshot,
                    CreateActiveSnapshot());
                StateLoadResult recovered =
                    new XmlServiceDirectoryStateStore(
                        stateDirectory).Load();

                Assert.IsFalse(commit.IsSuccess);
                Assert.AreEqual(
                    StateCommitFailureCode.RecoveryRequired,
                    commit.FailureCode);
                Assert.IsTrue(commit.RequiresReload);
                Assert.IsTrue(recovered.IsSuccess);
                Assert.AreEqual(0, recovered.Snapshot.Records.Count);
                Assert.AreEqual(0UL, recovered.Snapshot.LogicalClock);
                Assert.IsFalse(File.Exists(
                    Path.Combine(stateDirectory, "directory.xml")));
                Assert.IsFalse(File.Exists(
                    Path.Combine(stateDirectory, "pending.xml")));
                Assert.IsFalse(File.Exists(
                    Path.Combine(stateDirectory, "directory.xml.bak")));
                Assert.IsFalse(File.Exists(
                    Path.Combine(stateDirectory, "pending.xml.bak")));
                AssertJournalIsEmpty(stateDirectory);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void CommittedTransactionRollsForwardOnNextLoad()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                DirectorySnapshot next = CreateActiveSnapshot();
                var injector = new ThrowOnceFaultInjector(
                    RecoveryJournalFaultPoint.CommittedFlushed,
                    null);
                var store = new XmlServiceDirectoryStateStore(
                    stateDirectory,
                    injector);
                StateLoadResult initial = store.Load();

                StateCommitResult commit = store.Commit(
                    initial.Snapshot,
                    next);
                StateLoadResult recovered =
                    new XmlServiceDirectoryStateStore(
                        stateDirectory).Load();

                Assert.IsFalse(commit.IsSuccess);
                Assert.AreEqual(
                    StateCommitFailureCode.RecoveryRequired,
                    commit.FailureCode);
                Assert.IsTrue(commit.RequiresReload);
                Assert.IsTrue(recovered.IsSuccess);
                Assert.IsTrue(
                    DirectorySnapshotValueComparer.Equals(
                        next,
                        recovered.Snapshot));
                AssertJournalIsEmpty(stateDirectory);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void FaultBeforePreparedReturnsIoFailureAndCleansTransaction()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                var injector = new ThrowOnceFaultInjector(
                    RecoveryJournalFaultPoint.ImagesFlushed,
                    null);
                var store = new XmlServiceDirectoryStateStore(
                    stateDirectory,
                    injector);
                StateLoadResult initial = store.Load();

                StateCommitResult commit = store.Commit(
                    initial.Snapshot,
                    CreateActiveSnapshot());
                StateLoadResult reloaded =
                    new XmlServiceDirectoryStateStore(
                        stateDirectory).Load();

                Assert.IsFalse(commit.IsSuccess);
                Assert.AreEqual(
                    StateCommitFailureCode.IoFailure,
                    commit.FailureCode);
                Assert.IsFalse(commit.RequiresReload);
                Assert.IsTrue(reloaded.IsSuccess);
                Assert.AreEqual(0, reloaded.Snapshot.Records.Count);
                Assert.AreEqual(0UL, reloaded.Snapshot.LogicalClock);
                Assert.IsFalse(File.Exists(
                    Path.Combine(stateDirectory, "directory.xml")));
                Assert.IsFalse(File.Exists(
                    Path.Combine(stateDirectory, "pending.xml")));
                AssertJournalIsEmpty(stateDirectory);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        [DataRow(".preparing")]
        [DataRow(".complete")]
        public void LoadCleansCanonicalJournalRemnant(string suffix)
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                string journalDirectory = Path.Combine(
                    stateDirectory,
                    "journal");
                string remnantDirectory = Path.Combine(
                    journalDirectory,
                    "abcdefab-cdef-abcd-efab-cdefabcdefab" + suffix);
                Directory.CreateDirectory(remnantDirectory);
                File.WriteAllBytes(
                    Path.Combine(remnantDirectory, "remnant.bin"),
                    new byte[] { 1, 2, 3 });

                StateLoadResult loaded =
                    new XmlServiceDirectoryStateStore(
                        stateDirectory).Load();

                Assert.IsTrue(loaded.IsSuccess);
                Assert.AreEqual(StateLoadFailureCode.None, loaded.FailureCode);
                Assert.IsFalse(Directory.Exists(remnantDirectory));
                AssertJournalIsEmpty(stateDirectory);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void LoadReturnsRecoveryFailedForNoncanonicalJournalRootItem()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                string journalDirectory = Path.Combine(
                    stateDirectory,
                    "journal");
                string noncanonicalDirectory = Path.Combine(
                    journalDirectory,
                    "ABCDEFAB-CDEF-ABCD-EFAB-CDEFABCDEFAB.preparing");
                Directory.CreateDirectory(noncanonicalDirectory);

                StateLoadResult loaded =
                    new XmlServiceDirectoryStateStore(
                        stateDirectory).Load();

                Assert.IsFalse(loaded.IsSuccess);
                Assert.AreEqual(
                    StateLoadFailureCode.RecoveryFailed,
                    loaded.FailureCode);
                Assert.IsNull(loaded.Snapshot);
                Assert.IsTrue(Directory.Exists(noncanonicalDirectory));
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void StoreRejectsAStaleExpectedSnapshotBeforeWriting()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                var store = new XmlServiceDirectoryStateStore(
                    stateDirectory);
                StateLoadResult initial = store.Load();

                StateCommitResult commit = store.Commit(
                    CreateActiveSnapshot(),
                    CreateActiveSnapshot());

                Assert.AreEqual(
                    StateCommitFailureCode.RecoveryRequired,
                    commit.FailureCode);
                Assert.IsFalse(File.Exists(
                    Path.Combine(stateDirectory, "directory.xml")));
                Assert.IsTrue(initial.IsSuccess);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void LoadFailsClosedWhenAStateTargetIsADirectory()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                Directory.CreateDirectory(Path.Combine(
                    stateDirectory,
                    "directory.xml"));

                StateLoadResult loaded =
                    new XmlServiceDirectoryStateStore(
                        stateDirectory).Load();

                Assert.IsFalse(loaded.IsSuccess);
                Assert.AreEqual(
                    StateLoadFailureCode.IoFailure,
                    loaded.FailureCode);
                Assert.IsNull(loaded.Snapshot);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void LoadFailsClosedWhenOnlyABackupStateFileRemains()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                File.WriteAllBytes(
                    Path.Combine(stateDirectory, "directory.xml.bak"),
                    StrictUtf8.GetBytes("previous durable state"));

                StateLoadResult loaded =
                    new XmlServiceDirectoryStateStore(
                        stateDirectory).Load();

                Assert.IsFalse(loaded.IsSuccess);
                Assert.AreEqual(
                    StateLoadFailureCode.RecoveryFailed,
                    loaded.FailureCode);
                Assert.IsNull(loaded.Snapshot);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void PreparedUpdateRollbackDoesNotRetainCandidateAsBackup()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                DirectorySnapshot before = CreateActiveSnapshot();
                var initialStore = new XmlServiceDirectoryStateStore(
                    stateDirectory);
                StateLoadResult initial = initialStore.Load();
                Assert.IsTrue(initialStore.Commit(
                    initial.Snapshot,
                    before).IsSuccess);

                ServiceDefinition updatedDefinition;
                ServiceDefinitionValidationError validationError;
                Assert.IsTrue(ServiceDefinition.TryCreate(
                    "Updated Directory Service",
                    "AB12",
                    "service.internal",
                    21000,
                    out updatedDefinition,
                    out validationError));
                var after = new DirectorySnapshot(
                    new[]
                    {
                        ServiceRecord.CreateActive(
                            updatedDefinition,
                            TestData.Utc(2),
                            2UL,
                            TestData.OriginA)
                    },
                    new PendingRegistration[0],
                    2UL);
                var interruptedStore = new XmlServiceDirectoryStateStore(
                    stateDirectory,
                    new ThrowOnceFaultInjector(
                        RecoveryJournalFaultPoint.TargetApplied,
                        StateFileTarget.Directory));
                StateLoadResult loaded = interruptedStore.Load();

                StateCommitResult interrupted = interruptedStore.Commit(
                    loaded.Snapshot,
                    after);
                StateLoadResult recovered =
                    new XmlServiceDirectoryStateStore(
                        stateDirectory).Load();

                Assert.AreEqual(
                    StateCommitFailureCode.RecoveryRequired,
                    interrupted.FailureCode);
                Assert.IsTrue(recovered.IsSuccess);
                Assert.IsTrue(DirectorySnapshotValueComparer.Equals(
                    before,
                    recovered.Snapshot));
                CollectionAssert.AreEqual(
                    File.ReadAllBytes(Path.Combine(
                        stateDirectory,
                        "directory.xml")),
                    File.ReadAllBytes(Path.Combine(
                        stateDirectory,
                        "directory.xml.bak")));
                AssertJournalIsEmpty(stateDirectory);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void PreparedAbsenceRollbackResumesWithDurableDiscardFiles()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                var interruptedCommitStore =
                    new XmlServiceDirectoryStateStore(
                        stateDirectory,
                        new ThrowOnceFaultInjector(
                            RecoveryJournalFaultPoint.TargetApplied,
                            StateFileTarget.Directory));
                StateLoadResult initial = interruptedCommitStore.Load();
                StateCommitResult interruptedCommit =
                    interruptedCommitStore.Commit(
                        initial.Snapshot,
                        CreateActiveSnapshot());
                Assert.AreEqual(
                    StateCommitFailureCode.RecoveryRequired,
                    interruptedCommit.FailureCode);

                var interruptedRecoveryStore =
                    new XmlServiceDirectoryStateStore(
                        stateDirectory,
                        new ThrowOnceFaultInjector(
                            RecoveryJournalFaultPoint.CleanupStarting,
                            null));
                StateLoadResult interruptedRecovery =
                    interruptedRecoveryStore.Load();

                Assert.IsFalse(interruptedRecovery.IsSuccess);
                Assert.AreEqual(
                    StateLoadFailureCode.RecoveryFailed,
                    interruptedRecovery.FailureCode);
                string[] discardFiles = Directory.GetFiles(
                    Path.Combine(stateDirectory, "journal"),
                    "*.discard.bin",
                    SearchOption.AllDirectories);
                Assert.IsTrue(discardFiles.Length > 0);
                Assert.IsFalse(File.Exists(Path.Combine(
                    stateDirectory,
                    "directory.xml")));
                Assert.IsFalse(File.Exists(Path.Combine(
                    stateDirectory,
                    "directory.xml.bak")));

                StateLoadResult resumed =
                    new XmlServiceDirectoryStateStore(
                        stateDirectory).Load();

                Assert.IsTrue(resumed.IsSuccess);
                Assert.AreEqual(0, resumed.Snapshot.Records.Count);
                Assert.AreEqual(0UL, resumed.Snapshot.LogicalClock);
                AssertJournalIsEmpty(stateDirectory);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        private static DirectorySnapshot CreateActiveSnapshot()
        {
            ServiceRecord record = TestData.ActiveRecord(
                TestData.Definition(),
                1UL,
                TestData.OriginA);
            return new DirectorySnapshot(
                new[] { record },
                new PendingRegistration[0],
                1UL);
        }

        private static DirectorySnapshot CreateSnapshotWithPending()
        {
            ServiceRecord record = TestData.ActiveRecord(
                TestData.Definition(),
                4UL,
                TestData.OriginA);
            var pending = new PendingRegistration(
                new Guid("33333333-3333-3333-3333-333333333333"),
                PendingRequestType.Modify,
                TestData.Utc(1),
                "192.0.2.10",
                TestData.Definition(
                    name: "Updated Directory Service",
                    serverAddress: "2001:db8::10",
                    port: 22000),
                DirectoryBaseRevision.Capture(record));
            return new DirectorySnapshot(
                new[] { record },
                new[] { pending },
                4UL);
        }

        private static string CreateStateDirectory()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "dpai-sd-state-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteStateDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        private static void AssertJournalIsEmpty(string stateDirectory)
        {
            string journalPath = Path.Combine(
                stateDirectory,
                "journal");
            Assert.IsTrue(Directory.Exists(journalPath));
            Assert.AreEqual(
                0,
                Directory.GetFileSystemEntries(journalPath).Length);
        }

        private sealed class ThrowOnceFaultInjector
            : IRecoveryJournalFaultInjector
        {
            private readonly RecoveryJournalFaultPoint _faultPoint;
            private readonly StateFileTarget? _target;
            private bool _thrown;

            internal ThrowOnceFaultInjector(
                RecoveryJournalFaultPoint faultPoint,
                StateFileTarget? target)
            {
                _faultPoint = faultPoint;
                _target = target;
            }

            public void OnFault(
                RecoveryJournalFaultPoint faultPoint,
                StateFileTarget? target)
            {
                if (!_thrown
                    && faultPoint == _faultPoint
                    && target == _target)
                {
                    _thrown = true;
                    throw new IOException(
                        "Injected persistence interruption.");
                }
            }
        }
    }
}
