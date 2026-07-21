using System;
using DEEPAi.ServiceDirectory.Application.Registration;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    public sealed class CertificateAuthorityRuntimeAdministration
        : ICertificateAuthorityAdministration,
        ICertificateAuthorityRotationAdministration,
        ICertificateServiceMutationAdministration,
        ICertificateAuthorityPeerSynchronization,
        IPeerTlsTrustProvider,
        IDisposable
    {
        private readonly CertificateAuthorityAdministration _administration;
        private readonly StateMutationCoordinator _directoryState;
        private readonly RegistrationModeOwner _registrationModeOwner;
        private readonly DirectoryEndpointIdentity _directoryIdentity;
        private readonly PeerPkiSynchronizationStore _peerPkiStore;
        private readonly Guid _installedInstanceId;
        private bool _disposed;

        public CertificateAuthorityRuntimeAdministration(
            string stateDirectoryPath,
            StateMutationGate mutationGate,
            Guid installedInstanceId)
            : this(
                stateDirectoryPath,
                mutationGate,
                installedInstanceId,
                null,
                null,
                null)
        {
        }

        public CertificateAuthorityRuntimeAdministration(
            string stateDirectoryPath,
            StateMutationGate mutationGate,
            Guid installedInstanceId,
            StateMutationCoordinator directoryState,
            RegistrationModeOwner registrationModeOwner,
            DirectoryEndpointIdentity directoryIdentity)
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

            bool hasRegistrationDependencies = directoryState != null
                || registrationModeOwner != null
                || directoryIdentity != null;
            if (hasRegistrationDependencies
                && (directoryState == null
                    || registrationModeOwner == null
                    || directoryIdentity == null))
            {
                throw new ArgumentException(
                    "External registration dependencies must be supplied together.");
            }

            _directoryState = directoryState;
            _registrationModeOwner = registrationModeOwner;
            _directoryIdentity = directoryIdentity;
            _installedInstanceId = installedInstanceId;
            _peerPkiStore = new PeerPkiSynchronizationStore(
                stateDirectoryPath,
                mutationGate);

            CertificateAuthorityIssuerRole installedRole =
                _peerPkiStore.GetRole();
            if (installedRole == CertificateAuthorityIssuerRole.Standby)
            {
                _administration = null;
                CertificateAuthorityStatus standbyStatus =
                    _peerPkiStore.GetStandbyStatus(DateTime.UtcNow);
                if (standbyStatus.State
                    == CertificateAuthorityOperationalState.NotProvisioned)
                {
                    throw new InvalidOperationException(
                        "Standby PKI state is not provisioned.");
                }

                return;
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
                CertificateAuthorityStatus status =
                    _administration.GetStatus();
                if (status.State
                    == CertificateAuthorityOperationalState.NotProvisioned)
                {
                    throw new InvalidOperationException(
                        "PKI state must be provisioned by the stopped installer repair path before service startup.");
                }

                if (status.Role
                        == CertificateAuthorityIssuerRole.ActiveIssuer
                    && status.IssuerInstanceId != installedInstanceId)
                {
                    throw new InvalidOperationException(
                        "The active CA issuer identity does not match config.xml.");
                }
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
            return _administration == null
                ? _peerPkiStore.GetStandbyStatus(DateTime.UtcNow)
                : _administration.GetStatus();
        }

        public CertificateAuthorityBackupResult CreateBackup(
            string password,
            DateTime createdUtc)
        {
            ThrowIfDisposed();
            EnsureActiveIssuer("CA backup creation");
            return _administration.CreateBackup(password, createdUtc);
        }

        public CertificateLedgerSnapshot GetLedgerSnapshot()
        {
            ThrowIfDisposed();
            EnsureActiveIssuer("certificate ledger access");
            return _administration.GetLedgerSnapshot();
        }

        public CertificateRevocationResult Revoke(
            string serialNumber,
            CertificateRevocationReason reason,
            DateTime revokedUtc)
        {
            ThrowIfDisposed();
            EnsureActiveIssuer("certificate revocation");
            return _administration.Revoke(
                serialNumber,
                reason,
                revokedUtc);
        }

        public AdminServerCaRotationResponse GetRotationStatus(
            DateTime utcNow)
        {
            ThrowIfDisposed();
            EnsureActiveIssuer("CA rotation status access");
            return _administration.GetRotationStatus(utcNow);
        }

        public AdminServerCaRotationResponse PrepareRotation(
            DateTime utcNow)
        {
            ThrowIfDisposed();
            EnsureActiveIssuer("CA rotation preparation");
            if (_registrationModeOwner == null
                || _registrationModeOwner.GetSnapshot().State
                    != RegistrationModeState.Closed)
            {
                throw new InvalidOperationException(
                    "Registration mode must be closed before CA rotation preparation.");
            }

            return _administration.PrepareRotation(utcNow);
        }

        public AdminServerCaRotationResponse CancelRotation(
            Guid rotationId,
            DateTime utcNow)
        {
            ThrowIfDisposed();
            EnsureActiveIssuer("CA rotation cancellation");
            return _administration.CancelRotation(rotationId, utcNow);
        }

        internal ExternalTrustInfo GetExternalTrustInfo()
        {
            ThrowIfDisposed();
            return _administration == null
                ? _peerPkiStore.GetStandbyExternalTrustInfo(
                    DateTime.UtcNow)
                : _administration.GetExternalTrustInfo();
        }

        internal ExternalTrustSnapshot GetExternalTrustSnapshot()
        {
            ThrowIfDisposed();
            return _administration == null
                ? _peerPkiStore.GetStandbyExternalTrustSnapshot(
                    DateTime.UtcNow)
                : _administration.GetExternalTrustSnapshot();
        }

        internal byte[] GetExternalCertificateRevocationList()
        {
            ThrowIfDisposed();
            if (_directoryState == null)
            {
                throw new InvalidOperationException(
                    "External PKI maintenance is not configured for this administration instance.");
            }

            return _administration == null
                ? _peerPkiStore
                    .GetStandbyExternalCertificateRevocationList(
                        DateTime.UtcNow)
                : _administration.GetExternalCertificateRevocationList(
                    _directoryState,
                    _installedInstanceId,
                    DateTime.UtcNow);
        }

        internal byte[] GetExternalCertificateRevocationList(
            string caSerialNumber)
        {
            ThrowIfDisposed();
            if (_directoryState == null)
            {
                throw new InvalidOperationException(
                    "External PKI maintenance is not configured for this administration instance.");
            }

            return _administration == null
                ? _peerPkiStore
                    .GetStandbyExternalCertificateRevocationList(
                        caSerialNumber,
                        DateTime.UtcNow)
                : _administration.GetExternalCertificateRevocationList(
                    caSerialNumber);
        }

        internal ExternalRegistrationServiceResult RegisterExternalService(
            ExternalRegistrationRequest request,
            DateTime utcNow)
        {
            ThrowIfDisposed();
            if (_directoryState == null)
            {
                throw new InvalidOperationException(
                    "External registration is not configured for this PKI administration instance.");
            }

            if (_administration == null)
            {
                return ExternalRegistrationServiceResult.Failure(
                    ExternalRegistrationServiceStatus.Conflict);
            }

            return _administration.RegisterExternalService(
                request,
                _directoryState,
                _registrationModeOwner,
                _directoryIdentity,
                _installedInstanceId,
                utcNow);
        }

        internal ExternalRegistrationServiceResult RenewExternalService(
            ExternalCertificateRenewalRequest request,
            DateTime utcNow)
        {
            ThrowIfDisposed();
            if (_directoryState == null)
            {
                throw new InvalidOperationException(
                    "External renewal is not configured for this PKI administration instance.");
            }

            if (_administration == null)
            {
                return ExternalRegistrationServiceResult.Failure(
                    ExternalRegistrationServiceStatus.Conflict);
            }

            return _administration.RenewExternalService(
                request,
                _directoryState,
                _directoryIdentity,
                _installedInstanceId,
                utcNow);
        }

        CertificateServiceDeletionResult
            ICertificateServiceMutationAdministration.DeleteService(
                ProductCode productCode,
                DateTime utcNow)
        {
            ThrowIfDisposed();
            if (_directoryState == null)
            {
                throw new InvalidOperationException(
                    "Certificate service deletion is not configured for this PKI administration instance.");
            }

            EnsureActiveIssuer("certificate-backed service deletion");

            return _administration.DeleteService(
                _directoryState,
                _installedInstanceId,
                productCode,
                utcNow);
        }

        public CertificateAuthorityIssuerRole GetPeerPkiRole()
        {
            ThrowIfDisposed();
            return _peerPkiStore.GetRole();
        }

        public PeerPkiState GetPeerPkiState()
        {
            ThrowIfDisposed();
            return _peerPkiStore.GetActiveState();
        }

        public PeerPkiState GetKnownPeerPkiState()
        {
            ThrowIfDisposed();
            return _peerPkiStore.GetKnownStandbyState();
        }

        public void ApplyPeerPkiState(
            PeerPkiState state,
            DateTime utcNow)
        {
            ThrowIfDisposed();
            _peerPkiStore.ApplyStandbyState(state, utcNow);
        }

        PeerTlsTrustSnapshot IPeerTlsTrustProvider.CapturePeerTlsTrust(
            string peerEndpoint,
            DateTime utcNow)
        {
            ThrowIfDisposed();
            return ((IPeerTlsTrustProvider)_peerPkiStore)
                .CapturePeerTlsTrust(peerEndpoint, utcNow);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_administration != null)
            {
                _administration.Dispose();
            }

            _disposed = true;
        }

        private void EnsureActiveIssuer(string operation)
        {
            if (_administration == null)
            {
                throw new InvalidOperationException(
                    "A standby cannot perform " + operation + ".");
            }
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
