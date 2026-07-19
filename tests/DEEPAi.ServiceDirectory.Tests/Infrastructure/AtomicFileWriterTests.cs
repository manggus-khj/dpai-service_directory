using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class AtomicFileWriterTests
    {
        [TestMethod]
        public void WriteCreatesInitialFileWithoutAssumingReplaceSupport()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                byte[] expected = Encoding.UTF8.GetBytes("first");
                var writer = new AtomicFileWriter(stateDirectory);

                writer.Write("directory.xml", expected);

                string destination = Path.Combine(stateDirectory, "directory.xml");
                CollectionAssert.AreEqual(expected, File.ReadAllBytes(destination));
                Assert.IsFalse(File.Exists(destination + ".bak"));
                Assert.AreEqual(0, Directory.GetFiles(stateDirectory, "*.tmp").Length);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void WriteAtomicallyReplacesExistingFileAndKeepsBackup()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                byte[] first = Encoding.UTF8.GetBytes("first");
                byte[] second = Encoding.UTF8.GetBytes("second");
                var writer = new AtomicFileWriter(stateDirectory);
                writer.Write("pending.xml", first);

                writer.Write("pending.xml", second);

                string destination = Path.Combine(stateDirectory, "pending.xml");
                CollectionAssert.AreEqual(second, File.ReadAllBytes(destination));
                CollectionAssert.AreEqual(first, File.ReadAllBytes(destination + ".bak"));
                Assert.AreEqual(0, Directory.GetFiles(stateDirectory, "*.tmp").Length);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void WriteRejectsFileOutsideFixedStateDocumentAllowlist()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                var writer = new AtomicFileWriter(stateDirectory);

                Assert.ThrowsExactly<ArgumentException>(
                    () => writer.Write("..\\outside.xml", new byte[0]));

                Assert.AreEqual(0, Directory.GetFiles(stateDirectory).Length);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void PeerSecretReplacementAndDeletionNeverLeaveABackupCredential()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                Directory.CreateDirectory(Path.Combine(
                    stateDirectory,
                    "secrets"));
                var writer = new AtomicFileWriter(
                    new StateStoragePathPolicy(stateDirectory),
                    new NoOpSecretAccessPolicy());
                byte[] first = Encoding.UTF8.GetBytes("protected-first");
                byte[] second = Encoding.UTF8.GetBytes("protected-second");

                writer.Write(StateFileTarget.PeerSecret, first);
                writer.Write(StateFileTarget.PeerSecret, second);

                string peerPath = Path.Combine(
                    stateDirectory,
                    "secrets",
                    "peer.dat");
                CollectionAssert.AreEqual(second, File.ReadAllBytes(peerPath));
                Assert.IsFalse(File.Exists(peerPath + ".bak"));

                string transactionPath = Path.Combine(
                    stateDirectory,
                    "journal",
                    Guid.NewGuid().ToString("D"));
                Directory.CreateDirectory(transactionPath);
                writer.DeleteForTransaction(
                    StateFileTarget.PeerSecret,
                    transactionPath);

                Assert.IsFalse(File.Exists(peerPath));
                Assert.IsFalse(File.Exists(peerPath + ".bak"));
                Assert.AreEqual(
                    1,
                    Directory.GetFiles(
                        transactionPath,
                        "*.discard.bin").Length);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void PeerSecretWriteFailsClosedWhenUnexpectedBackupExists()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                string secretsPath = Path.Combine(
                    stateDirectory,
                    "secrets");
                Directory.CreateDirectory(secretsPath);
                string peerPath = Path.Combine(
                    secretsPath,
                    "peer.dat");
                File.WriteAllBytes(
                    peerPath + ".bak",
                    Encoding.UTF8.GetBytes("unexpected-backup"));
                var writer = new AtomicFileWriter(stateDirectory);

                Assert.ThrowsExactly<InvalidDataException>(
                    () => writer.Write(
                        StateFileTarget.PeerSecret,
                        Encoding.UTF8.GetBytes("protected")));

                Assert.IsFalse(File.Exists(peerPath));
                Assert.IsTrue(File.Exists(peerPath + ".bak"));
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        private static string CreateStateDirectory()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "dpai-sd-tests-" + Guid.NewGuid().ToString("N"));
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
