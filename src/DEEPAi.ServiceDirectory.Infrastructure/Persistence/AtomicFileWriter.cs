using System;
using System.IO;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal sealed class AtomicFileWriter
    {
        private const string BackupSuffix = ".bak";
        private readonly StateStoragePathPolicy _pathPolicy;
        private readonly IPeerSecretAccessPolicy _peerSecretAccessPolicy;

        public AtomicFileWriter(string stateDirectoryPath)
            : this(new StateStoragePathPolicy(stateDirectoryPath))
        {
        }

        internal AtomicFileWriter(StateStoragePathPolicy pathPolicy)
            : this(pathPolicy, null)
        {
        }

        internal AtomicFileWriter(
            StateStoragePathPolicy pathPolicy,
            IPeerSecretAccessPolicy peerSecretAccessPolicy)
        {
            if (pathPolicy == null)
            {
                throw new ArgumentNullException(nameof(pathPolicy));
            }

            _pathPolicy = pathPolicy;
            _peerSecretAccessPolicy = peerSecretAccessPolicy;
            _pathPolicy.EnsureStateDirectoryIsSafe();
        }

        public void Write(string fileName, byte[] contents)
        {
            StateFileTarget target;
            if (!StateFileTargets.TryParseXmlFileName(fileName, out target))
            {
                throw new ArgumentException(
                    "File name is not an approved service directory state document.",
                    nameof(fileName));
            }

            Write(target, contents);
        }

        internal void Write(StateFileTarget target, byte[] contents)
        {
            if (contents == null)
            {
                throw new ArgumentNullException(nameof(contents));
            }

            _pathPolicy.EnsureTargetParentIsSafe(target);
            string fullDestinationPath = _pathPolicy.GetTargetPath(target);
            string fullBackupPath = fullDestinationPath + BackupSuffix;
            bool maintainBackup = target != StateFileTarget.PeerSecret;
            _pathPolicy.EnsureExistingFileIsSafe(fullDestinationPath);
            _pathPolicy.EnsureExistingFileIsSafe(fullBackupPath);
            if (!maintainBackup && File.Exists(fullBackupPath))
            {
                throw new InvalidDataException(
                    "A peer secret backup credential is not allowed.");
            }

            string temporaryPath = Path.Combine(
                Path.GetDirectoryName(fullDestinationPath),
                Path.GetFileName(fullDestinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            bool temporaryFileCreated = false;
            bool writeCompleted = false;
            Exception writeFailure = null;
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    temporaryFileCreated = true;
                    if (target == StateFileTarget.PeerSecret)
                    {
                        if (_peerSecretAccessPolicy == null)
                        {
                            throw new InvalidOperationException(
                                "A peer secret access policy is required.");
                        }

                        // Apply the exact DACL before any DPAPI LocalMachine
                        // credential bytes reach the file.  Protecting only
                        // after the atomic move would leave a confidentiality
                        // window under the parent directory's inherited ACL.
                        _peerSecretAccessPolicy.ProtectExistingFile(
                            temporaryPath);
                    }

                    stream.Write(contents, 0, contents.Length);
                    stream.Flush(true);
                }

                bool destinationExists = File.Exists(fullDestinationPath);
                if (destinationExists && maintainBackup)
                {
                    ReplaceBackupFromTargetDurably(
                        fullDestinationPath,
                        fullBackupPath);
                }

                WindowsFileSystem.MoveWriteThrough(
                    temporaryPath,
                    fullDestinationPath,
                    destinationExists);
                temporaryFileCreated = false;

                FlushExistingFile(fullDestinationPath);
                if (target == StateFileTarget.PeerSecret)
                {
                    _peerSecretAccessPolicy.ValidateExistingFile(
                        fullDestinationPath);
                }
                writeCompleted = true;
            }
            catch (Exception exception)
            {
                writeFailure = exception;
                throw;
            }
            finally
            {
                if (!writeCompleted && temporaryFileCreated)
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (Exception cleanupFailure)
                    {
                        if (writeFailure != null)
                        {
                            throw new AggregateException(
                                "The state write and temporary file cleanup both failed.",
                                writeFailure,
                                cleanupFailure);
                        }

                        throw;
                    }
                }
            }
        }

        internal void Delete(StateFileTarget target)
        {
            _pathPolicy.EnsureTargetParentIsSafe(target);
            string targetPath = _pathPolicy.GetTargetPath(target);
            string backupPath = _pathPolicy.GetBackupPath(target);
            _pathPolicy.EnsureExistingFileIsSafe(targetPath);
            _pathPolicy.EnsureExistingFileIsSafe(backupPath);
            if (target == StateFileTarget.PeerSecret)
            {
                throw new InvalidOperationException(
                    "Peer secret deletion requires a recovery transaction.");
            }

            if (!File.Exists(targetPath))
            {
                return;
            }

            WindowsFileSystem.MoveWriteThrough(
                targetPath,
                backupPath,
                true);
        }

        internal void RestorePreparedImage(
            StateFileTarget target,
            bool exists,
            byte[] contents,
            string transactionPath)
        {
            if (exists != (contents != null))
            {
                throw new ArgumentException(
                    "Prepared recovery image metadata is inconsistent.",
                    nameof(contents));
            }

            _pathPolicy.EnsureTargetParentIsSafe(target);
            _pathPolicy.EnsureDirectoryIsSafe(transactionPath);
            string targetPath = _pathPolicy.GetTargetPath(target);
            string backupPath = _pathPolicy.GetBackupPath(target);
            _pathPolicy.EnsureExistingFileIsSafe(targetPath);
            _pathPolicy.EnsureExistingFileIsSafe(backupPath);

            if (!exists)
            {
                MoveTargetAndBackupToDiscards(
                    target,
                    targetPath,
                    backupPath,
                    transactionPath);
                return;
            }

            Write(target, contents);
            if (target == StateFileTarget.PeerSecret)
            {
                _pathPolicy.EnsureExistingFileIsSafe(backupPath);
                if (File.Exists(backupPath))
                {
                    throw new InvalidDataException(
                        "A peer secret backup credential is not allowed.");
                }

                return;
            }

            _pathPolicy.EnsureExistingFileIsSafe(targetPath);
            _pathPolicy.EnsureExistingFileIsSafe(backupPath);
            ReplaceBackupFromTargetDurably(
                targetPath,
                backupPath);
        }

        internal void DeleteForTransaction(
            StateFileTarget target,
            string transactionPath)
        {
            if (target != StateFileTarget.PeerSecret)
            {
                Delete(target);
                return;
            }

            _pathPolicy.EnsureTargetParentIsSafe(target);
            _pathPolicy.EnsureDirectoryIsSafe(transactionPath);
            string targetPath = _pathPolicy.GetTargetPath(target);
            string backupPath = _pathPolicy.GetBackupPath(target);
            _pathPolicy.EnsureExistingFileIsSafe(targetPath);
            _pathPolicy.EnsureExistingFileIsSafe(backupPath);
            if (target == StateFileTarget.PeerSecret
                && File.Exists(backupPath))
            {
                throw new InvalidDataException(
                    "A peer secret backup credential is not allowed.");
            }

            MoveTargetAndBackupToDiscards(
                target,
                targetPath,
                backupPath,
                transactionPath);
        }

        private void MoveTargetAndBackupToDiscards(
            StateFileTarget target,
            string targetPath,
            string backupPath,
            string transactionPath)
        {
            if (target == StateFileTarget.PeerSecret
                && File.Exists(backupPath))
            {
                throw new InvalidDataException(
                    "A peer secret backup credential is not allowed.");
            }

            StateFileTargetDescriptor descriptor =
                StateFileTargets.Get(target);
            MoveToTransactionDiscard(
                targetPath,
                Path.Combine(
                    transactionPath,
                    descriptor.PrimaryDiscardFileName));
            if (target == StateFileTarget.PeerSecret)
            {
                return;
            }

            MoveToTransactionDiscard(
                backupPath,
                Path.Combine(
                    transactionPath,
                    descriptor.BackupDiscardFileName));
        }

        private void MoveToTransactionDiscard(
            string sourcePath,
            string discardPath)
        {
            _pathPolicy.EnsurePathIsInsideStateDirectory(discardPath);
            _pathPolicy.EnsureExistingFileIsSafe(sourcePath);
            _pathPolicy.EnsureExistingFileIsSafe(discardPath);
            if (!File.Exists(sourcePath))
            {
                return;
            }

            WindowsFileSystem.MoveWriteThrough(
                sourcePath,
                discardPath,
                true);
            _pathPolicy.EnsureExistingFileIsSafe(discardPath);
        }

        internal bool Exists(StateFileTarget target)
        {
            _pathPolicy.EnsureTargetParentIsSafe(target);
            string path = _pathPolicy.GetTargetPath(target);
            _pathPolicy.EnsureExistingFileIsSafe(path);
            return File.Exists(path);
        }

        internal bool BackupExists(StateFileTarget target)
        {
            _pathPolicy.EnsureTargetParentIsSafe(target);
            string path = _pathPolicy.GetBackupPath(target);
            _pathPolicy.EnsureExistingFileIsSafe(path);
            return File.Exists(path);
        }

        internal byte[] Read(StateFileTarget target, int maximumBytes)
        {
            if (maximumBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            _pathPolicy.EnsureTargetParentIsSafe(target);
            string path = _pathPolicy.GetTargetPath(target);
            _pathPolicy.EnsureExistingFileIsSafe(path);
            return ReadExistingFile(path, maximumBytes);
        }

        internal byte[] ReadBackup(
            StateFileTarget target,
            int maximumBytes)
        {
            if (maximumBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            _pathPolicy.EnsureTargetParentIsSafe(target);
            string path = _pathPolicy.GetBackupPath(target);
            _pathPolicy.EnsureExistingFileIsSafe(path);
            return ReadExistingFile(path, maximumBytes);
        }

        private static byte[] ReadExistingFile(
            string path,
            int maximumBytes)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan))
            {
                if (stream.Length > maximumBytes)
                {
                    throw new InvalidDataException(
                        "The state file exceeds its configured size limit.");
                }

                var bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException(
                            "The state file ended before its declared length.");
                    }

                    offset += read;
                }

                if (stream.ReadByte() != -1)
                {
                    throw new InvalidDataException(
                        "The state file changed while it was being read.");
                }

                return bytes;
            }
        }

        private void FlushExistingFile(string path)
        {
            _pathPolicy.EnsureExistingFileIsSafe(path);
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Flush(true);
            }
        }

        private void ReplaceBackupFromTargetDurably(
            string targetPath,
            string backupPath)
        {
            string temporaryPath = backupPath
                + "."
                + Guid.NewGuid().ToString("N")
                + ".tmp";
            bool temporaryFileMayExist = true;
            try
            {
                File.Copy(
                    targetPath,
                    temporaryPath,
                    false);
                FlushExistingFile(temporaryPath);
                WindowsFileSystem.MoveWriteThrough(
                    temporaryPath,
                    backupPath,
                    true);
                temporaryFileMayExist = false;
            }
            finally
            {
                if (temporaryFileMayExist
                    && File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}
