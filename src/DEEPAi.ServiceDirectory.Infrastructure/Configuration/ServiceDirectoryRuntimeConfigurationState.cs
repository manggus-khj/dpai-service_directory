using System;
using System.IO;
using System.Security;
using DEEPAi.ServiceDirectory.Application.Persistence;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;

namespace DEEPAi.ServiceDirectory.Infrastructure.Configuration
{
    // Owns the only live config.xml + peer.dat baseline in the service
    // process. Logging and Peer mutations must both pass through this owner so
    // one subsystem cannot invalidate the other's recovery baseline.
    public sealed class ServiceDirectoryRuntimeConfigurationState
        : IAdminConfigurationState, IDisposable
    {
        private readonly object _lifecycleGate = new object();
        private readonly StateMutationGate _mutationGate;
        private readonly PeerConfigurationTransactionStore _store;
        private PeerConfigurationSnapshot _current;
        private bool _recoveryRequired;
        private bool _disposed;

        public ServiceDirectoryRuntimeConfigurationState(
            string stateDirectoryPath)
            : this(stateDirectoryPath, new StateMutationGate())
        {
        }

        public ServiceDirectoryRuntimeConfigurationState(
            string stateDirectoryPath,
            StateMutationGate mutationGate)
            : this(
                new PeerConfigurationTransactionStore(
                    stateDirectoryPath,
                    new DpapiMachinePeerCredentialProtector(),
                    PeerSecretAccessPolicy.ForInstalledMainService()),
                mutationGate)
        {
        }

        internal ServiceDirectoryRuntimeConfigurationState(
            PeerConfigurationTransactionStore store)
            : this(store, new StateMutationGate())
        {
        }

        internal ServiceDirectoryRuntimeConfigurationState(
            PeerConfigurationTransactionStore store,
            StateMutationGate mutationGate)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _mutationGate = mutationGate
                ?? throw new ArgumentNullException(nameof(mutationGate));
            try
            {
                _current = _mutationGate.Execute(_store.Load);
            }
            catch
            {
                _store.Dispose();
                throw;
            }
        }

        public ServiceDirectoryConfiguration GetCurrent()
        {
            lock (_lifecycleGate)
            {
                ThrowIfUnavailable();
                return _current.Configuration;
            }
        }

        public int GetLastKnownLogRetentionDays()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                return _current.Configuration.LogRetentionDays;
            }
        }

        public AdminConfigurationUpdateResult SetLogRetentionDays(
            int logRetentionDays)
        {
            if (logRetentionDays
                    < ServiceDirectoryConfiguration.MinimumLogRetentionDays
                || logRetentionDays
                    > ServiceDirectoryConfiguration.MaximumLogRetentionDays)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(logRetentionDays));
            }

            return _mutationGate.Execute(() =>
            {
                lock (_lifecycleGate)
                {
                    ThrowIfDisposed();
                    if (_recoveryRequired)
                    {
                        return AdminConfigurationUpdateResult.Failure(
                            AdminConfigurationUpdateStatus
                                .BlockedForRecovery);
                    }

                    ServiceDirectoryConfiguration next =
                        _current.Configuration.WithLogRetentionDays(
                            logRetentionDays);
                    return MapAdminUpdate(
                        CommitPeerStateCore(
                            next,
                            _current.Credential));
                }
            });
        }

        internal bool IsRecoveryRequired
        {
            get
            {
                lock (_lifecycleGate)
                {
                    ThrowIfDisposed();
                    return _recoveryRequired;
                }
            }
        }

        internal PairedPeerCredential CopyCredential()
        {
            lock (_lifecycleGate)
            {
                ThrowIfUnavailable();
                return _current.Credential == null
                    ? null
                    : _current.Credential.Clone();
            }
        }

        internal RuntimeConfigurationCommitResult CommitPeerState(
            ServiceDirectoryConfiguration nextConfiguration,
            PairedPeerCredential nextCredential)
        {
            if (nextConfiguration == null)
            {
                throw new ArgumentNullException(nameof(nextConfiguration));
            }

            return _mutationGate.Execute(() =>
            {
                lock (_lifecycleGate)
                {
                    ThrowIfDisposed();
                    ServiceDirectoryConfiguration current =
                        _current.Configuration;
                    if (!StringComparer.Ordinal.Equals(
                            nextConfiguration.ListenAddress,
                            current.ListenAddress)
                        || !nextConfiguration.DirectoryEndpointIdentity.Equals(
                            current.DirectoryEndpointIdentity)
                        || nextConfiguration.InstanceId
                            != current.InstanceId)
                    {
                        throw new InvalidOperationException(
                            "A Peer state commit may not replace installation identity fields.");
                    }

                    // The Peer controller serializes its own state, while the
                    // Admin logging endpoint can commit through the shared
                    // mutation gate between the controller's read and write.
                    // Rebase the Peer-owned fields on the current baseline so
                    // a successful logging PUT cannot be silently lost.
                    ServiceDirectoryConfiguration rebased =
                        current.WithSynchronization(
                            nextConfiguration.LastPeerKeyEpoch,
                            nextConfiguration.Synchronization);
                    return CommitPeerStateCore(
                        rebased,
                        nextCredential);
                }
            });
        }

        public void Dispose()
        {
            _mutationGate.Execute(() =>
            {
                lock (_lifecycleGate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    if (_current != null)
                    {
                        _current.Dispose();
                        _current = null;
                    }

                    _store.Dispose();
                    _disposed = true;
                }
            });

            GC.SuppressFinalize(this);
        }

        private RuntimeConfigurationCommitResult CommitPeerStateCore(
            ServiceDirectoryConfiguration nextConfiguration,
            PairedPeerCredential nextCredential)
        {
            ThrowIfDisposed();
            if (_recoveryRequired)
            {
                return RuntimeConfigurationCommitResult.Failure(
                    RuntimeConfigurationCommitStatus.BlockedForRecovery);
            }

            try
            {
                ReplaceCurrent(
                    _store.Commit(
                        _current,
                        nextConfiguration,
                        nextCredential));
                return RuntimeConfigurationCommitResult.Success(
                    _current.Configuration);
            }
            catch (RecoveryRequiredException)
            {
                _recoveryRequired = true;
                return RuntimeConfigurationCommitResult.Failure(
                    RuntimeConfigurationCommitStatus.RecoveryRequired);
            }
            catch (RecoveryJournalException)
            {
                _recoveryRequired = true;
                return RuntimeConfigurationCommitResult.Failure(
                    RuntimeConfigurationCommitStatus.RecoveryRequired);
            }
            catch (InvalidDataException)
            {
                _recoveryRequired = true;
                return RuntimeConfigurationCommitResult.Failure(
                    RuntimeConfigurationCommitStatus.RecoveryRequired);
            }
            catch (UnauthorizedAccessException)
            {
                return RuntimeConfigurationCommitResult.Failure(
                    RuntimeConfigurationCommitStatus.PersistenceFailed);
            }
            catch (SecurityException)
            {
                return RuntimeConfigurationCommitResult.Failure(
                    RuntimeConfigurationCommitStatus.PersistenceFailed);
            }
            catch (IOException)
            {
                return RuntimeConfigurationCommitResult.Failure(
                    RuntimeConfigurationCommitStatus.PersistenceFailed);
            }
        }

        private static AdminConfigurationUpdateResult MapAdminUpdate(
            RuntimeConfigurationCommitResult result)
        {
            switch (result.Status)
            {
                case RuntimeConfigurationCommitStatus.Completed:
                    return AdminConfigurationUpdateResult.Success(
                        result.Configuration);
                case RuntimeConfigurationCommitStatus.PersistenceFailed:
                    return AdminConfigurationUpdateResult.Failure(
                        AdminConfigurationUpdateStatus.PersistenceFailed);
                case RuntimeConfigurationCommitStatus.RecoveryRequired:
                    return AdminConfigurationUpdateResult.Failure(
                        AdminConfigurationUpdateStatus.RecoveryRequired);
                case RuntimeConfigurationCommitStatus.BlockedForRecovery:
                    return AdminConfigurationUpdateResult.Failure(
                        AdminConfigurationUpdateStatus.BlockedForRecovery);
                default:
                    throw new InvalidOperationException(
                        "The runtime configuration commit returned an unknown status.");
            }
        }

        private void ReplaceCurrent(PeerConfigurationSnapshot next)
        {
            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }

            PeerConfigurationSnapshot previous = _current;
            _current = next;
            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private void ThrowIfUnavailable()
        {
            ThrowIfDisposed();
            if (_recoveryRequired)
            {
                throw new RecoveryRequiredException(
                    "The runtime configuration is blocked for recovery.",
                    null);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(ServiceDirectoryRuntimeConfigurationState));
            }
        }
    }

    internal enum RuntimeConfigurationCommitStatus
    {
        Completed = 0,
        PersistenceFailed = 1,
        RecoveryRequired = 2,
        BlockedForRecovery = 3
    }

    internal sealed class RuntimeConfigurationCommitResult
    {
        private RuntimeConfigurationCommitResult(
            RuntimeConfigurationCommitStatus status,
            ServiceDirectoryConfiguration configuration)
        {
            bool completed = status
                == RuntimeConfigurationCommitStatus.Completed;
            if (!Enum.IsDefined(
                    typeof(RuntimeConfigurationCommitStatus),
                    status)
                || completed != (configuration != null))
            {
                throw new ArgumentException(
                    "The runtime configuration result is inconsistent.");
            }

            Status = status;
            Configuration = configuration;
        }

        internal RuntimeConfigurationCommitStatus Status { get; }

        internal ServiceDirectoryConfiguration Configuration { get; }

        internal static RuntimeConfigurationCommitResult Success(
            ServiceDirectoryConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            return new RuntimeConfigurationCommitResult(
                RuntimeConfigurationCommitStatus.Completed,
                configuration);
        }

        internal static RuntimeConfigurationCommitResult Failure(
            RuntimeConfigurationCommitStatus status)
        {
            if (status == RuntimeConfigurationCommitStatus.Completed)
            {
                throw new ArgumentException(
                    "A failure status is required.",
                    nameof(status));
            }

            return new RuntimeConfigurationCommitResult(status, null);
        }
    }
}
