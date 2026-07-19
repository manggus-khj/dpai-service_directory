using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class RecoveryJournalTests
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public void CodecRoundTripsCanonicalFixedTargets()
        {
            Guid transactionId = new Guid(
                "44444444-4444-4444-4444-444444444444");
            string hash = new string('a', 64);
            var state = new RecoveryJournalState(
                transactionId,
                RecoveryJournalPhase.Prepared,
                new List<RecoveryJournalEntry>
                {
                    new RecoveryJournalEntry(
                        StateFileTarget.Directory,
                        true,
                        true,
                        hash,
                        hash),
                    new RecoveryJournalEntry(
                        StateFileTarget.Pending,
                        false,
                        true,
                        null,
                        hash),
                    new RecoveryJournalEntry(
                        StateFileTarget.Config,
                        true,
                        false,
                        hash,
                        null),
                    new RecoveryJournalEntry(
                        StateFileTarget.PeerSecret,
                        true,
                        true,
                        hash,
                        hash)
                }.AsReadOnly());
            var codec = new RecoveryJournalCodec();

            RecoveryJournalState actual = codec.Deserialize(
                codec.Serialize(state),
                transactionId);

            Assert.AreEqual(transactionId, actual.TransactionId);
            Assert.AreEqual(
                RecoveryJournalPhase.Prepared,
                actual.Phase);
            Assert.AreEqual(4, actual.Entries.Count);
            Assert.AreEqual(
                StateFileTarget.PeerSecret,
                actual.Entries[3].Target);
        }

        [TestMethod]
        public void CodecRejectsUnknownJournalAttribute()
        {
            Guid transactionId = new Guid(
                "55555555-5555-5555-5555-555555555555");
            byte[] image = StrictUtf8.GetBytes("state");
            string hash = RecoveryJournalManager.ComputeSha256(image);
            var state = new RecoveryJournalState(
                transactionId,
                RecoveryJournalPhase.Prepared,
                new[]
                {
                    new RecoveryJournalEntry(
                        StateFileTarget.Directory,
                        true,
                        true,
                        hash,
                        hash)
                });
            var codec = new RecoveryJournalCodec();
            string xml = StrictUtf8.GetString(codec.Serialize(state));
            byte[] invalid = StrictUtf8.GetBytes(
                xml.Replace(
                    "Phase=\"PREPARED\"",
                    "Phase=\"PREPARED\" Unexpected=\"value\""));

            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.Deserialize(invalid, transactionId));
        }

        [TestMethod]
        public void CodecRejectsEntryWithoutBeforeOrAfterImage()
        {
            Guid transactionId = new Guid(
                "56565656-5656-5656-5656-565656565656");
            string xml =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
                + "<RecoveryJournal SchemaVersion=\"1\" TransactionId=\""
                + transactionId.ToString("D")
                + "\" Phase=\"PREPARED\">\n"
                + "  <Entry Target=\"Directory\" BeforeExists=\"false\" AfterExists=\"false\" />\n"
                + "</RecoveryJournal>";
            var codec = new RecoveryJournalCodec();

            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.Deserialize(
                    StrictUtf8.GetBytes(xml),
                    transactionId));
        }

        [TestMethod]
        public void RecoveryPreflightsEveryImageBeforeChangingAnyTarget()
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
                byte[] pendingBefore = StrictUtf8.GetBytes(
                    "pending-before");
                byte[] pendingAfter = StrictUtf8.GetBytes(
                    "pending-after");
                writer.Write(StateFileTarget.Directory, directoryBefore);
                writer.Write(StateFileTarget.Pending, pendingBefore);

                Guid transactionId = new Guid(
                    "66666666-6666-6666-6666-666666666666");
                string transactionPath = Path.Combine(
                    pathPolicy.JournalRootPath,
                    transactionId.ToString("D"));
                pathPolicy.EnsureJournalRootExistsAndIsSafe();
                Directory.CreateDirectory(transactionPath);
                WriteImage(
                    transactionPath,
                    "directory.before.bin",
                    directoryBefore);
                WriteImage(
                    transactionPath,
                    "directory.after.bin",
                    directoryAfter);
                WriteImage(
                    transactionPath,
                    "pending.before.bin",
                    pendingBefore);
                WriteImage(
                    transactionPath,
                    "pending.after.bin",
                    StrictUtf8.GetBytes("corrupted-after"));

                var journalState = new RecoveryJournalState(
                    transactionId,
                    RecoveryJournalPhase.Committed,
                    new[]
                    {
                        CreateEntry(
                            StateFileTarget.Directory,
                            directoryBefore,
                            directoryAfter),
                        CreateEntry(
                            StateFileTarget.Pending,
                            pendingBefore,
                            pendingAfter)
                    });
                File.WriteAllBytes(
                    Path.Combine(transactionPath, "journal.xml"),
                    new RecoveryJournalCodec().Serialize(journalState));
                var manager = new RecoveryJournalManager(
                    pathPolicy,
                    writer,
                    null);

                Assert.ThrowsExactly<RecoveryJournalException>(
                    () => manager.Recover(() => { }));

                CollectionAssert.AreEqual(
                    directoryBefore,
                    File.ReadAllBytes(
                        Path.Combine(stateDirectory, "directory.xml")));
                CollectionAssert.AreEqual(
                    pendingBefore,
                    File.ReadAllBytes(
                        Path.Combine(stateDirectory, "pending.xml")));
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void LoadCleansInterruptedPreparingDirectoryWithoutApplyingIt()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                WriteCanonicalEmptyState(stateDirectory);
                string journalRoot = Path.Combine(
                    stateDirectory,
                    "journal");
                string preparingPath = Path.Combine(
                    journalRoot,
                    "77777777-7777-7777-7777-777777777777.preparing");
                Directory.CreateDirectory(preparingPath);
                File.WriteAllBytes(
                    Path.Combine(preparingPath, "directory.after.bin"),
                    StrictUtf8.GetBytes("must-not-be-applied"));

                var store = new XmlServiceDirectoryStateStore(
                    stateDirectory);
                var result = store.Load();

                Assert.IsTrue(result.IsSuccess);
                Assert.AreEqual(0, result.Snapshot.Records.Count);
                Assert.AreEqual(
                    0,
                    Directory.GetFileSystemEntries(journalRoot).Length);
                Assert.IsTrue(File.Exists(
                    Path.Combine(stateDirectory, "directory.xml")));
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
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

        private static void WriteImage(
            string transactionPath,
            string fileName,
            byte[] contents)
        {
            File.WriteAllBytes(
                Path.Combine(transactionPath, fileName),
                contents);
        }

        private static void WriteCanonicalEmptyState(string stateDirectory)
        {
            var codec = new StateXmlCodec();
            DirectorySnapshot empty = DirectorySnapshot.Empty();
            File.WriteAllBytes(
                Path.Combine(stateDirectory, "directory.xml"),
                codec.SerializeDirectory(empty));
            File.WriteAllBytes(
                Path.Combine(stateDirectory, "pending.xml"),
                codec.SerializePending(empty));
        }

        private static string CreateStateDirectory()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "dpai-sd-journal-tests-" + Guid.NewGuid().ToString("N"));
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
    }
}
