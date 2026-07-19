using System;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed class IssuedCertificateArtifact : IDisposable
    {
        private readonly byte[] _certificateDer;
        private byte[] _privateKeyPkcs8;
        private bool _disposed;

        internal IssuedCertificateArtifact(
            PkiSerialNumber serialNumber,
            DateTime notBeforeUtc,
            DateTime notAfterUtc,
            byte[] certificateDer,
            byte[] privateKeyPkcs8)
        {
            SerialNumber = serialNumber
                ?? throw new ArgumentNullException(nameof(serialNumber));
            EnsureUtc(notBeforeUtc, nameof(notBeforeUtc));
            EnsureUtc(notAfterUtc, nameof(notAfterUtc));
            if (notAfterUtc <= notBeforeUtc)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(notAfterUtc),
                    notAfterUtc,
                    "Certificate expiry must be after its activation time.");
            }

            if (certificateDer == null || certificateDer.Length == 0)
            {
                throw new ArgumentException(
                    "Certificate DER must not be empty.",
                    nameof(certificateDer));
            }

            NotBeforeUtc = notBeforeUtc;
            NotAfterUtc = notAfterUtc;
            _certificateDer = (byte[])certificateDer.Clone();
            _privateKeyPkcs8 = privateKeyPkcs8 == null
                ? null
                : (byte[])privateKeyPkcs8.Clone();
        }

        internal PkiSerialNumber SerialNumber { get; }

        internal DateTime NotBeforeUtc { get; }

        internal DateTime NotAfterUtc { get; }

        internal bool HasPrivateKey => _privateKeyPkcs8 != null;

        internal byte[] GetCertificateDer()
        {
            ThrowIfDisposed();
            return (byte[])_certificateDer.Clone();
        }

        // The caller owns the returned plaintext PKCS#8 buffer and must protect it
        // immediately, then clear it in a finally block.
        internal byte[] ExportPrivateKeyPkcs8()
        {
            ThrowIfDisposed();
            if (_privateKeyPkcs8 == null)
            {
                throw new InvalidOperationException(
                    "This issued certificate does not include a locally generated private key.");
            }

            return (byte[])_privateKeyPkcs8.Clone();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_privateKeyPkcs8 != null)
            {
                Array.Clear(_privateKeyPkcs8, 0, _privateKeyPkcs8.Length);
                _privateKeyPkcs8 = null;
            }

            _disposed = true;
        }

        private static void EnsureUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Certificate timestamps must use DateTimeKind.Utc.",
                    parameterName);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(IssuedCertificateArtifact));
            }
        }
    }
}
