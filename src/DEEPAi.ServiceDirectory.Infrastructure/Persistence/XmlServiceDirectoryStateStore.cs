using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using DEEPAi.ServiceDirectory.Application.Persistence;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    public sealed class XmlServiceDirectoryStateStore
        : IServiceDirectoryStateStore
    {
        private const int MaximumStateDocumentBytes = 16 * 1024 * 1024;
        private const int ErrorHandleDiskFull = 39;
        private const int ErrorDiskFull = 112;

        private readonly object _gate = new object();
        private readonly StateStoragePathPolicy _pathPolicy;
        private readonly AtomicFileWriter _fileWriter;
        private readonly RecoveryJournalManager _journalManager;
        private readonly StateXmlCodec _codec;
        private PersistedState _baseline;

        public XmlServiceDirectoryStateStore(string stateDirectoryPath)
            : this(
                stateDirectoryPath,
                NoOpRecoveryJournalFaultInjector.Instance)
        {
        }

        internal XmlServiceDirectoryStateStore(
            string stateDirectoryPath,
            IRecoveryJournalFaultInjector faultInjector)
        {
            var pathPolicy = new StateStoragePathPolicy(
                stateDirectoryPath);
            _pathPolicy = pathPolicy;
            _fileWriter = new AtomicFileWriter(pathPolicy);
            _journalManager = new RecoveryJournalManager(
                pathPolicy,
                _fileWriter,
                faultInjector);
            _codec = new StateXmlCodec();
        }

        public StateLoadResult Load()
        {
            lock (_gate)
            {
                _baseline = null;
                try
                {
                    _journalManager.Recover(
                        ValidateRecoveryTargets,
                        ValidateRecoveredState);
                    PersistedState current = ReadCurrentState();
                    _baseline = current;
                    return StateLoadResult.Success(current.Snapshot);
                }
                catch (RecoveryJournalException)
                {
                    return StateLoadResult.Failure(
                        StateLoadFailureCode.RecoveryFailed);
                }
                catch (UnauthorizedAccessException)
                {
                    return StateLoadResult.Failure(
                        StateLoadFailureCode.AccessDenied);
                }
                catch (SecurityException)
                {
                    return StateLoadResult.Failure(
                        StateLoadFailureCode.AccessDenied);
                }
                catch (InvalidDataException)
                {
                    return StateLoadResult.Failure(
                        StateLoadFailureCode.InvalidData);
                }
                catch (IOException)
                {
                    return StateLoadResult.Failure(
                        StateLoadFailureCode.IoFailure);
                }
            }
        }

        public StateCommitResult Commit(
            DirectorySnapshot expectedSnapshot,
            DirectorySnapshot nextSnapshot)
        {
            if (expectedSnapshot == null)
            {
                throw new ArgumentNullException(nameof(expectedSnapshot));
            }

            if (nextSnapshot == null)
            {
                throw new ArgumentNullException(nameof(nextSnapshot));
            }

            if (nextSnapshot.LogicalClock < expectedSnapshot.LogicalClock)
            {
                throw new ArgumentException(
                    "The next snapshot logical clock must not decrease.",
                    nameof(nextSnapshot));
            }

            lock (_gate)
            {
                if (_baseline == null
                    || !DirectorySnapshotValueComparer.Equals(
                        _baseline.Snapshot,
                        expectedSnapshot))
                {
                    return RequireRecovery();
                }

                try
                {
                    _journalManager.EnsureNoActiveTransaction();
                    PersistedState current = ReadCurrentState();
                    if (!_baseline.RawEquals(current))
                    {
                        return RequireRecovery();
                    }

                    byte[] directoryContents =
                        _codec.SerializeDirectory(nextSnapshot);
                    DirectorySnapshot roundTrip = _codec.DeserializeSnapshot(
                        directoryContents);
                    if (!DirectorySnapshotValueComparer.Equals(
                            nextSnapshot,
                            roundTrip))
                    {
                        throw new InvalidDataException(
                            "The next snapshot did not survive XML serialization exactly.");
                    }

                    IReadOnlyList<StateFileChange> changes = BuildChanges(
                        current,
                        directoryContents);
                    if (changes.Count == 0)
                    {
                        _baseline = new PersistedState(
                            nextSnapshot,
                            current.DirectoryExists,
                            current.DirectoryContents);
                        return StateCommitResult.Success();
                    }

                    PersistedState appliedState = null;
                    _journalManager.Commit(
                        changes,
                        () =>
                        {
                            PersistedState candidate = ReadCurrentState();
                            if (!DirectorySnapshotValueComparer.Equals(
                                    candidate.Snapshot,
                                    nextSnapshot))
                            {
                                throw new InvalidDataException(
                                    "The applied state does not match the requested snapshot.");
                            }

                            appliedState = candidate;
                        });

                    if (appliedState == null)
                    {
                        throw new InvalidOperationException(
                            "The state transaction completed without validating its result.");
                    }

                    _baseline = appliedState;
                    return StateCommitResult.Success();
                }
                catch (RecoveryRequiredException)
                {
                    return RequireRecovery();
                }
                catch (RecoveryJournalException)
                {
                    return RequireRecovery();
                }
                catch (UnauthorizedAccessException)
                {
                    return StateCommitResult.Failure(
                        StateCommitFailureCode.AccessDenied);
                }
                catch (SecurityException)
                {
                    return StateCommitResult.Failure(
                        StateCommitFailureCode.AccessDenied);
                }
                catch (InvalidDataException)
                {
                    return RequireRecovery();
                }
                catch (IOException exception)
                {
                    return StateCommitResult.Failure(
                        IsDiskFull(exception)
                            ? StateCommitFailureCode.DiskFull
                            : StateCommitFailureCode.IoFailure);
                }
            }
        }

        private PersistedState ReadCurrentState()
        {
            _pathPolicy.EnsureForbiddenLegacyStateIsAbsent();
            bool directoryExists = _fileWriter.Exists(
                StateFileTarget.Directory);
            if (!directoryExists)
            {
                if (_fileWriter.BackupExists(StateFileTarget.Directory))
                {
                    throw new RecoveryJournalException(
                        "Backup state exists without its primary directory document.",
                        null);
                }

                throw new InvalidDataException(
                    "The installed directory.xml state document is missing.");
            }

            byte[] directoryContents = _fileWriter.Read(
                StateFileTarget.Directory,
                MaximumStateDocumentBytes);
            DirectorySnapshot snapshot = _codec.DeserializeSnapshot(
                directoryContents);
            return new PersistedState(
                snapshot,
                true,
                directoryContents);
        }

        private void ValidateRecoveredState()
        {
            ReadCurrentState();
        }

        private static void ValidateRecoveryTargets(
            IReadOnlyList<StateFileTarget> targets)
        {
            foreach (StateFileTarget target in targets)
            {
                if (target != StateFileTarget.Directory)
                {
                    throw new InvalidDataException(
                        "This state store cannot validate a config or peer-secret transaction.");
                }
            }
        }

        private static IReadOnlyList<StateFileChange> BuildChanges(
            PersistedState current,
            byte[] nextDirectoryContents)
        {
            var changes = new List<StateFileChange>(1);
            if (!current.DirectoryExists
                || !ByteArraysEqual(
                    current.DirectoryContents,
                    nextDirectoryContents))
            {
                changes.Add(new StateFileChange(
                    StateFileTarget.Directory,
                    current.DirectoryExists,
                    current.DirectoryContents,
                    true,
                    nextDirectoryContents));
            }

            return changes.AsReadOnly();
        }

        private StateCommitResult RequireRecovery()
        {
            _baseline = null;
            return StateCommitResult.Failure(
                StateCommitFailureCode.RecoveryRequired);
        }

        private static bool IsDiskFull(IOException exception)
        {
            int nativeError = exception.HResult & 0xffff;
            return nativeError == ErrorHandleDiskFull
                || nativeError == ErrorDiskFull;
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null
                || right == null
                || left.Length != right.Length)
            {
                return false;
            }

            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class PersistedState
        {
            internal PersistedState(
                DirectorySnapshot snapshot,
                bool directoryExists,
                byte[] directoryContents)
            {
                Snapshot = snapshot
                    ?? throw new ArgumentNullException(nameof(snapshot));
                ValidateImage(
                    directoryExists,
                    directoryContents,
                    nameof(directoryContents));
                DirectoryExists = directoryExists;
                DirectoryContents = Clone(directoryContents);
            }

            internal DirectorySnapshot Snapshot { get; }

            internal bool DirectoryExists { get; }

            internal byte[] DirectoryContents { get; }

            internal bool RawEquals(PersistedState other)
            {
                return other != null
                    && DirectoryExists == other.DirectoryExists
                    && ByteArraysEqual(
                        DirectoryContents,
                        other.DirectoryContents);
            }

            private static void ValidateImage(
                bool exists,
                byte[] contents,
                string parameterName)
            {
                if (exists != (contents != null))
                {
                    throw new ArgumentException(
                        "A persisted state image has inconsistent existence metadata.",
                        parameterName);
                }
            }

            private static byte[] Clone(byte[] contents)
            {
                return contents == null
                    ? null
                    : (byte[])contents.Clone();
            }
        }
    }
}
