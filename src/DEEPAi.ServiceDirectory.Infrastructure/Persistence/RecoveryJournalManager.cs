using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal sealed partial class RecoveryJournalManager
    {
        internal const int MaximumImageBytes = 16 * 1024 * 1024;
        private const string JournalFileName = "journal.xml";
        private const string PreparingDirectorySuffix = ".preparing";
        private const string CompletedDirectorySuffix = ".complete";
        private readonly StateStoragePathPolicy _pathPolicy;
        private readonly AtomicFileWriter _targetWriter;
        private readonly RecoveryJournalCodec _codec;
        private readonly IRecoveryJournalFaultInjector _faultInjector;

        internal RecoveryJournalManager(
            StateStoragePathPolicy pathPolicy,
            AtomicFileWriter targetWriter,
            IRecoveryJournalFaultInjector faultInjector)
        {
            if (pathPolicy == null)
            {
                throw new ArgumentNullException(nameof(pathPolicy));
            }

            if (targetWriter == null)
            {
                throw new ArgumentNullException(nameof(targetWriter));
            }

            _pathPolicy = pathPolicy;
            _targetWriter = targetWriter;
            _codec = new RecoveryJournalCodec();
            _faultInjector = faultInjector
                ?? NoOpRecoveryJournalFaultInjector.Instance;
        }

        internal void EnsureNoActiveTransaction()
        {
            string transactionPath;
            if (TryFindActiveTransaction(out transactionPath))
            {
                throw new RecoveryRequiredException(
                    "An active recovery transaction must be recovered before another commit.",
                    null);
            }
        }

        internal void Commit(
            IReadOnlyList<StateFileChange> changes,
            Action validateAppliedState)
        {
            if (changes == null)
            {
                throw new ArgumentNullException(nameof(changes));
            }

            if (validateAppliedState == null)
            {
                throw new ArgumentNullException(nameof(validateAppliedState));
            }

            ValidateChanges(changes);
            EnsureNoActiveTransaction();
            VerifyExpectedTargets(changes);

            Guid transactionId = Guid.NewGuid();
            string preparingPath = Path.Combine(
                _pathPolicy.JournalRootPath,
                transactionId.ToString("D") + PreparingDirectorySuffix);
            string transactionPath = Path.Combine(
                _pathPolicy.JournalRootPath,
                transactionId.ToString("D"));
            bool transactionCreated = false;
            bool preparedDurable = false;
            try
            {
                Directory.CreateDirectory(preparingPath);
                transactionCreated = true;
                _pathPolicy.EnsureDirectoryIsSafe(preparingPath);

                IReadOnlyList<RecoveryJournalEntry> entries =
                    WriteAndVerifyImages(preparingPath, changes);
                _faultInjector.OnFault(
                    RecoveryJournalFaultPoint.ImagesFlushed,
                    null);

                string journalPath = Path.Combine(
                    preparingPath,
                    JournalFileName);
                var prepared = new RecoveryJournalState(
                    transactionId,
                    RecoveryJournalPhase.Prepared,
                    entries);
                WriteNewDurableFile(
                    journalPath,
                    _codec.Serialize(prepared));
                WindowsFileSystem.MoveWriteThrough(
                    preparingPath,
                    transactionPath,
                    false);
                preparedDurable = true;
                _faultInjector.OnFault(
                    RecoveryJournalFaultPoint.PreparedFlushed,
                    null);

                journalPath = Path.Combine(
                    transactionPath,
                    JournalFileName);

                foreach (StateFileChange change in changes)
                {
                    ApplyImage(
                        change.Target,
                        change.AfterExists,
                        change.AfterBytes,
                        transactionPath);
                    VerifyTarget(
                        change.Target,
                        change.AfterExists,
                        change.AfterBytes);
                    _faultInjector.OnFault(
                        RecoveryJournalFaultPoint.TargetApplied,
                        change.Target);
                }

                validateAppliedState();
                var committed = new RecoveryJournalState(
                    transactionId,
                    RecoveryJournalPhase.Committed,
                    entries);
                WriteManifestAtomically(
                    journalPath,
                    _codec.Serialize(committed));
                _faultInjector.OnFault(
                    RecoveryJournalFaultPoint.CommittedFlushed,
                    null);
                CleanupTransaction(transactionPath);
            }
            catch (Exception exception)
            {
                if (!preparedDurable
                    && Directory.Exists(transactionPath))
                {
                    preparedDurable = true;
                }

                if (preparedDurable)
                {
                    throw new RecoveryRequiredException(
                        "The state transaction requires journal recovery.",
                        exception);
                }

                if (transactionCreated)
                {
                    try
                    {
                        CleanupUnpreparedTransaction(preparingPath);
                    }
                    catch (Exception cleanupException)
                    {
                        throw new RecoveryRequiredException(
                            "An unprepared state transaction could not be cleaned safely.",
                            new AggregateException(exception, cleanupException));
                    }
                }

                throw;
            }
        }

        internal bool Recover(Action validateRecoveredState)
        {
            return Recover(null, validateRecoveredState);
        }

        internal bool Recover(
            Action<IReadOnlyList<StateFileTarget>> validateRecoveryTargets,
            Action validateRecoveredState)
        {
            if (validateRecoveredState == null)
            {
                throw new ArgumentNullException(nameof(validateRecoveredState));
            }

            string transactionPath;
            try
            {
                if (!TryFindActiveTransaction(out transactionPath))
                {
                    return false;
                }

                PreflightTransaction transaction = Preflight(transactionPath);
                if (validateRecoveryTargets != null)
                {
                    var targets = new List<StateFileTarget>(
                        transaction.Entries.Count);
                    foreach (PreflightEntry entry in transaction.Entries)
                    {
                        targets.Add(entry.Entry.Target);
                    }

                    validateRecoveryTargets(targets.AsReadOnly());
                }

                bool useAfter = transaction.State.Phase
                    == RecoveryJournalPhase.Committed;
                foreach (PreflightEntry entry in transaction.Entries)
                {
                    bool desiredExists = useAfter
                        ? entry.Entry.AfterExists
                        : entry.Entry.BeforeExists;
                    byte[] desiredBytes = useAfter
                        ? entry.AfterBytes
                        : entry.BeforeBytes;
                    if (!useAfter)
                    {
                        _targetWriter.RestorePreparedImage(
                            entry.Entry.Target,
                            desiredExists,
                            desiredBytes,
                            transactionPath);
                    }
                    else if (!entry.CurrentMatches(
                                 desiredExists,
                                 desiredBytes))
                    {
                        ApplyImage(
                            entry.Entry.Target,
                            desiredExists,
                            desiredBytes,
                            transactionPath);
                    }

                    VerifyTarget(
                        entry.Entry.Target,
                        desiredExists,
                        desiredBytes);
                    _faultInjector.OnFault(
                        RecoveryJournalFaultPoint.TargetApplied,
                        entry.Entry.Target);
                }

                validateRecoveredState();
                CleanupTransaction(transactionPath);
                return true;
            }
            catch (RecoveryJournalException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new RecoveryJournalException(
                    "The active state transaction could not be recovered safely.",
                    exception);
            }
        }

        internal static string ComputeSha256(byte[] contents)
        {
            if (contents == null)
            {
                throw new ArgumentNullException(nameof(contents));
            }

            byte[] hash;
            using (SHA256 algorithm = SHA256.Create())
            {
                hash = algorithm.ComputeHash(contents);
            }

            var characters = new char[hash.Length * 2];
            const string hex = "0123456789abcdef";
            for (int index = 0; index < hash.Length; index++)
            {
                characters[index * 2] = hex[hash[index] >> 4];
                characters[(index * 2) + 1] = hex[hash[index] & 0x0f];
            }

            return new string(characters);
        }

        private PreflightTransaction Preflight(string transactionPath)
        {
            _pathPolicy.EnsureDirectoryIsSafe(transactionPath);
            Guid pathTransactionId;
            string directoryName = Path.GetFileName(transactionPath);
            if (!Guid.TryParseExact(
                    directoryName,
                    "D",
                    out pathTransactionId)
                || !StringComparer.Ordinal.Equals(
                    directoryName,
                    pathTransactionId.ToString("D")))
            {
                throw new InvalidDataException(
                    "The recovery transaction directory name is invalid.");
            }

            string journalPath = Path.Combine(
                transactionPath,
                JournalFileName);
            byte[] journalBytes = ReadLimitedFile(
                journalPath,
                RecoveryJournalCodec.MaximumJournalBytes);
            RecoveryJournalState state = _codec.Deserialize(
                journalBytes,
                pathTransactionId);

            var expectedFileNames = new HashSet<string>(
                StringComparer.Ordinal)
            {
                JournalFileName
            };
            var optionalDiscardFileNames = new HashSet<string>(
                StringComparer.Ordinal);
            var entries = new List<PreflightEntry>(state.Entries.Count);
            foreach (RecoveryJournalEntry entry in state.Entries)
            {
                StateFileTargetDescriptor descriptor =
                    StateFileTargets.Get(entry.Target);
                bool preparedDeletion =
                    state.Phase == RecoveryJournalPhase.Prepared
                    && !entry.AfterExists;
                bool preparedAbsenceRollback =
                    state.Phase == RecoveryJournalPhase.Prepared
                    && !entry.BeforeExists;
                bool committedDeletion =
                    state.Phase == RecoveryJournalPhase.Committed
                    && !entry.AfterExists;
                if (preparedDeletion
                    || preparedAbsenceRollback
                    || committedDeletion)
                {
                    optionalDiscardFileNames.Add(
                        descriptor.PrimaryDiscardFileName);
                    if (entry.Target != StateFileTarget.PeerSecret)
                    {
                        optionalDiscardFileNames.Add(
                            descriptor.BackupDiscardFileName);
                    }
                }

                byte[] beforeBytes = ReadAndVerifyImage(
                    transactionPath,
                    descriptor.BeforeImageFileName,
                    entry.BeforeExists,
                    entry.BeforeSha256,
                    expectedFileNames);
                byte[] afterBytes = ReadAndVerifyImage(
                    transactionPath,
                    descriptor.AfterImageFileName,
                    entry.AfterExists,
                    entry.AfterSha256,
                    expectedFileNames);

                bool currentExists = _targetWriter.Exists(entry.Target);
                byte[] currentBytes = currentExists
                    ? _targetWriter.Read(
                        entry.Target,
                        MaximumImageBytes)
                    : null;
                if (!ImageMatches(
                        currentExists,
                        currentBytes,
                        entry.BeforeExists,
                        beforeBytes)
                    && !ImageMatches(
                        currentExists,
                        currentBytes,
                        entry.AfterExists,
                        afterBytes))
                {
                    throw new InvalidDataException(
                        "A recovery target matches neither its before nor after image.");
                }

                entries.Add(new PreflightEntry(
                    entry,
                    beforeBytes,
                    afterBytes,
                    currentExists,
                    currentBytes));
            }

            FileSystemInfo[] actualItems = new DirectoryInfo(
                transactionPath).GetFileSystemInfos();
            bool manifestTemporaryFileFound = false;
            int discardFileCount = 0;
            foreach (FileSystemInfo actualItem in actualItems)
            {
                bool isManifestTemporaryFile =
                    IsCanonicalManifestTemporaryFileName(actualItem.Name);
                bool isTransactionDiscardFile =
                    optionalDiscardFileNames.Contains(actualItem.Name);
                if ((actualItem.Attributes & FileAttributes.ReparsePoint) != 0
                    || actualItem is DirectoryInfo
                    || (!expectedFileNames.Contains(actualItem.Name)
                        && !isManifestTemporaryFile
                        && !isTransactionDiscardFile)
                    || (isManifestTemporaryFile
                        && manifestTemporaryFileFound))
                {
                    throw new InvalidDataException(
                        "The recovery transaction contains an unexpected item.");
                }

                if (isManifestTemporaryFile)
                {
                    manifestTemporaryFileFound = true;
                }

                if (isTransactionDiscardFile)
                {
                    if (((FileInfo)actualItem).Length
                        > MaximumImageBytes)
                    {
                        throw new InvalidDataException(
                            "A recovery transaction discard exceeds its size limit.");
                    }

                    discardFileCount++;
                }

                _pathPolicy.EnsureExistingFileIsSafe(actualItem.FullName);
            }

            int expectedItemCount = expectedFileNames.Count
                + (manifestTemporaryFileFound ? 1 : 0)
                + discardFileCount;
            if (actualItems.Length != expectedItemCount)
            {
                throw new InvalidDataException(
                    "The recovery transaction image set is incomplete.");
            }

            return new PreflightTransaction(
                state,
                entries.AsReadOnly());
        }

        private IReadOnlyList<RecoveryJournalEntry> WriteAndVerifyImages(
            string transactionPath,
            IReadOnlyList<StateFileChange> changes)
        {
            var entries = new List<RecoveryJournalEntry>(changes.Count);
            foreach (StateFileChange change in changes)
            {
                StateFileTargetDescriptor descriptor =
                    StateFileTargets.Get(change.Target);
                string beforeHash = WriteAndVerifyImage(
                    transactionPath,
                    descriptor.BeforeImageFileName,
                    change.BeforeExists,
                    change.BeforeBytes);
                string afterHash = WriteAndVerifyImage(
                    transactionPath,
                    descriptor.AfterImageFileName,
                    change.AfterExists,
                    change.AfterBytes);
                entries.Add(new RecoveryJournalEntry(
                    change.Target,
                    change.BeforeExists,
                    change.AfterExists,
                    beforeHash,
                    afterHash));
            }

            return entries.AsReadOnly();
        }

        private string WriteAndVerifyImage(
            string transactionPath,
            string imageFileName,
            bool exists,
            byte[] contents)
        {
            if (!exists)
            {
                return null;
            }

            if (contents.Length > MaximumImageBytes)
            {
                throw new InvalidDataException(
                    "A recovery image exceeds its configured size limit.");
            }

            string imagePath = Path.Combine(
                transactionPath,
                imageFileName);
            WriteNewDurableFile(imagePath, contents);
            byte[] verified = ReadLimitedFile(
                imagePath,
                MaximumImageBytes);
            string expectedHash = ComputeSha256(contents);
            if (!StringComparer.Ordinal.Equals(
                expectedHash,
                ComputeSha256(verified)))
            {
                throw new IOException(
                    "A recovery image did not persist exactly.");
            }

            return expectedHash;
        }

        private byte[] ReadAndVerifyImage(
            string transactionPath,
            string imageFileName,
            bool exists,
            string expectedHash,
            ISet<string> expectedFileNames)
        {
            string imagePath = Path.Combine(
                transactionPath,
                imageFileName);
            if (!exists)
            {
                if (File.Exists(imagePath))
                {
                    throw new InvalidDataException(
                        "A recovery image exists when the journal marks it absent.");
                }

                return null;
            }

            expectedFileNames.Add(imageFileName);
            byte[] contents = ReadLimitedFile(
                imagePath,
                MaximumImageBytes);
            if (!StringComparer.Ordinal.Equals(
                expectedHash,
                ComputeSha256(contents)))
            {
                throw new InvalidDataException(
                    "A recovery image SHA-256 value does not match its journal entry.");
            }

            return contents;
        }

        private void ApplyImage(
            StateFileTarget target,
            bool exists,
            byte[] contents,
            string transactionPath)
        {
            if (exists)
            {
                _targetWriter.Write(target, contents);
            }
            else
            {
                _targetWriter.DeleteForTransaction(
                    target,
                    transactionPath);
            }
        }

        private void VerifyExpectedTargets(
            IReadOnlyList<StateFileChange> changes)
        {
            foreach (StateFileChange change in changes)
            {
                if (change.Target == StateFileTarget.PeerSecret
                    && _targetWriter.BackupExists(change.Target))
                {
                    throw new RecoveryRequiredException(
                        "A peer secret backup credential requires operator recovery.",
                        null);
                }

                bool actualExists = _targetWriter.Exists(change.Target);
                if (actualExists != change.BeforeExists)
                {
                    throw new RecoveryRequiredException(
                        "A state target changed after its baseline was loaded.",
                        null);
                }

                if (!actualExists)
                {
                    continue;
                }

                byte[] actualContents = _targetWriter.Read(
                    change.Target,
                    MaximumImageBytes);
                if (!StringComparer.Ordinal.Equals(
                        ComputeSha256(change.BeforeBytes),
                        ComputeSha256(actualContents)))
                {
                    throw new RecoveryRequiredException(
                        "A state target changed after its baseline was loaded.",
                        null);
                }
            }
        }

        private void VerifyTarget(
            StateFileTarget target,
            bool exists,
            byte[] expectedContents)
        {
            bool actualExists = _targetWriter.Exists(target);
            if (actualExists != exists)
            {
                throw new IOException(
                    "A state target existence flag did not persist exactly.");
            }

            if (!exists)
            {
                if (target == StateFileTarget.PeerSecret
                    && _targetWriter.BackupExists(target))
                {
                    throw new IOException(
                        "A committed peer secret deletion left a backup credential.");
                }

                return;
            }

            if (target == StateFileTarget.PeerSecret
                && _targetWriter.BackupExists(target))
            {
                throw new IOException(
                    "A peer secret state must not retain a backup credential.");
            }

            byte[] actualContents = _targetWriter.Read(
                target,
                MaximumImageBytes);
            if (!StringComparer.Ordinal.Equals(
                ComputeSha256(expectedContents),
                ComputeSha256(actualContents)))
            {
                throw new IOException(
                    "A state target did not persist exactly.");
            }
        }

        private bool TryFindActiveTransaction(out string transactionPath)
        {
            _pathPolicy.EnsureJournalRootExistsAndIsSafe();
            var journalRoot = new DirectoryInfo(
                _pathPolicy.JournalRootPath);
            FileSystemInfo[] items = journalRoot.GetFileSystemInfos();
            DirectoryInfo activeDirectory = null;
            var cleanupDirectories = new List<DirectoryInfo>();
            foreach (FileSystemInfo item in items)
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0
                    || !(item is DirectoryInfo))
                {
                    throw new RecoveryJournalException(
                        "The journal root contains an unexpected item.",
                        null);
                }

                var directory = (DirectoryInfo)item;
                if (IsCanonicalSuffixedDirectoryName(
                        directory.Name,
                        PreparingDirectorySuffix)
                    || IsCanonicalSuffixedDirectoryName(
                        directory.Name,
                        CompletedDirectorySuffix))
                {
                    cleanupDirectories.Add(directory);
                    continue;
                }

                Guid activeTransactionId;
                if (!Guid.TryParseExact(
                        directory.Name,
                        "D",
                        out activeTransactionId)
                    || !StringComparer.Ordinal.Equals(
                        directory.Name,
                        activeTransactionId.ToString("D")))
                {
                    throw new RecoveryJournalException(
                        "The journal root contains an invalid transaction directory.",
                        null);
                }

                if (activeDirectory != null)
                {
                    throw new RecoveryJournalException(
                        "More than one active recovery transaction exists.",
                        null);
                }

                activeDirectory = directory;
            }

            foreach (DirectoryInfo cleanupDirectory in cleanupDirectories)
            {
                CleanupRemnantDirectory(cleanupDirectory.FullName);
            }

            if (activeDirectory == null)
            {
                transactionPath = null;
                return false;
            }

            _pathPolicy.EnsureDirectoryIsSafe(activeDirectory.FullName);
            transactionPath = activeDirectory.FullName;
            return true;
        }

        private void WriteNewDurableFile(string path, byte[] contents)
        {
            _pathPolicy.EnsurePathIsInsideStateDirectory(path);
            _pathPolicy.EnsureExistingFileIsSafe(path);
            using (var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(contents, 0, contents.Length);
                stream.Flush(true);
            }
        }

        private void WriteManifestAtomically(string path, byte[] contents)
        {
            string directoryPath = Path.GetDirectoryName(path);
            string temporaryPath = Path.Combine(
                directoryPath,
                "journal." + Guid.NewGuid().ToString("N") + ".tmp");
            bool temporaryCreated = false;
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
                    temporaryCreated = true;
                    stream.Write(contents, 0, contents.Length);
                    stream.Flush(true);
                }

                _pathPolicy.EnsureExistingFileIsSafe(path);
                WindowsFileSystem.MoveWriteThrough(
                    temporaryPath,
                    path,
                    true);
                temporaryCreated = false;
                FlushExistingFile(path);
                byte[] persisted = ReadLimitedFile(
                    path,
                    RecoveryJournalCodec.MaximumJournalBytes);
                if (!ByteArraysEqual(contents, persisted))
                {
                    throw new IOException(
                        "The committed recovery journal did not persist exactly.");
                }
            }
            finally
            {
                if (temporaryCreated && File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private byte[] ReadLimitedFile(string path, int maximumBytes)
        {
            _pathPolicy.EnsurePathIsInsideStateDirectory(path);
            _pathPolicy.EnsureExistingFileIsSafe(path);
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan))
            {
                if (stream.Length <= 0 || stream.Length > maximumBytes)
                {
                    throw new InvalidDataException(
                        "A recovery journal file has an invalid size.");
                }

                var contents = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < contents.Length)
                {
                    int read = stream.Read(
                        contents,
                        offset,
                        contents.Length - offset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException(
                            "A recovery journal file ended early.");
                    }

                    offset += read;
                }

                if (stream.ReadByte() != -1)
                {
                    throw new InvalidDataException(
                        "A recovery journal file changed while being read.");
                }

                return contents;
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

        private void CleanupTransaction(string transactionPath)
        {
            _faultInjector.OnFault(
                RecoveryJournalFaultPoint.CleanupStarting,
                null);
            _pathPolicy.EnsureDirectoryIsSafe(transactionPath);
            string completedPath = transactionPath + CompletedDirectorySuffix;
            _pathPolicy.EnsurePathIsInsideStateDirectory(completedPath);
            if (Directory.Exists(completedPath)
                || File.Exists(completedPath))
            {
                throw new IOException(
                    "The completed recovery transaction path already exists.");
            }

            WindowsFileSystem.MoveWriteThrough(
                transactionPath,
                completedPath,
                false);
            CleanupRemnantDirectory(completedPath);
        }

        private void CleanupRemnantDirectory(string directoryPath)
        {
            _pathPolicy.EnsureDirectoryIsSafe(directoryPath);
            FileSystemInfo[] items = new DirectoryInfo(
                directoryPath).GetFileSystemInfos();
            foreach (FileSystemInfo item in items)
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0
                    || item is DirectoryInfo)
                {
                    throw new IOException(
                        "The recovery transaction cannot be cleaned safely.");
                }

                _pathPolicy.EnsureExistingFileIsSafe(item.FullName);
                File.Delete(item.FullName);
            }

            Directory.Delete(directoryPath, false);
        }

        private void CleanupUnpreparedTransaction(string transactionPath)
        {
            if (!Directory.Exists(transactionPath))
            {
                return;
            }

            CleanupRemnantDirectory(transactionPath);
        }
    }
}
