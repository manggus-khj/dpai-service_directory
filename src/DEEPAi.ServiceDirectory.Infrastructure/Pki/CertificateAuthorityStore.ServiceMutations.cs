using System;
using System.Collections.Generic;
using System.IO;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal enum CertificateServiceMutationOperation
    {
        Registration = 0,
        Reregistration = 1,
        Renewal = 2,
        ScheduledRetirement = 3,
        Deletion = 4,
        SerialRevocation = 5
    }

    internal sealed partial class CertificateAuthorityStore
    {
        internal CertificateAuthorityStoreSnapshot CommitServiceMutation(
            StateMutationCoordinator directoryState,
            CertificateServiceMutationOperation operation,
            DirectorySnapshot expectedDirectory,
            DirectorySnapshot nextDirectory,
            CertificateAuthorityState nextState,
            CertificateLedgerSnapshot nextLedger,
            byte[] nextCrlDer,
            DateTime utcNow)
        {
            if (directoryState == null)
            {
                throw new ArgumentNullException(nameof(directoryState));
            }

            ValidateServiceMutationArguments(
                operation,
                expectedDirectory,
                nextDirectory,
                nextState,
                nextLedger,
                nextCrlDer,
                utcNow);

            CertificateAuthorityStoreSnapshot committed = null;
            bool coordinatorAdvanced;
            try
            {
                coordinatorAdvanced = _mutationGate.Execute(() =>
                    directoryState.TryCommitExternalMutation(
                        expectedDirectory,
                        () =>
                        {
                            lock (_lifecycleGate)
                            {
                                ThrowIfAvailable();
                                try
                                {
                                    CommitServiceMutationCore(
                                        operation,
                                        expectedDirectory,
                                        nextDirectory,
                                        nextState,
                                        nextLedger,
                                        nextCrlDer,
                                        utcNow);
                                    committed = _current.Clone();
                                }
                                catch (RecoveryRequiredException)
                                {
                                    _recoveryRequired = true;
                                    throw;
                                }
                            }
                        }));
            }
            catch
            {
                lock (_lifecycleGate)
                {
                    if (committed != null)
                    {
                        committed.Dispose();
                        committed = null;
                        _recoveryRequired = true;
                    }
                }

                throw;
            }

            if (!coordinatorAdvanced)
            {
                lock (_lifecycleGate)
                {
                    if (committed != null)
                    {
                        committed.Dispose();
                        committed = null;
                        _recoveryRequired = true;
                        throw new RecoveryRequiredException(
                            "The certificate transaction committed but the directory baseline could not be refreshed.",
                            null);
                    }
                }

                throw new InvalidOperationException(
                    "The directory snapshot is stale or unavailable for mutation.");
            }

            if (committed == null)
            {
                throw new InvalidOperationException(
                    "The certificate transaction completed without a PKI snapshot.");
            }

            return committed;
        }

        private void CommitServiceMutationCore(
            CertificateServiceMutationOperation operation,
            DirectorySnapshot expectedDirectory,
            DirectorySnapshot nextDirectory,
            CertificateAuthorityState nextState,
            CertificateLedgerSnapshot nextLedger,
            byte[] nextCrlDer,
            DateTime utcNow)
        {
            if (_current.State.Role != CertificateAuthorityRole.ActiveIssuer
                || !_current.State.LastBackupUtc.HasValue)
            {
                throw new InvalidOperationException(
                    "The CA is not ready for certificate service mutations.");
            }

            byte[] currentDirectoryBytes = null;
            byte[] nextDirectoryBytes = null;
            byte[] nextMetadataBytes = null;
            byte[] nextLedgerBytes = null;
            byte[] privateKey = null;
            CertificateAuthorityStoreSnapshot applied = null;
            try
            {
                currentDirectoryBytes = _fileWriter.Read(
                    StateFileTarget.Directory,
                    RecoveryJournalManager.MaximumImageBytes);
                var directoryCodec = new StateXmlCodec();
                DirectorySnapshot persistedDirectory =
                    directoryCodec.DeserializeSnapshot(currentDirectoryBytes);
                if (!DirectorySnapshotValueComparer.Equals(
                        persistedDirectory,
                        expectedDirectory))
                {
                    throw new RecoveryRequiredException(
                        "directory.xml changed outside the coordinated mutation baseline.",
                        null);
                }

                nextDirectoryBytes = directoryCodec.SerializeDirectory(
                    nextDirectory);
                DirectorySnapshot roundTripDirectory =
                    directoryCodec.DeserializeSnapshot(nextDirectoryBytes);
                if (!DirectorySnapshotValueComparer.Equals(
                        nextDirectory,
                        roundTripDirectory))
                {
                    throw new InvalidDataException(
                        "The next directory snapshot did not survive canonical serialization.");
                }

                bool directoryChanged = !ByteArraysEqual(
                    currentDirectoryBytes,
                    nextDirectoryBytes);
                bool crlChanged = !ByteArraysEqual(
                    _current.CrlDer,
                    nextCrlDer);
                ValidateServiceMutationHighWater(
                    operation,
                    expectedDirectory,
                    nextDirectory,
                    nextState,
                    nextLedger,
                    directoryChanged,
                    crlChanged);
                ValidateUnchangedAuthorityState(nextState);
                ValidateStateAndLedger(nextState, nextLedger);
                StorageV1ConsistencyValidator
                    .ValidateActiveDirectoryAndLedger(
                        nextDirectory,
                        nextLedger);

                privateKey = _protector.Unprotect(
                    _current.ProtectedPrivateKey);
                ValidateAuthority(
                    nextState,
                    _current.CaCertificateDer,
                    privateKey,
                    utcNow);
                ValidateLedgerCertificates(
                    nextLedger,
                    _current.CaCertificateDer);
                ValidateCrl(
                    nextState,
                    nextLedger,
                    _current.CaCertificateDer,
                    nextCrlDer);

                nextMetadataBytes = _codec.SerializeState(nextState);
                nextLedgerBytes = _codec.SerializeLedger(nextLedger);
                IReadOnlyList<StateFileChange> changes =
                    BuildServiceMutationChanges(
                        directoryChanged,
                        currentDirectoryBytes,
                        nextDirectoryBytes,
                        nextMetadataBytes,
                        nextLedgerBytes,
                        crlChanged,
                        nextCrlDer);
                ValidateServiceMutationTargets(
                    operation,
                    directoryChanged,
                    changes);

                _journalManager.Commit(
                    changes,
                    () =>
                    {
                        byte[] appliedDirectoryBytes = null;
                        try
                        {
                            appliedDirectoryBytes = _fileWriter.Read(
                                StateFileTarget.Directory,
                                RecoveryJournalManager.MaximumImageBytes);
                            if (!ByteArraysEqual(
                                    appliedDirectoryBytes,
                                    nextDirectoryBytes))
                            {
                                throw new InvalidDataException(
                                    "The applied directory bytes do not match the requested mutation.");
                            }

                            applied = ReadCurrent(false);
                            if (!ByteArraysEqual(
                                    applied.MetadataBytes,
                                    nextMetadataBytes)
                                || !ByteArraysEqual(
                                    applied.LedgerBytes,
                                    nextLedgerBytes)
                                || !ByteArraysEqual(
                                    applied.CrlDer,
                                    nextCrlDer))
                            {
                                throw new InvalidDataException(
                                    "The applied PKI bytes do not match the requested mutation.");
                            }

                            StorageV1ConsistencyValidator
                                .ValidateActiveDirectoryAndLedger(
                                    nextDirectory,
                                    applied.Ledger);
                        }
                        catch
                        {
                            if (applied != null)
                            {
                                applied.Dispose();
                                applied = null;
                            }

                            throw;
                        }
                        finally
                        {
                            Clear(appliedDirectoryBytes);
                        }
                    });

                if (applied == null)
                {
                    throw new InvalidOperationException(
                        "The certificate service mutation completed without validation.");
                }

                ReplaceCurrent(applied);
                applied = null;
            }
            finally
            {
                if (applied != null)
                {
                    applied.Dispose();
                }

                Clear(currentDirectoryBytes);
                Clear(nextDirectoryBytes);
                Clear(nextMetadataBytes);
                Clear(nextLedgerBytes);
                Clear(privateKey);
            }
        }

        private void ValidateUnchangedAuthorityState(
            CertificateAuthorityState nextState)
        {
            byte[] currentSpki = _current.State.GetCaSpkiSha256();
            byte[] nextSpki = nextState.GetCaSpkiSha256();
            try
            {
                if (nextState.SiteId != _current.State.SiteId
                    || nextState.IssuerInstanceId
                        != _current.State.IssuerInstanceId
                    || nextState.Role != _current.State.Role
                    || nextState.CaSerialNumber
                        != _current.State.CaSerialNumber
                    || nextState.NotBeforeUtc != _current.State.NotBeforeUtc
                    || nextState.NotAfterUtc != _current.State.NotAfterUtc
                    || nextState.LastBackupUtc
                        != _current.State.LastBackupUtc
                    || !FixedTimeEquals(currentSpki, nextSpki))
                {
                    throw new InvalidDataException(
                        "A certificate service mutation cannot replace CA identity or backup state.");
                }
            }
            finally
            {
                Clear(currentSpki);
                Clear(nextSpki);
            }
        }

        private void ValidateServiceMutationHighWater(
            CertificateServiceMutationOperation operation,
            DirectorySnapshot expectedDirectory,
            DirectorySnapshot nextDirectory,
            CertificateAuthorityState nextState,
            CertificateLedgerSnapshot nextLedger,
            bool directoryChanged,
            bool crlChanged)
        {
            bool requiresDirectory = RequiresDirectoryTarget(operation);
            bool forbidsDirectory = ForbidsDirectoryTarget(operation);
            if ((requiresDirectory && !directoryChanged)
                || (forbidsDirectory && directoryChanged))
            {
                throw new InvalidDataException(
                    "The directory target does not match the certificate service operation.");
            }

            if (directoryChanged)
            {
                if (expectedDirectory.LogicalClock == ulong.MaxValue
                    || nextDirectory.LogicalClock
                        != expectedDirectory.LogicalClock + 1)
                {
                    throw new InvalidDataException(
                        "A directory-changing certificate mutation must advance LogicalClock exactly once.");
                }
            }
            else if (!DirectorySnapshotValueComparer.Equals(
                         expectedDirectory,
                         nextDirectory))
            {
                throw new InvalidDataException(
                    "A mutation without the directory target must preserve its snapshot exactly.");
            }

            if (_current.State.PkiRevision == ulong.MaxValue
                || nextState.PkiRevision
                    != _current.State.PkiRevision + 1
                || nextLedger.PkiRevision != nextState.PkiRevision)
            {
                throw new InvalidDataException(
                    "A certificate service mutation must advance PkiRevision exactly once.");
            }

            bool requiresCrl = RequiresCrlTarget(operation);
            ulong expectedCrlNumber = _current.State.CrlNumber;
            if (requiresCrl)
            {
                if (expectedCrlNumber == ulong.MaxValue)
                {
                    throw new OverflowException(
                        "The CRL number high-water value is exhausted.");
                }

                expectedCrlNumber++;
            }

            if (crlChanged != requiresCrl
                || nextState.CrlNumber != expectedCrlNumber
                || nextLedger.CrlNumber != expectedCrlNumber)
            {
                throw new InvalidDataException(
                    "The CRL target or high-water value does not match the certificate service operation.");
            }
        }

        private IReadOnlyList<StateFileChange> BuildServiceMutationChanges(
            bool directoryChanged,
            byte[] currentDirectoryBytes,
            byte[] nextDirectoryBytes,
            byte[] nextMetadataBytes,
            byte[] nextLedgerBytes,
            bool crlChanged,
            byte[] nextCrlDer)
        {
            var changes = new List<StateFileChange>(4);
            if (directoryChanged)
            {
                changes.Add(new StateFileChange(
                    StateFileTarget.Directory,
                    true,
                    currentDirectoryBytes,
                    true,
                    nextDirectoryBytes));
            }

            if (!ByteArraysEqual(_current.MetadataBytes, nextMetadataBytes))
            {
                changes.Add(new StateFileChange(
                    StateFileTarget.PkiMetadata,
                    true,
                    _current.MetadataBytes,
                    true,
                    nextMetadataBytes));
            }

            if (!ByteArraysEqual(_current.LedgerBytes, nextLedgerBytes))
            {
                changes.Add(new StateFileChange(
                    StateFileTarget.CertificateLedger,
                    true,
                    _current.LedgerBytes,
                    true,
                    nextLedgerBytes));
            }

            if (crlChanged)
            {
                changes.Add(new StateFileChange(
                    StateFileTarget.CertificateRevocationList,
                    true,
                    _current.CrlDer,
                    true,
                    nextCrlDer));
            }

            return changes.AsReadOnly();
        }

        private static void ValidateServiceMutationTargets(
            CertificateServiceMutationOperation operation,
            bool directoryChanged,
            IReadOnlyList<StateFileChange> changes)
        {
            var expected = new List<StateFileTarget>(4);
            if (directoryChanged)
            {
                expected.Add(StateFileTarget.Directory);
            }

            expected.Add(StateFileTarget.PkiMetadata);
            expected.Add(StateFileTarget.CertificateLedger);
            if (RequiresCrlTarget(operation))
            {
                expected.Add(StateFileTarget.CertificateRevocationList);
            }

            if (changes.Count != expected.Count)
            {
                throw new InvalidDataException(
                    "The certificate service mutation does not contain its exact target set.");
            }

            for (int index = 0; index < expected.Count; index++)
            {
                if (changes[index].Target != expected[index])
                {
                    throw new InvalidDataException(
                        "The certificate service mutation target order is not canonical.");
                }
            }
        }

        private static void ValidateServiceMutationArguments(
            CertificateServiceMutationOperation operation,
            DirectorySnapshot expectedDirectory,
            DirectorySnapshot nextDirectory,
            CertificateAuthorityState nextState,
            CertificateLedgerSnapshot nextLedger,
            byte[] nextCrlDer,
            DateTime utcNow)
        {
            if (!Enum.IsDefined(
                    typeof(CertificateServiceMutationOperation),
                    operation))
            {
                throw new ArgumentOutOfRangeException(nameof(operation));
            }

            if (expectedDirectory == null
                || nextDirectory == null
                || nextState == null
                || nextLedger == null
                || nextCrlDer == null)
            {
                throw new ArgumentNullException(
                    expectedDirectory == null
                        ? nameof(expectedDirectory)
                        : nextDirectory == null
                            ? nameof(nextDirectory)
                            : nextState == null
                                ? nameof(nextState)
                                : nextLedger == null
                                    ? nameof(nextLedger)
                                    : nameof(nextCrlDer));
            }

            if (expectedDirectory.PendingCount != 0
                || nextDirectory.PendingCount != 0)
            {
                throw new ArgumentException(
                    "Target v1 certificate mutations cannot contain pending registrations.",
                    nameof(nextDirectory));
            }

            if (nextCrlDer.Length == 0
                || nextCrlDer.Length > MaximumCrlBytes)
            {
                throw new ArgumentException(
                    "The CRL DER size is outside the supported range.",
                    nameof(nextCrlDer));
            }

            EnsureUtc(utcNow, nameof(utcNow));
        }

        private static bool RequiresDirectoryTarget(
            CertificateServiceMutationOperation operation)
        {
            return operation
                    == CertificateServiceMutationOperation.Registration
                || operation
                    == CertificateServiceMutationOperation.Reregistration
                || operation
                    == CertificateServiceMutationOperation.Deletion;
        }

        private static bool ForbidsDirectoryTarget(
            CertificateServiceMutationOperation operation)
        {
            return operation
                    == CertificateServiceMutationOperation
                        .ScheduledRetirement
                || operation
                    == CertificateServiceMutationOperation.SerialRevocation;
        }

        private static bool RequiresCrlTarget(
            CertificateServiceMutationOperation operation)
        {
            return operation
                    == CertificateServiceMutationOperation.Reregistration
                || operation
                    == CertificateServiceMutationOperation
                        .ScheduledRetirement
                || operation
                    == CertificateServiceMutationOperation.Deletion
                || operation
                    == CertificateServiceMutationOperation.SerialRevocation;
        }
    }
}
