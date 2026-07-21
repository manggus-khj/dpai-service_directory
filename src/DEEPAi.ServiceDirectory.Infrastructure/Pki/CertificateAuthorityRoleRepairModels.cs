using System;
using DEEPAi.ServiceDirectory.Domain.Certificates;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed class CertificateAuthorityRoleRepairStateResult
        : IDisposable
    {
        private bool _disposed;

        internal CertificateAuthorityRoleRepairStateResult(
            IssuedCertificateArtifact directoryCertificate,
            CaBackupFileArtifact backupArtifact,
            bool promoted)
        {
            DirectoryCertificate = directoryCertificate
                ?? throw new ArgumentNullException(
                    nameof(directoryCertificate));
            BackupArtifact = backupArtifact
                ?? throw new ArgumentNullException(nameof(backupArtifact));
            Promoted = promoted;
        }

        internal IssuedCertificateArtifact DirectoryCertificate
        {
            get;
            private set;
        }

        internal CaBackupFileArtifact BackupArtifact { get; }

        internal bool Promoted { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            DirectoryCertificate.Dispose();
            DirectoryCertificate = null;
            _disposed = true;
        }
    }

    internal sealed class CertificateAuthorityBackupState
    {
        internal CertificateAuthorityBackupState(
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger)
        {
            State = state;
            Ledger = ledger;
        }

        internal CertificateAuthorityState State { get; }

        internal CertificateLedgerSnapshot Ledger { get; }
    }

    internal sealed class CertificateAuthorityStandbyState : IDisposable
    {
        internal CertificateAuthorityStandbyState(
            CertificateAuthorityState state,
            PeerPkiCacheSnapshot cache,
            byte[] caCertificate,
            byte[] crl)
        {
            State = state;
            Cache = cache;
            CaCertificate = (byte[])caCertificate.Clone();
            Crl = (byte[])crl.Clone();
        }

        internal CertificateAuthorityState State { get; }

        internal PeerPkiCacheSnapshot Cache { get; }

        internal byte[] CaCertificate { get; private set; }

        internal byte[] Crl { get; private set; }

        public void Dispose()
        {
            Clear(CaCertificate);
            Clear(Crl);
            CaCertificate = null;
            Crl = null;
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }
    }
}
