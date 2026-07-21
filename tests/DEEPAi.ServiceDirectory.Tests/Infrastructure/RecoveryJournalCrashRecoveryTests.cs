using System;
using System.IO;
using System.Text;
using DEEPAi.ServiceDirectory.Application.Persistence;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class RecoveryJournalCrashRecoveryTests
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void PreparedRecoveryIgnoresCanonicalCommittedManifestTemporaryFile()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                var pathPolicy = new StateStoragePathPolicy(
                    stateDirectory);
                var writer = new AtomicFileWriter(pathPolicy);
                byte[] before = StrictUtf8.GetBytes("before");
                byte[] after = StrictUtf8.GetBytes("after");
                writer.Write(StateFileTarget.Directory, before);
                writer.Write(StateFileTarget.Directory, after);

                Guid transactionId = new Guid(
                    "81818181-8181-8181-8181-818181818181");
                RecoveryJournalState prepared = CreateState(
                    transactionId,
                    RecoveryJournalPhase.Prepared,
                    before,
                    after);
                string transactionPath = WriteTransaction(
                    pathPolicy,
                    prepared,
                    before,
                    after);
                RecoveryJournalState committed = CreateState(
                    transactionId,
                    RecoveryJournalPhase.Committed,
                    before,
                    after);
                File.WriteAllBytes(
                    Path.Combine(
                        transactionPath,
                        "journal.91919191919191919191919191919191.tmp"),
                    new RecoveryJournalCodec().Serialize(committed));

                var manager = new RecoveryJournalManager(
                    pathPolicy,
                    writer,
                    null);

                Assert.IsTrue(manager.Recover(() => { }));
                CollectionAssert.AreEqual(
                    before,
                    File.ReadAllBytes(
                        Path.Combine(stateDirectory, "directory.xml")));
                Assert.AreEqual(
                    0,
                    Directory.GetFileSystemEntries(
                        pathPolicy.JournalRootPath).Length);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void CommittedRecoveryDoesNotRotateBackupWhenTargetAlreadyMatches()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                var pathPolicy = new StateStoragePathPolicy(
                    stateDirectory);
                var writer = new AtomicFileWriter(pathPolicy);
                byte[] before = StrictUtf8.GetBytes("before");
                byte[] after = StrictUtf8.GetBytes("after");
                writer.Write(StateFileTarget.Directory, before);
                writer.Write(StateFileTarget.Directory, after);

                Guid transactionId = new Guid(
                    "82828282-8282-8282-8282-828282828282");
                RecoveryJournalState committed = CreateState(
                    transactionId,
                    RecoveryJournalPhase.Committed,
                    before,
                    after);
                WriteTransaction(
                    pathPolicy,
                    committed,
                    before,
                    after);
                var manager = new RecoveryJournalManager(
                    pathPolicy,
                    writer,
                    null);

                Assert.IsTrue(manager.Recover(() => { }));
                CollectionAssert.AreEqual(
                    after,
                    File.ReadAllBytes(
                        Path.Combine(stateDirectory, "directory.xml")));
                CollectionAssert.AreEqual(
                    before,
                    File.ReadAllBytes(
                        Path.Combine(stateDirectory, "directory.xml.bak")));
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void RecoveryRejectsUnexpectedCurrentTargetBeforeChangingAnotherTarget()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                var pathPolicy = new StateStoragePathPolicy(
                    stateDirectory);
                var writer = new AtomicFileWriter(pathPolicy);
                byte[] directoryBefore = StrictUtf8.GetBytes(
                    "directory-before");
                byte[] directoryAfter = StrictUtf8.GetBytes(
                    "directory-after");
                byte[] peerCacheBefore = StrictUtf8.GetBytes(
                    "peer-cache-before");
                byte[] peerCacheAfter = StrictUtf8.GetBytes(
                    "peer-cache-after");
                Directory.CreateDirectory(Path.Combine(
                    stateDirectory,
                    "pki"));
                writer.Write(
                    StateFileTarget.Directory,
                    directoryAfter);
                writer.Write(
                    StateFileTarget.PeerPkiCache,
                    StrictUtf8.GetBytes("unexpected"));

                Guid transactionId = new Guid(
                    "83838383-8383-8383-8383-838383838383");
                var state = new RecoveryJournalState(
                    transactionId,
                    RecoveryJournalPhase.Prepared,
                    new[]
                    {
                        CreateEntry(
                            StateFileTarget.Directory,
                            directoryBefore,
                            directoryAfter),
                        CreateEntry(
                            StateFileTarget.PeerPkiCache,
                            peerCacheBefore,
                            peerCacheAfter)
                    });
                WriteTransaction(
                    pathPolicy,
                    state,
                    directoryBefore,
                    directoryAfter,
                    peerCacheBefore,
                    peerCacheAfter);
                var manager = new RecoveryJournalManager(
                    pathPolicy,
                    writer,
                    null);

                Assert.ThrowsExactly<RecoveryJournalException>(
                    () => manager.Recover(() => { }));
                CollectionAssert.AreEqual(
                    directoryAfter,
                    File.ReadAllBytes(
                        Path.Combine(stateDirectory, "directory.xml")));
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        [DataRow("directory.xml.bak")]
        public void LoadDoesNotOpenFreshStateWhenOnlyBackupRemains(
            string backupFileName)
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                File.WriteAllBytes(
                    Path.Combine(stateDirectory, backupFileName),
                    StrictUtf8.GetBytes("previous-state"));
                var store = new XmlServiceDirectoryStateStore(
                    stateDirectory);

                StateLoadResult result = store.Load();

                Assert.IsFalse(result.IsSuccess);
                Assert.AreEqual(
                    StateLoadFailureCode.RecoveryFailed,
                    result.FailureCode);
                Assert.IsNull(result.Snapshot);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void PeerSecretDeletionUsesTransactionDiscardUntilCleanup()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                Directory.CreateDirectory(Path.Combine(
                    stateDirectory,
                    "secrets"));
                var pathPolicy = new StateStoragePathPolicy(
                    stateDirectory);
                var writer = new AtomicFileWriter(
                    pathPolicy,
                    new NoOpSecretAccessPolicy());
                byte[] before = StrictUtf8.GetBytes("protected-peer-root");
                writer.Write(StateFileTarget.PeerSecret, before);
                var manager = new RecoveryJournalManager(
                    pathPolicy,
                    writer,
                    null);

                manager.Commit(
                    new[]
                    {
                        new StateFileChange(
                            StateFileTarget.PeerSecret,
                            true,
                            before,
                            false,
                            null)
                    },
                    () =>
                    {
                        Assert.IsFalse(
                            writer.Exists(StateFileTarget.PeerSecret));
                        Assert.AreEqual(
                            1,
                            Directory.GetFiles(
                                pathPolicy.JournalRootPath,
                                "*.discard.bin",
                                SearchOption.AllDirectories).Length);
                    });

                Assert.IsFalse(writer.Exists(StateFileTarget.PeerSecret));
                Assert.IsFalse(
                    writer.BackupExists(StateFileTarget.PeerSecret));
                Assert.AreEqual(
                    0,
                    Directory.GetFileSystemEntries(
                        pathPolicy.JournalRootPath).Length);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void PreparedPeerSecretDeletionRecoveryRestoresBeforeImage()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                Directory.CreateDirectory(Path.Combine(
                    stateDirectory,
                    "secrets"));
                var pathPolicy = new StateStoragePathPolicy(
                    stateDirectory);
                var writer = new AtomicFileWriter(
                    pathPolicy,
                    new NoOpSecretAccessPolicy());
                byte[] before = StrictUtf8.GetBytes("protected-peer-root");
                writer.Write(StateFileTarget.PeerSecret, before);
                var interruptedManager = new RecoveryJournalManager(
                    pathPolicy,
                    writer,
                    new ThrowOnceTargetAppliedFaultInjector(
                        StateFileTarget.PeerSecret));

                Assert.ThrowsExactly<RecoveryRequiredException>(
                    () => interruptedManager.Commit(
                        new[]
                        {
                            new StateFileChange(
                                StateFileTarget.PeerSecret,
                                true,
                                before,
                                false,
                                null)
                        },
                        () => { }));
                Assert.IsFalse(writer.Exists(StateFileTarget.PeerSecret));

                var recoveryManager = new RecoveryJournalManager(
                    pathPolicy,
                    writer,
                    null);
                Assert.IsTrue(recoveryManager.Recover(() => { }));

                CollectionAssert.AreEqual(
                    before,
                    writer.Read(
                        StateFileTarget.PeerSecret,
                        RecoveryJournalManager.MaximumImageBytes));
                Assert.IsFalse(
                    writer.BackupExists(StateFileTarget.PeerSecret));
                Assert.AreEqual(
                    0,
                    Directory.GetFileSystemEntries(
                        pathPolicy.JournalRootPath).Length);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        private static RecoveryJournalState CreateState(
            Guid transactionId,
            RecoveryJournalPhase phase,
            byte[] before,
            byte[] after)
        {
            return new RecoveryJournalState(
                transactionId,
                phase,
                new[]
                {
                    CreateEntry(
                        StateFileTarget.Directory,
                        before,
                        after)
                });
        }

        private static RecoveryJournalEntry CreateEntry(
            StateFileTarget target,
            byte[] before,
            byte[] after)
        {
            return new RecoveryJournalEntry(
                target,
                true,
                true,
                RecoveryJournalManager.ComputeSha256(before),
                RecoveryJournalManager.ComputeSha256(after));
        }

        private static string WriteTransaction(
            StateStoragePathPolicy pathPolicy,
            RecoveryJournalState state,
            byte[] directoryBefore,
            byte[] directoryAfter,
            byte[] peerCacheBefore = null,
            byte[] peerCacheAfter = null)
        {
            pathPolicy.EnsureJournalRootExistsAndIsSafe();
            string transactionPath = Path.Combine(
                pathPolicy.JournalRootPath,
                state.TransactionId.ToString("D"));
            Directory.CreateDirectory(transactionPath);
            WriteImage(
                transactionPath,
                "directory.before.bin",
                directoryBefore);
            WriteImage(
                transactionPath,
                "directory.after.bin",
                directoryAfter);
            if (peerCacheBefore != null)
            {
                WriteImage(
                    transactionPath,
                    "peer-pki.before.bin",
                    peerCacheBefore);
                WriteImage(
                    transactionPath,
                    "peer-pki.after.bin",
                    peerCacheAfter);
            }

            File.WriteAllBytes(
                Path.Combine(transactionPath, "journal.xml"),
                new RecoveryJournalCodec().Serialize(state));
            return transactionPath;
        }

        private static void WriteImage(
            string transactionPath,
            string fileName,
            byte[] contents)
        {
            File.WriteAllBytes(
                Path.Combine(transactionPath, fileName),
                contents);
        }

        private static string CreateStateDirectory()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "dpai-sd-crash-recovery-tests-"
                    + Guid.NewGuid().ToString("N"));
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

        private sealed class ThrowOnceTargetAppliedFaultInjector
            : IRecoveryJournalFaultInjector
        {
            private readonly StateFileTarget _target;
            private bool _thrown;

            internal ThrowOnceTargetAppliedFaultInjector(
                StateFileTarget target)
            {
                _target = target;
            }

            public void OnFault(
                RecoveryJournalFaultPoint point,
                StateFileTarget? target)
            {
                if (_thrown
                    || point != RecoveryJournalFaultPoint.TargetApplied
                    || target != _target)
                {
                    return;
                }

                _thrown = true;
                throw new IOException("Injected recovery journal fault.");
            }
        }

        private sealed class NoOpSecretAccessPolicy
            : ISecretFileAccessPolicy
        {
            public void ProtectExistingFile(string path)
            {
            }

            public void ValidateExistingFile(string path)
            {
            }
        }
    }
}
