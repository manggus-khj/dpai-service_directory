using System;
using System.Collections.Generic;
using System.IO;
using DEEPAi.ServiceDirectory.Domain.Certificates;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    public enum CertificateAuthorityOperationalState
    {
        NotProvisioned = 1,
        BackupRequired = 2,
        Ready = 3
    }

    public enum CertificateAuthorityIssuerRole
    {
        ActiveIssuer = 1,
        Standby = 2
    }

    public sealed class CertificateAuthorityStatus
    {
        private readonly byte[] _caSpkiSha256;

        internal CertificateAuthorityStatus(
            CertificateAuthorityOperationalState state)
        {
            if (state != CertificateAuthorityOperationalState.NotProvisioned)
            {
                throw new ArgumentOutOfRangeException(nameof(state));
            }

            State = state;
        }

        internal CertificateAuthorityStatus(
            CertificateAuthorityOperationalState state,
            CertificateAuthorityIssuerRole role,
            Guid siteId,
            Guid issuerInstanceId,
            CertificateSerialNumber caSerialNumber,
            byte[] caSpkiSha256,
            DateTime notBeforeUtc,
            DateTime notAfterUtc,
            ulong pkiRevision,
            ulong crlNumber,
            DateTime? lastBackupUtc)
        {
            if (state == CertificateAuthorityOperationalState.NotProvisioned
                || !Enum.IsDefined(
                    typeof(CertificateAuthorityOperationalState),
                    state))
            {
                throw new ArgumentOutOfRangeException(nameof(state));
            }

            if (!Enum.IsDefined(
                typeof(CertificateAuthorityIssuerRole),
                role))
            {
                throw new ArgumentOutOfRangeException(nameof(role));
            }

            if (siteId == Guid.Empty || issuerInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Provisioned CA identifiers must not be empty.");
            }

            if (!caSerialNumber.IsValid)
            {
                throw new ArgumentException(
                    "CA serial number must be valid.",
                    nameof(caSerialNumber));
            }

            if (caSpkiSha256 == null || caSpkiSha256.Length != 32)
            {
                throw new ArgumentException(
                    "CA SPKI SHA-256 must contain 32 bytes.",
                    nameof(caSpkiSha256));
            }

            State = state;
            Role = role;
            SiteId = siteId;
            IssuerInstanceId = issuerInstanceId;
            CaSerialNumber = caSerialNumber.Hex;
            _caSpkiSha256 = (byte[])caSpkiSha256.Clone();
            NotBeforeUtc = notBeforeUtc;
            NotAfterUtc = notAfterUtc;
            PkiRevision = pkiRevision;
            CrlNumber = crlNumber;
            LastBackupUtc = lastBackupUtc;
        }

        public CertificateAuthorityOperationalState State { get; }

        public CertificateAuthorityIssuerRole? Role { get; }

        public Guid? SiteId { get; }

        public Guid? IssuerInstanceId { get; }

        public string CaSerialNumber { get; }

        public DateTime? NotBeforeUtc { get; }

        public DateTime? NotAfterUtc { get; }

        public ulong? PkiRevision { get; }

        public ulong? CrlNumber { get; }

        public DateTime? LastBackupUtc { get; }

        public byte[] GetCaSpkiSha256()
        {
            return _caSpkiSha256 == null
                ? null
                : (byte[])_caSpkiSha256.Clone();
        }
    }

    public sealed class CertificateAuthorityBackupResult
    {
        private readonly byte[] _sha256;

        internal CertificateAuthorityBackupResult(
            string fileName,
            DateTime createdUtc,
            byte[] sha256)
        {
            FileName = fileName;
            CreatedUtc = createdUtc;
            _sha256 = (byte[])sha256.Clone();
        }

        public string FileName { get; }

        public DateTime CreatedUtc { get; }

        public byte[] GetSha256()
        {
            return (byte[])_sha256.Clone();
        }
    }

    public sealed class CertificateRevocationResult
    {
        internal CertificateRevocationResult(
            string serialNumber,
            DateTime revokedUtc,
            CertificateRevocationReason reason,
            ulong pkiRevision,
            ulong crlNumber,
            bool replayed)
        {
            SerialNumber = serialNumber;
            RevokedUtc = revokedUtc;
            Reason = reason;
            PkiRevision = pkiRevision;
            CrlNumber = crlNumber;
            Replayed = replayed;
        }

        public string SerialNumber { get; }

        public DateTime RevokedUtc { get; }

        public CertificateRevocationReason Reason { get; }

        public ulong PkiRevision { get; }

        public ulong CrlNumber { get; }

        public bool Replayed { get; }
    }

    public interface ICertificateAuthorityAdministration
    {
        CertificateAuthorityStatus GetStatus();

        CertificateAuthorityBackupResult CreateBackup(
            string password,
            DateTime createdUtc);

        CertificateLedgerSnapshot GetLedgerSnapshot();

        CertificateRevocationResult Revoke(
            string serialNumber,
            CertificateRevocationReason reason,
            DateTime revokedUtc);
    }

    internal sealed partial class CertificateAuthorityAdministration
        : ICertificateAuthorityAdministration,
        IDisposable
    {
        private readonly CertificateAuthorityStore _store;
        private readonly CaBackupFileStore _backupFileStore;
        private readonly CaBackupCodec _backupCodec;
        private bool _provisioned;
        private bool _disposed;

        internal CertificateAuthorityAdministration(
            CertificateAuthorityStore store,
            CaBackupFileStore backupFileStore)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _backupFileStore = backupFileStore
                ?? throw new ArgumentNullException(nameof(backupFileStore));
            _backupCodec = new CaBackupCodec();
            _provisioned = _store.TryLoad();
        }

        public CertificateAuthorityStatus GetStatus()
        {
            ThrowIfDisposed();
            if (!_provisioned)
            {
                return new CertificateAuthorityStatus(
                    CertificateAuthorityOperationalState.NotProvisioned);
            }

            using (CertificateAuthorityStoreSnapshot snapshot =
                _store.GetCurrent())
            {
                CertificateAuthorityState state = snapshot.State;
                return new CertificateAuthorityStatus(
                    state.LastBackupUtc.HasValue
                        ? CertificateAuthorityOperationalState.Ready
                        : CertificateAuthorityOperationalState.BackupRequired,
                    state.Role == CertificateAuthorityRole.ActiveIssuer
                        ? CertificateAuthorityIssuerRole.ActiveIssuer
                        : CertificateAuthorityIssuerRole.Standby,
                    state.SiteId,
                    state.IssuerInstanceId,
                    state.CaSerialNumber,
                    state.GetCaSpkiSha256(),
                    state.NotBeforeUtc,
                    state.NotAfterUtc,
                    state.PkiRevision,
                    state.CrlNumber,
                    state.LastBackupUtc);
            }
        }

        public CertificateAuthorityBackupResult CreateBackup(
            string password,
            DateTime createdUtc)
        {
            ThrowIfDisposed();
            if (!_provisioned)
            {
                throw new InvalidOperationException(
                    "PKI state is not provisioned.");
            }

            if (createdUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "CA backup creation time must be UTC.",
                    nameof(createdUtc));
            }

            CaBackupCodec.ValidatePassword(password);
            ulong pkiRevision;
            ulong crlNumber;
            CaBackupPayload payload = _store.CaptureBackupPayload(
                createdUtc,
                out pkiRevision,
                out crlNumber);
            byte[] encrypted = null;
            CaBackupFileArtifact artifact = null;
            try
            {
                encrypted = _backupCodec.Encrypt(payload, password);
                CertificateAuthorityStatus status = GetStatus();
                artifact = _backupFileStore.WriteNew(
                    status.SiteId.Value,
                    createdUtc,
                    encrypted);
                if (!_store.MarkBackupCompleted(
                        pkiRevision,
                        crlNumber,
                        createdUtc))
                {
                    _backupFileStore.DeleteUnapproved(artifact.FileName);
                    artifact = null;
                    throw new InvalidOperationException(
                        "PKI state changed while the backup was created.");
                }

                return new CertificateAuthorityBackupResult(
                    artifact.FileName,
                    artifact.CreatedUtc,
                    artifact.GetSha256());
            }
            finally
            {
                payload.Dispose();
                if (encrypted != null)
                {
                    Array.Clear(encrypted, 0, encrypted.Length);
                }
            }
        }

        public CertificateLedgerSnapshot GetLedgerSnapshot()
        {
            ThrowIfDisposed();
            if (!_provisioned)
            {
                throw new InvalidOperationException(
                    "PKI state is not provisioned.");
            }

            using (CertificateAuthorityStoreSnapshot snapshot =
                _store.GetCurrent())
            {
                return new CertificateLedgerSnapshot(
                    snapshot.Ledger.EntriesBySerial.Values,
                    snapshot.Ledger.PkiRevision,
                    snapshot.Ledger.CrlNumber);
            }
        }

        public CertificateRevocationResult Revoke(
            string serialNumber,
            CertificateRevocationReason reason,
            DateTime revokedUtc)
        {
            ThrowIfDisposed();
            if (!_provisioned)
            {
                throw new InvalidOperationException(
                    "PKI state is not provisioned.");
            }

            CertificateSerialNumber parsed;
            if (!CertificateSerialNumber.TryCreate(
                    serialNumber,
                    out parsed))
            {
                throw new ArgumentException(
                    "Certificate serial is not canonical.",
                    nameof(serialNumber));
            }

            bool alreadyRevoked;
            using (CertificateAuthorityStoreSnapshot snapshot =
                _store.Revoke(
                    parsed,
                    reason,
                    revokedUtc,
                    out alreadyRevoked))
            {
                CertificateLedgerEntry entry;
                if (!snapshot.Ledger.TryGetBySerial(parsed, out entry)
                    || !entry.RevokedUtc.HasValue
                    || !entry.RevocationReason.HasValue)
                {
                    throw new InvalidDataException(
                        "Successful revocation did not produce a revoked ledger entry.");
                }

                return new CertificateRevocationResult(
                    entry.SerialNumber.Hex,
                    entry.RevokedUtc.Value,
                    entry.RevocationReason.Value,
                    snapshot.State.PkiRevision,
                    snapshot.State.CrlNumber,
                    alreadyRevoked);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _store.Dispose();
            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(CertificateAuthorityAdministration));
            }
        }
    }
}
