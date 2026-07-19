using System;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    public sealed class CertificateAuthorityRuntimeAdministration
        : ICertificateAuthorityAdministration,
        IDisposable
    {
        private readonly CertificateAuthorityAdministration _administration;
        private bool _disposed;

        public CertificateAuthorityRuntimeAdministration(
            string stateDirectoryPath,
            StateMutationGate mutationGate,
            Guid installedInstanceId)
        {
            if (string.IsNullOrWhiteSpace(stateDirectoryPath))
            {
                throw new ArgumentException(
                    "The state directory path is required.",
                    nameof(stateDirectoryPath));
            }

            if (mutationGate == null)
            {
                throw new ArgumentNullException(nameof(mutationGate));
            }

            if (installedInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The installed instance ID must not be empty.",
                    nameof(installedInstanceId));
            }

            var pathPolicy = new StateStoragePathPolicy(stateDirectoryPath);
            var accessPolicy =
                PeerSecretAccessPolicy.ForInstalledMainService();
            var store = new CertificateAuthorityStore(
                pathPolicy,
                mutationGate,
                new DpapiMachineCaPrivateKeyProtector(),
                accessPolicy,
                NoOpRecoveryJournalFaultInjector.Instance);
            try
            {
                _administration = new CertificateAuthorityAdministration(
                    store,
                    new CaBackupFileStore(pathPolicy, accessPolicy));
                _administration.EnsureProvisioned(
                    installedInstanceId,
                    DateTime.UtcNow);
            }
            catch
            {
                store.Dispose();
                throw;
            }
        }

        public CertificateAuthorityStatus GetStatus()
        {
            ThrowIfDisposed();
            return _administration.GetStatus();
        }

        public CertificateAuthorityBackupResult CreateBackup(
            string password,
            DateTime createdUtc)
        {
            ThrowIfDisposed();
            return _administration.CreateBackup(password, createdUtc);
        }

        public CertificateLedgerSnapshot GetLedgerSnapshot()
        {
            ThrowIfDisposed();
            return _administration.GetLedgerSnapshot();
        }

        public CertificateRevocationResult Revoke(
            string serialNumber,
            CertificateRevocationReason reason,
            DateTime revokedUtc)
        {
            ThrowIfDisposed();
            return _administration.Revoke(
                serialNumber,
                reason,
                revokedUtc);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _administration.Dispose();
            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(CertificateAuthorityRuntimeAdministration));
            }
        }
    }
}
