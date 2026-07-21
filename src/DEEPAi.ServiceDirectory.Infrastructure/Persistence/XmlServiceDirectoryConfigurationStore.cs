using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    public sealed class XmlServiceDirectoryConfigurationStore
        : IServiceDirectoryConfigurationStore
    {
        private const int MaximumConfigurationBytes = 16 * 1024 * 1024;
        private const int ErrorHandleDiskFull = 39;
        private const int ErrorDiskFull = 112;

        private readonly object _gate = new object();
        private readonly AtomicFileWriter _fileWriter;
        private readonly RecoveryJournalManager _journalManager;
        private readonly StateXmlCodec _codec;
        private PersistedConfiguration _baseline;

        public XmlServiceDirectoryConfigurationStore(string stateDirectoryPath)
            : this(
                stateDirectoryPath,
                NoOpRecoveryJournalFaultInjector.Instance)
        {
        }

        internal XmlServiceDirectoryConfigurationStore(
            string stateDirectoryPath,
            IRecoveryJournalFaultInjector faultInjector)
        {
            var pathPolicy = new StateStoragePathPolicy(stateDirectoryPath);
            _fileWriter = new AtomicFileWriter(pathPolicy);
            _journalManager = new RecoveryJournalManager(
                pathPolicy,
                _fileWriter,
                faultInjector);
            _codec = new StateXmlCodec();
        }

        public ConfigurationLoadResult Load()
        {
            lock (_gate)
            {
                _baseline = null;
                try
                {
                    _journalManager.Recover(
                        ValidateRecoveryTargets,
                        ValidateRecoveredConfiguration);
                    PersistedConfiguration current =
                        ReadCurrentConfiguration();
                    _baseline = current;
                    return current.Exists
                        ? ConfigurationLoadResult.Success(
                            current.Configuration)
                        : ConfigurationLoadResult.Failure(
                            ConfigurationLoadFailureCode.Missing);
                }
                catch (RecoveryJournalException)
                {
                    return ConfigurationLoadResult.Failure(
                        ConfigurationLoadFailureCode.RecoveryFailed);
                }
                catch (UnauthorizedAccessException)
                {
                    return ConfigurationLoadResult.Failure(
                        ConfigurationLoadFailureCode.AccessDenied);
                }
                catch (SecurityException)
                {
                    return ConfigurationLoadResult.Failure(
                        ConfigurationLoadFailureCode.AccessDenied);
                }
                catch (InvalidDataException)
                {
                    return ConfigurationLoadResult.Failure(
                        ConfigurationLoadFailureCode.InvalidData);
                }
                catch (IOException)
                {
                    return ConfigurationLoadResult.Failure(
                        ConfigurationLoadFailureCode.IoFailure);
                }
            }
        }

        public ConfigurationCommitResult Initialize(
            ServiceDirectoryConfiguration initialConfiguration)
        {
            if (initialConfiguration == null)
            {
                throw new ArgumentNullException(nameof(initialConfiguration));
            }

            ServiceDirectoryConfiguration requiredInitial =
                ServiceDirectoryConfiguration.CreateInitial(
                    initialConfiguration.DirectoryEndpointIdentity,
                    initialConfiguration.InstanceId);
            if (!ConfigurationValueComparer.Equals(
                    requiredInitial,
                    initialConfiguration))
            {
                throw new ArgumentException(
                    "Initial config.xml must use the installation defaults.",
                    nameof(initialConfiguration));
            }

            lock (_gate)
            {
                if (_baseline == null)
                {
                    return RequireRecovery();
                }

                if (_baseline.Exists)
                {
                    return ConfigurationCommitResult.Failure(
                        ConfigurationCommitFailureCode.AlreadyInitialized);
                }

                return CommitCore(
                    _baseline,
                    initialConfiguration);
            }
        }

        public ConfigurationCommitResult Commit(
            ServiceDirectoryConfiguration expectedConfiguration,
            ServiceDirectoryConfiguration nextConfiguration)
        {
            if (expectedConfiguration == null)
            {
                throw new ArgumentNullException(nameof(expectedConfiguration));
            }

            if (nextConfiguration == null)
            {
                throw new ArgumentNullException(nameof(nextConfiguration));
            }

            if (!StringComparer.Ordinal.Equals(
                    expectedConfiguration.ListenAddress,
                    nextConfiguration.ListenAddress)
                || !expectedConfiguration.DirectoryEndpointIdentity.Equals(
                    nextConfiguration.DirectoryEndpointIdentity))
            {
                throw new ArgumentException(
                    "Directory identity can only be changed by installer repair.",
                    nameof(nextConfiguration));
            }

            if (expectedConfiguration.InstanceId
                != nextConfiguration.InstanceId)
            {
                throw new ArgumentException(
                    "InstanceId cannot be changed.",
                    nameof(nextConfiguration));
            }

            if (nextConfiguration.LastPeerKeyEpoch
                < expectedConfiguration.LastPeerKeyEpoch)
            {
                throw new ArgumentException(
                    "LastPeerKeyEpoch must not decrease.",
                    nameof(nextConfiguration));
            }

            lock (_gate)
            {
                if (_baseline == null
                    || !_baseline.Exists
                    || !ConfigurationValueComparer.Equals(
                        _baseline.Configuration,
                        expectedConfiguration))
                {
                    return RequireRecovery();
                }

                return CommitCore(_baseline, nextConfiguration);
            }
        }

        public ConfigurationCommitResult CommitDirectoryIdentityForRepair(
            ServiceDirectoryConfiguration expectedConfiguration,
            string nextDirectoryHostName,
            string nextDirectoryIpv4Address)
        {
            if (expectedConfiguration == null)
            {
                throw new ArgumentNullException(nameof(expectedConfiguration));
            }

            DirectoryEndpointIdentity nextIdentity;
            EndpointIdentityValidationError identityError;
            if (!DirectoryEndpointIdentity.TryCreate(
                    nextDirectoryHostName,
                    nextDirectoryIpv4Address,
                    out nextIdentity,
                    out identityError)
                || !StringComparer.Ordinal.Equals(
                    nextDirectoryHostName,
                    nextIdentity.DirectoryHostName)
                || !StringComparer.Ordinal.Equals(
                    nextDirectoryIpv4Address,
                    nextIdentity.DirectoryIpv4Address))
            {
                throw new ArgumentException(
                    "The repair Directory identity is invalid: "
                    + identityError
                    + ".",
                    nameof(nextDirectoryHostName));
            }

            ServiceDirectoryConfiguration nextConfiguration =
                expectedConfiguration.WithDirectoryIdentityForRepair(
                    nextIdentity);
            if (!ConfigurationValueComparer.EqualsExceptDirectoryIdentity(
                    expectedConfiguration,
                    nextConfiguration))
            {
                throw new InvalidOperationException(
                    "The repair operation attempted to change non-address settings.");
            }

            lock (_gate)
            {
                if (_baseline == null
                    || !_baseline.Exists
                    || !ConfigurationValueComparer.Equals(
                        _baseline.Configuration,
                        expectedConfiguration))
                {
                    return RequireRecovery();
                }

                return CommitCore(_baseline, nextConfiguration);
            }
        }

        private ConfigurationCommitResult CommitCore(
            PersistedConfiguration expected,
            ServiceDirectoryConfiguration nextConfiguration)
        {
            try
            {
                _journalManager.EnsureNoActiveTransaction();
                PersistedConfiguration current =
                    ReadCurrentConfiguration();
                if (!expected.RawEquals(current))
                {
                    return RequireRecovery();
                }

                byte[] contents =
                    _codec.SerializeConfiguration(nextConfiguration);
                ServiceDirectoryConfiguration roundTrip =
                    _codec.DeserializeConfiguration(contents);
                if (!ConfigurationValueComparer.Equals(
                        nextConfiguration,
                        roundTrip))
                {
                    throw new InvalidDataException(
                        "The configuration did not survive XML serialization exactly.");
                }

                if (current.Exists
                    && ByteArraysEqual(current.Contents, contents))
                {
                    _baseline = new PersistedConfiguration(
                        true,
                        nextConfiguration,
                        contents);
                    return ConfigurationCommitResult.Success();
                }

                var changes = new[]
                {
                    new StateFileChange(
                        StateFileTarget.Config,
                        current.Exists,
                        current.Contents,
                        true,
                        contents)
                };
                PersistedConfiguration applied = null;
                _journalManager.Commit(
                    changes,
                    () =>
                    {
                        PersistedConfiguration candidate =
                            ReadCurrentConfiguration();
                        if (!candidate.Exists
                            || !ConfigurationValueComparer.Equals(
                                candidate.Configuration,
                                nextConfiguration))
                        {
                            throw new InvalidDataException(
                                "The applied config.xml does not match the requested configuration.");
                        }

                        applied = candidate;
                    });

                if (applied == null)
                {
                    throw new InvalidOperationException(
                        "The configuration transaction completed without validating its result.");
                }

                _baseline = applied;
                return ConfigurationCommitResult.Success();
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
                return ConfigurationCommitResult.Failure(
                    ConfigurationCommitFailureCode.AccessDenied);
            }
            catch (SecurityException)
            {
                return ConfigurationCommitResult.Failure(
                    ConfigurationCommitFailureCode.AccessDenied);
            }
            catch (InvalidDataException)
            {
                return RequireRecovery();
            }
            catch (IOException exception)
            {
                return ConfigurationCommitResult.Failure(
                    IsDiskFull(exception)
                        ? ConfigurationCommitFailureCode.DiskFull
                        : ConfigurationCommitFailureCode.IoFailure);
            }
        }

        private PersistedConfiguration ReadCurrentConfiguration()
        {
            bool exists = _fileWriter.Exists(StateFileTarget.Config);
            bool backupExists = _fileWriter.BackupExists(
                StateFileTarget.Config);
            if (!exists)
            {
                if (backupExists)
                {
                    throw new RecoveryJournalException(
                        "config.xml backup exists without its primary file.",
                        null);
                }

                return PersistedConfiguration.Missing();
            }

            byte[] contents = _fileWriter.Read(
                StateFileTarget.Config,
                MaximumConfigurationBytes);
            ServiceDirectoryConfiguration configuration =
                _codec.DeserializeConfiguration(contents);
            if (backupExists)
            {
                byte[] backupContents = _fileWriter.ReadBackup(
                    StateFileTarget.Config,
                    MaximumConfigurationBytes);
                _codec.DeserializeConfiguration(backupContents);
            }

            return new PersistedConfiguration(
                true,
                configuration,
                contents);
        }

        private void ValidateRecoveredConfiguration()
        {
            ReadCurrentConfiguration();
        }

        private static void ValidateRecoveryTargets(
            IReadOnlyList<StateFileTarget> targets)
        {
            foreach (StateFileTarget target in targets)
            {
                if (target != StateFileTarget.Config)
                {
                    throw new InvalidDataException(
                        "The configuration store can only recover a config.xml transaction.");
                }
            }
        }

        private ConfigurationCommitResult RequireRecovery()
        {
            _baseline = null;
            return ConfigurationCommitResult.Failure(
                ConfigurationCommitFailureCode.RecoveryRequired);
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

        private sealed class PersistedConfiguration
        {
            internal PersistedConfiguration(
                bool exists,
                ServiceDirectoryConfiguration configuration,
                byte[] contents)
            {
                if (exists != (configuration != null)
                    || exists != (contents != null))
                {
                    throw new ArgumentException(
                        "Configuration existence metadata is inconsistent.");
                }

                Exists = exists;
                Configuration = configuration;
                Contents = contents == null
                    ? null
                    : (byte[])contents.Clone();
            }

            internal bool Exists { get; }

            internal ServiceDirectoryConfiguration Configuration { get; }

            internal byte[] Contents { get; }

            internal static PersistedConfiguration Missing()
            {
                return new PersistedConfiguration(false, null, null);
            }

            internal bool RawEquals(PersistedConfiguration other)
            {
                return other != null
                    && Exists == other.Exists
                    && ByteArraysEqual(Contents, other.Contents);
            }
        }
    }
}
