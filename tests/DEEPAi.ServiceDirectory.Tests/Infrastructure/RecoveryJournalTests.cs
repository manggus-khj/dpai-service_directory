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
                        StateFileTarget.Config,
                        false,
                        true,
                        null,
                        hash),
                    new RecoveryJournalEntry(
                        StateFileTarget.PeerSecret,
                        true,
                        false,
                        hash,
                        null),
                    new RecoveryJournalEntry(
                        StateFileTarget.PkiMetadata,
                        true,
                        true,
                        hash,
                        hash),
                    new RecoveryJournalEntry(
                        StateFileTarget.CertificateLedger,
                        true,
                        true,
                        hash,
                        hash),
                    new RecoveryJournalEntry(
                        StateFileTarget.PeerPkiCache,
                        true,
                        true,
                        hash,
                        hash),
                    new RecoveryJournalEntry(
                        StateFileTarget.CertificateRevocationList,
                        true,
                        true,
                        hash,
                        hash),
                    new RecoveryJournalEntry(
                        StateFileTarget.CaCertificate,
                        true,
                        true,
                        hash,
                        hash),
                    new RecoveryJournalEntry(
                        StateFileTarget.CaPrivateKey,
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
            Assert.AreEqual(9, actual.Entries.Count);
            Assert.AreEqual(
                StateFileTarget.CaPrivateKey,
                actual.Entries[8].Target);
        }

        [TestMethod]
        public void FixedTargetDescriptorsMatchStorageV1Allowlist()
        {
            StateFileTargetDescriptor[] expected =
            {
                Descriptor(
                    StateFileTarget.Directory,
                    "Directory",
                    "directory.xml",
                    "directory.before.bin",
                    "directory.after.bin"),
                Descriptor(
                    StateFileTarget.Config,
                    "Config",
                    "config.xml",
                    "config.before.bin",
                    "config.after.bin"),
                Descriptor(
                    StateFileTarget.PeerSecret,
                    "PeerSecret",
                    @"secrets\peer.dat",
                    "peer.before.bin",
                    "peer.after.bin"),
                Descriptor(
                    StateFileTarget.PkiMetadata,
                    "PkiMetadata",
                    @"pki\state.xml",
                    "pki-state.before.bin",
                    "pki-state.after.bin"),
                Descriptor(
                    StateFileTarget.CertificateLedger,
                    "CertificateLedger",
                    @"pki\ledger.xml",
                    "ledger.before.bin",
                    "ledger.after.bin"),
                Descriptor(
                    StateFileTarget.PeerPkiCache,
                    "PeerPkiCache",
                    @"pki\peer-cache.xml",
                    "peer-pki.before.bin",
                    "peer-pki.after.bin"),
                Descriptor(
                    StateFileTarget.CertificateRevocationList,
                    "CertificateRevocationListA",
                    @"pki\crl-a.der",
                    "crl-a.before.bin",
                    "crl-a.after.bin"),
                Descriptor(
                    StateFileTarget.CaCertificate,
                    "CaCertificateA",
                    @"pki\ca-a.der",
                    "ca-a.before.bin",
                    "ca-a.after.bin"),
                Descriptor(
                    StateFileTarget.CaPrivateKey,
                    "CaPrivateKeyA",
                    @"secrets\ca-a.key",
                    "ca-a-key.before.bin",
                    "ca-a-key.after.bin"),
                Descriptor(
                    StateFileTarget.CertificateRevocationListB,
                    "CertificateRevocationListB",
                    @"pki\crl-b.der",
                    "crl-b.before.bin",
                    "crl-b.after.bin"),
                Descriptor(
                    StateFileTarget.CaCertificateB,
                    "CaCertificateB",
                    @"pki\ca-b.der",
                    "ca-b.before.bin",
                    "ca-b.after.bin"),
                Descriptor(
                    StateFileTarget.CaPrivateKeyB,
                    "CaPrivateKeyB",
                    @"secrets\ca-b.key",
                    "ca-b-key.before.bin",
                    "ca-b-key.after.bin"),
                Descriptor(
                    StateFileTarget.RetiredAuthorities,
                    "RetiredAuthorities",
                    @"pki\retired-authorities.xml",
                    "retired-authorities.before.bin",
                    "retired-authorities.after.bin")
            };

            Assert.AreEqual(expected.Length, StateFileTargets.All.Count);
            for (int index = 0; index < expected.Length; index++)
            {
                StateFileTargetDescriptor actual =
                    StateFileTargets.All[index];
                Assert.AreEqual(expected[index].Target, actual.Target);
                Assert.AreEqual(
                    expected[index].JournalName,
                    actual.JournalName);
                Assert.AreEqual(
                    expected[index].RelativePath,
                    actual.RelativePath);
                Assert.AreEqual(
                    expected[index].BeforeImageFileName,
                    actual.BeforeImageFileName);
                Assert.AreEqual(
                    expected[index].AfterImageFileName,
                    actual.AfterImageFileName);
            }
        }

        [TestMethod]
        public void CodecWritesExactCanonicalJournalBytes()
        {
            Guid transactionId = new Guid(
                "44444444-4444-4444-4444-444444444444");
            string beforeHash = new string('a', 64);
            string afterHash = new string('b', 64);
            var state = new RecoveryJournalState(
                transactionId,
                RecoveryJournalPhase.Prepared,
                new[]
                {
                    new RecoveryJournalEntry(
                        StateFileTarget.Directory,
                        true,
                        true,
                        beforeHash,
                        afterHash)
                });
            string expected =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
                + "<RecoveryJournal SchemaVersion=\"1\" TransactionId=\"44444444-4444-4444-4444-444444444444\" Phase=\"PREPARED\">\r\n"
                + "  <Entry Target=\"Directory\" BeforeExists=\"true\" AfterExists=\"true\" BeforeSha256=\""
                + beforeHash
                + "\" AfterSha256=\""
                + afterHash
                + "\" />\r\n"
                + "</RecoveryJournal>\r\n";

            CollectionAssert.AreEqual(
                StrictUtf8.GetBytes(expected),
                new RecoveryJournalCodec().Serialize(state));
        }

        [TestMethod]
        public void CodecRejectsDuplicateOrOutOfOrderTargets()
        {
            string hash = new string('a', 64);
            var state = new RecoveryJournalState(
                Guid.NewGuid(),
                RecoveryJournalPhase.Prepared,
                new[]
                {
                    new RecoveryJournalEntry(
                        StateFileTarget.Config,
                        true,
                        true,
                        hash,
                        hash),
                    new RecoveryJournalEntry(
                        StateFileTarget.Directory,
                        true,
                        true,
                        hash,
                        hash)
                });

            Assert.ThrowsExactly<InvalidDataException>(() =>
                new RecoveryJournalCodec().Serialize(state));
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
                byte[] peerCacheBefore = StrictUtf8.GetBytes(
                    "peer-cache-before");
                byte[] peerCacheAfter = StrictUtf8.GetBytes(
                    "peer-cache-after");
                writer.Write(StateFileTarget.Directory, directoryBefore);
                Directory.CreateDirectory(Path.Combine(
                    stateDirectory,
                    "pki"));
                writer.Write(
                    StateFileTarget.PeerPkiCache,
                    peerCacheBefore);

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
                    "peer-pki.before.bin",
                    peerCacheBefore);
                WriteImage(
                    transactionPath,
                    "peer-pki.after.bin",
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
                            StateFileTarget.PeerPkiCache,
                            peerCacheBefore,
                            peerCacheAfter)
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
                    peerCacheBefore,
                    File.ReadAllBytes(
                        Path.Combine(
                            stateDirectory,
                            "pki",
                            "peer-cache.xml")));
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

        private static StateFileTargetDescriptor Descriptor(
            StateFileTarget target,
            string journalName,
            string relativePath,
            string beforeImage,
            string afterImage)
        {
            return new StateFileTargetDescriptor(
                target,
                journalName,
                relativePath,
                beforeImage,
                afterImage,
                "unused.primary.discard.bin",
                "unused.backup.discard.bin");
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
