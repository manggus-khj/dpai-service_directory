using System;
using System.Collections.Generic;
using System.IO;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal sealed class PeerConfigurationSnapshot : IDisposable
    {
        private PairedPeerCredential _credential;
        private bool _disposed;

        internal PeerConfigurationSnapshot(
            Guid storeId,
            long generation,
            ServiceDirectoryConfiguration configuration,
            PairedPeerCredential credential)
        {
            StoreId = storeId;
            Generation = generation;
            Configuration = configuration
                ?? throw new ArgumentNullException(nameof(configuration));
            _credential = credential == null
                ? null
                : credential.Clone();
        }

        internal Guid StoreId { get; }

        internal long Generation { get; }

        internal ServiceDirectoryConfiguration Configuration { get; }

        internal bool IsDisposed => _disposed;

        internal PairedPeerCredential Credential
        {
            get
            {
                ThrowIfDisposed();
                return _credential;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_credential != null)
            {
                _credential.Dispose();
                _credential = null;
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PeerConfigurationSnapshot));
            }
        }
    }

    // Owns config.xml + peer.dat transactions. Load also recovers and validates
    // every approved fixed state target, so startup never has to guess which
    // scoped store created the single active recovery journal.
    internal sealed class PeerConfigurationTransactionStore : IDisposable
    {
        private const int MaximumConfigurationBytes = 16 * 1024 * 1024;
        private const int MaximumStateDocumentBytes = 16 * 1024 * 1024;

        private readonly object _gate = new object();
        private readonly Guid _storeId = Guid.NewGuid();
        private readonly AtomicFileWriter _fileWriter;
        private readonly RecoveryJournalManager _journalManager;
        private readonly StateXmlCodec _stateCodec;
        private readonly PeerCredentialFile _peerCredentialFile;
        private readonly StateStoragePathPolicy _pathPolicy;
        private readonly IPeerSecretAccessPolicy _secretAccessPolicy;

        private PersistedPeerConfiguration _baseline;
        private long _generation;
        private bool _disposed;

        internal PeerConfigurationTransactionStore(
            string stateDirectoryPath,
            IPeerCredentialProtector protector,
            IPeerSecretAccessPolicy accessPolicy)
            : this(
                stateDirectoryPath,
                protector,
                accessPolicy,
                NoOpRecoveryJournalFaultInjector.Instance)
        {
        }

        internal PeerConfigurationTransactionStore(
            string stateDirectoryPath,
            IPeerCredentialProtector protector,
            IPeerSecretAccessPolicy accessPolicy,
            IRecoveryJournalFaultInjector faultInjector)
        {
            var pathPolicy = new StateStoragePathPolicy(
                stateDirectoryPath);
            _pathPolicy = pathPolicy;
            _secretAccessPolicy = accessPolicy;
            _fileWriter = new AtomicFileWriter(pathPolicy, accessPolicy);
            _journalManager = new RecoveryJournalManager(
                pathPolicy,
                _fileWriter,
                faultInjector);
            _stateCodec = new StateXmlCodec();
            _peerCredentialFile = new PeerCredentialFile(
                pathPolicy,
                protector,
                accessPolicy);
        }

        internal PeerConfigurationSnapshot Load()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                DisposeBaseline();
                _journalManager.Recover(
                    ValidateKnownRecoveryTargets,
                    ValidateAllRecoveredState);
                PersistedPeerConfiguration current = ReadCurrent(true);
                if (!current.Exists)
                {
                    current.Dispose();
                    throw new FileNotFoundException(
                        "The installed config.xml is missing.");
                }

                _baseline = current;
                _generation = NextGeneration(_generation);
                return CreateSnapshot();
            }
        }

        internal PeerConfigurationSnapshot Commit(
            PeerConfigurationSnapshot expected,
            ServiceDirectoryConfiguration nextConfiguration,
            PairedPeerCredential nextCredential)
        {
            if (expected == null)
            {
                throw new ArgumentNullException(nameof(expected));
            }

            if (nextConfiguration == null)
            {
                throw new ArgumentNullException(nameof(nextConfiguration));
            }

            PeerCredentialConfigurationValidator.Validate(
                nextConfiguration,
                nextCredential);

            lock (_gate)
            {
                ThrowIfDisposed();
                RequireCurrentSnapshot(expected);
                ValidateImmutableConfiguration(
                    expected.Configuration,
                    nextConfiguration);
                _journalManager.EnsureNoActiveTransaction();

                PersistedPeerConfiguration current = ReadCurrent(false);
                byte[] nextConfigurationBytes = null;
                byte[] nextPeerBytes = null;
                PersistedPeerConfiguration applied = null;
                try
                {
                    if (!_baseline.RawEquals(current))
                    {
                        throw new RecoveryRequiredException(
                            "config.xml or peer.dat changed outside the loaded baseline.",
                            null);
                    }

                    nextConfigurationBytes =
                        _stateCodec.SerializeConfiguration(
                            nextConfiguration);
                    ServiceDirectoryConfiguration roundTrip =
                        _stateCodec.DeserializeConfiguration(
                            nextConfigurationBytes);
                    if (!ConfigurationValueComparer.Equals(
                            nextConfiguration,
                            roundTrip))
                    {
                        throw new InvalidDataException(
                            "The next configuration did not survive canonical serialization.");
                    }

                    if (nextCredential != null)
                    {
                        nextPeerBytes =
                            PeerCredentialValueComparer.Equals(
                                current.Credential,
                                nextCredential)
                                ? Clone(current.PeerProtectedBytes)
                                : _peerCredentialFile.EncodeProtected(
                                    nextCredential);
                    }

                    IReadOnlyList<StateFileChange> changes = BuildChanges(
                        current,
                        nextConfigurationBytes,
                        nextPeerBytes);
                    if (changes.Count == 0)
                    {
                        ReplaceBaseline(
                            new PersistedPeerConfiguration(
                                true,
                                nextConfiguration,
                                nextConfigurationBytes,
                                nextCredential,
                                nextPeerBytes));
                        _generation = NextGeneration(_generation);
                        return CreateSnapshot();
                    }

                    _journalManager.Commit(
                        changes,
                        () =>
                        {
                            PersistedPeerConfiguration candidate =
                                ReadCurrent(false);
                            if (!candidate.ValueEquals(
                                    nextConfiguration,
                                    nextCredential))
                            {
                                candidate.Dispose();
                                throw new InvalidDataException(
                                    "The applied config.xml and peer.dat do not match the requested state.");
                            }

                            applied = candidate;
                        });

                    if (applied == null)
                    {
                        throw new InvalidOperationException(
                            "The peer configuration transaction completed without validation.");
                    }

                    ReplaceBaseline(applied);
                    applied = null;
                    _generation = NextGeneration(_generation);
                    return CreateSnapshot();
                }
                finally
                {
                    current.Dispose();
                    if (applied != null)
                    {
                        applied.Dispose();
                    }

                    Clear(nextConfigurationBytes);
                    Clear(nextPeerBytes);
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                DisposeBaseline();
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }

        private PersistedPeerConfiguration ReadCurrent(bool allowMissing)
        {
            bool configExists = _fileWriter.Exists(StateFileTarget.Config);
            bool configBackupExists = _fileWriter.BackupExists(
                StateFileTarget.Config);
            bool peerExists = _peerCredentialFile.Exists();
            if (!configExists)
            {
                if (configBackupExists)
                {
                    throw new RecoveryJournalException(
                        "config.xml backup exists without its primary file.",
                        null);
                }

                if (peerExists)
                {
                    throw new InvalidDataException(
                        "peer.dat cannot exist without config.xml.");
                }

                if (!allowMissing)
                {
                    throw new FileNotFoundException(
                        "The installed config.xml is missing.");
                }

                return PersistedPeerConfiguration.Missing();
            }

            byte[] configBytes = _fileWriter.Read(
                StateFileTarget.Config,
                MaximumConfigurationBytes);
            ServiceDirectoryConfiguration configuration =
                _stateCodec.DeserializeConfiguration(configBytes);
            if (configBackupExists)
            {
                byte[] backupBytes = _fileWriter.ReadBackup(
                    StateFileTarget.Config,
                    MaximumConfigurationBytes);
                try
                {
                    _stateCodec.DeserializeConfiguration(backupBytes);
                }
                finally
                {
                    Clear(backupBytes);
                }
            }

            PairedPeerCredential credential = null;
            byte[] peerProtectedBytes = null;
            try
            {
                if (peerExists)
                {
                    peerProtectedBytes =
                        _peerCredentialFile.ReadExistingProtectedBytes();
                    credential = _peerCredentialFile.DecodeProtected(
                        peerProtectedBytes);
                }

                PeerCredentialConfigurationValidator.Validate(
                    configuration,
                    credential);
                return new PersistedPeerConfiguration(
                    true,
                    configuration,
                    configBytes,
                    credential,
                    peerProtectedBytes);
            }
            finally
            {
                Clear(configBytes);
                Clear(peerProtectedBytes);
                if (credential != null)
                {
                    credential.Dispose();
                }
            }
        }

        private void ValidateAllRecoveredState()
        {
            ValidateDirectory();
            using (PersistedPeerConfiguration current = ReadCurrent(true))
            {
            }

            CertificateAuthorityStore.ValidateInstalledStateFiles(
                _pathPolicy,
                _secretAccessPolicy);
        }

        internal static void ValidateInstalledNonPkiStateFiles(
            StateStoragePathPolicy pathPolicy,
            IPeerSecretAccessPolicy accessPolicy)
        {
            var store = new PeerConfigurationTransactionStore(
                pathPolicy.StateDirectoryPath,
                new DpapiMachinePeerCredentialProtector(),
                accessPolicy,
                NoOpRecoveryJournalFaultInjector.Instance);
            try
            {
                store.ValidateDirectory();
                using (PersistedPeerConfiguration current =
                    store.ReadCurrent(true))
                {
                    if (!current.Exists)
                    {
                        throw new FileNotFoundException(
                            "The installed config.xml is missing.");
                    }
                }
            }
            finally
            {
                store.Dispose();
            }
        }

        private void ValidateDirectory()
        {
            _pathPolicy.EnsureForbiddenLegacyStateIsAbsent();
            bool directoryExists = _fileWriter.Exists(
                StateFileTarget.Directory);
            if (!directoryExists)
            {
                if (_fileWriter.BackupExists(StateFileTarget.Directory))
                {
                    throw new RecoveryJournalException(
                        "Directory state backup exists without its primary document.",
                        null);
                }

                return;
            }

            byte[] directoryBytes = null;
            try
            {
                directoryBytes = _fileWriter.Read(
                    StateFileTarget.Directory,
                    MaximumStateDocumentBytes);
                _stateCodec.DeserializeSnapshot(
                    directoryBytes);
            }
            finally
            {
                Clear(directoryBytes);
            }
        }

        private static void ValidateKnownRecoveryTargets(
            IReadOnlyList<StateFileTarget> targets)
        {
            foreach (StateFileTarget target in targets)
            {
                StateFileTargets.Get(target);
            }
        }

        private static IReadOnlyList<StateFileChange> BuildChanges(
            PersistedPeerConfiguration current,
            byte[] nextConfigurationBytes,
            byte[] nextPeerBytes)
        {
            var changes = new List<StateFileChange>(2);
            if (!ByteArraysEqual(
                    current.ConfigurationBytes,
                    nextConfigurationBytes))
            {
                changes.Add(new StateFileChange(
                    StateFileTarget.Config,
                    true,
                    current.ConfigurationBytes,
                    true,
                    nextConfigurationBytes));
            }

            bool nextPeerExists = nextPeerBytes != null;
            if (current.PeerExists != nextPeerExists
                || (nextPeerExists
                    && !ByteArraysEqual(
                        current.PeerProtectedBytes,
                        nextPeerBytes)))
            {
                changes.Add(new StateFileChange(
                    StateFileTarget.PeerSecret,
                    current.PeerExists,
                    current.PeerProtectedBytes,
                    nextPeerExists,
                    nextPeerBytes));
            }

            return changes.AsReadOnly();
        }

        private static void ValidateImmutableConfiguration(
            ServiceDirectoryConfiguration expected,
            ServiceDirectoryConfiguration next)
        {
            if (!StringComparer.Ordinal.Equals(
                    expected.ListenAddress,
                    next.ListenAddress)
                || !expected.DirectoryEndpointIdentity.Equals(
                    next.DirectoryEndpointIdentity))
            {
                throw new ArgumentException(
                    "Directory identity can only be changed by installer repair.",
                    nameof(next));
            }

            if (expected.InstanceId != next.InstanceId)
            {
                throw new ArgumentException(
                    "InstanceId cannot be changed.",
                    nameof(next));
            }

            if (next.LastPeerKeyEpoch < expected.LastPeerKeyEpoch)
            {
                throw new ArgumentException(
                    "LastPeerKeyEpoch must not decrease.",
                    nameof(next));
            }
        }

        private void RequireCurrentSnapshot(
            PeerConfigurationSnapshot expected)
        {
            if (expected.IsDisposed)
            {
                throw new ObjectDisposedException(
                    nameof(PeerConfigurationSnapshot));
            }

            if (_baseline == null
                || expected.StoreId != _storeId
                || expected.Generation != _generation
                || !ConfigurationValueComparer.Equals(
                    expected.Configuration,
                    _baseline.Configuration))
            {
                throw new RecoveryRequiredException(
                    "The peer configuration snapshot is stale or belongs to another store.",
                    null);
            }
        }

        private PeerConfigurationSnapshot CreateSnapshot()
        {
            return new PeerConfigurationSnapshot(
                _storeId,
                _generation,
                _baseline.Configuration,
                _baseline.Credential);
        }

        private void ReplaceBaseline(PersistedPeerConfiguration next)
        {
            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }

            PersistedPeerConfiguration previous = _baseline;
            _baseline = next;
            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private void DisposeBaseline()
        {
            if (_baseline != null)
            {
                _baseline.Dispose();
                _baseline = null;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PeerConfigurationTransactionStore));
            }
        }

        private static long NextGeneration(long current)
        {
            return current == long.MaxValue ? 1 : current + 1;
        }

        private static byte[] Clone(byte[] value)
        {
            return value == null ? null : (byte[])value.Clone();
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
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

            int difference = 0;
            for (int index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        private sealed class PersistedPeerConfiguration : IDisposable
        {
            private bool _disposed;

            internal PersistedPeerConfiguration(
                bool exists,
                ServiceDirectoryConfiguration configuration,
                byte[] configurationBytes,
                PairedPeerCredential credential,
                byte[] peerProtectedBytes)
            {
                if (exists != (configuration != null)
                    || exists != (configurationBytes != null))
                {
                    throw new ArgumentException(
                        "Persisted configuration existence metadata is inconsistent.");
                }

                Exists = exists;
                Configuration = configuration;
                ConfigurationBytes = Clone(configurationBytes);
                Credential = credential == null
                    ? null
                    : credential.Clone();
                PeerProtectedBytes = Clone(peerProtectedBytes);
                PeerExists = peerProtectedBytes != null;
                if (!exists && PeerExists)
                {
                    throw new ArgumentException(
                        "A missing configuration cannot own peer credentials.");
                }
            }

            internal bool Exists { get; }

            internal ServiceDirectoryConfiguration Configuration { get; }

            internal byte[] ConfigurationBytes { get; }

            internal PairedPeerCredential Credential { get; }

            internal bool PeerExists { get; }

            internal byte[] PeerProtectedBytes { get; }

            internal static PersistedPeerConfiguration Missing()
            {
                return new PersistedPeerConfiguration(
                    false,
                    null,
                    null,
                    null,
                    null);
            }

            internal bool RawEquals(PersistedPeerConfiguration other)
            {
                return other != null
                    && Exists == other.Exists
                    && PeerExists == other.PeerExists
                    && ByteArraysEqual(
                        ConfigurationBytes,
                        other.ConfigurationBytes)
                    && ByteArraysEqual(
                        PeerProtectedBytes,
                        other.PeerProtectedBytes);
            }

            internal bool ValueEquals(
                ServiceDirectoryConfiguration configuration,
                PairedPeerCredential credential)
            {
                return ConfigurationValueComparer.Equals(
                        Configuration,
                        configuration)
                    && PeerCredentialValueComparer.Equals(
                        Credential,
                        credential);
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                Clear(ConfigurationBytes);
                Clear(PeerProtectedBytes);
                if (Credential != null)
                {
                    Credential.Dispose();
                }

                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }
}
