using System;
using System.IO;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed partial class PeerPkiSynchronizationStore
    {
        internal CertificateAuthorityStatus GetStandbyStatus(
            DateTime utcNow)
        {
            EnsureUtc(utcNow, nameof(utcNow));
            return _mutationGate.Execute(() =>
            {
                _journal.EnsureNoActiveTransaction();
                using (StandbyReadSnapshot snapshot =
                    ReadValidatedStandbySnapshot(utcNow))
                {
                    CertificateAuthorityState state = snapshot.State;
                    return new CertificateAuthorityStatus(
                        state.LastBackupUtc.HasValue
                            ? CertificateAuthorityOperationalState.Ready
                            : CertificateAuthorityOperationalState
                                .BackupRequired,
                        CertificateAuthorityIssuerRole.Standby,
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
            });
        }

        internal ExternalTrustInfo GetStandbyExternalTrustInfo(
            DateTime utcNow)
        {
            EnsureUtc(utcNow, nameof(utcNow));
            return _mutationGate.Execute(() =>
            {
                _journal.EnsureNoActiveTransaction();
                using (StandbyReadSnapshot snapshot =
                    ReadValidatedStandbySnapshot(utcNow))
                {
                    return new ExternalTrustInfo(
                        snapshot.State.SiteId,
                        snapshot.CaCertificate,
                        snapshot.State.GetCaSpkiSha256(),
                        SiteCertificateAuthority.CrlRelativePath);
                }
            });
        }

        internal ExternalTrustSnapshot GetStandbyExternalTrustSnapshot(
            DateTime utcNow)
        {
            EnsureUtc(utcNow, nameof(utcNow));
            return _mutationGate.Execute(() =>
            {
                _journal.EnsureNoActiveTransaction();
                using (StandbyReadSnapshot snapshot =
                    ReadValidatedStandbySnapshot(utcNow))
                {
                    var trustInfo = new ExternalTrustInfo(
                        snapshot.State.SiteId,
                        snapshot.CaCertificate,
                        snapshot.State.GetCaSpkiSha256(),
                        SiteCertificateAuthority.CrlRelativePath);
                    var authority = new ExternalTrustAuthority(
                        ExternalTrustAuthorityRole.Current,
                        snapshot.State.CaSerialNumber.Hex,
                        snapshot.CaCertificate,
                        snapshot.State.GetCaSpkiSha256(),
                        SiteCertificateAuthority.GetIssuerCrlRelativePath(
                            snapshot.State.CaSerialNumber),
                        snapshot.State.NotBeforeUtc,
                        snapshot.State.NotAfterUtc);
                    var trustBundle = new ExternalTrustBundle(
                        snapshot.State.SiteId,
                        snapshot.State.TrustRevision,
                        null,
                        ExternalCaRotationPhase.Stable,
                        null,
                        null,
                        null,
                        null,
                        new[] { authority });
                    return new ExternalTrustSnapshot(
                        trustInfo,
                        trustBundle);
                }
            });
        }

        internal byte[] GetStandbyExternalCertificateRevocationList(
            DateTime utcNow)
        {
            EnsureUtc(utcNow, nameof(utcNow));
            return _mutationGate.Execute(() =>
            {
                _journal.EnsureNoActiveTransaction();
                using (StandbyReadSnapshot snapshot =
                    ReadValidatedStandbySnapshot(utcNow))
                {
                    if (snapshot.Crl.Length
                        > ExternalApiContract.MaximumCrlResponseBytes)
                    {
                        throw new InvalidDataException(
                            "The current CRL exceeds the External response limit.");
                    }

                    return (byte[])snapshot.Crl.Clone();
                }
            });
        }

        internal byte[] GetStandbyExternalCertificateRevocationList(
            string caSerialNumber,
            DateTime utcNow)
        {
            EnsureUtc(utcNow, nameof(utcNow));
            return _mutationGate.Execute(() =>
            {
                _journal.EnsureNoActiveTransaction();
                using (StandbyReadSnapshot snapshot =
                    ReadValidatedStandbySnapshot(utcNow))
                {
                    if (!StringComparer.Ordinal.Equals(
                            caSerialNumber,
                            snapshot.State.CaSerialNumber.Hex))
                    {
                        return null;
                    }

                    if (snapshot.Crl.Length
                        > ExternalApiContract.MaximumCrlResponseBytes)
                    {
                        throw new InvalidDataException(
                            "The requested CRL exceeds the External response limit.");
                    }

                    return (byte[])snapshot.Crl.Clone();
                }
            });
        }

        internal void ValidateInstalledStandbyFiles(DateTime utcNow)
        {
            EnsureUtc(utcNow, nameof(utcNow));
            _mutationGate.Execute(() =>
            {
                _journal.EnsureNoActiveTransaction();
                using (StandbyReadSnapshot unused =
                    ReadValidatedStandbySnapshot(utcNow))
                {
                }
            });
        }

        internal static void ValidateStandbySnapshot(
            CertificateAuthorityState state,
            PeerPkiCacheSnapshot cache,
            byte[] caCertificate,
            byte[] crl,
            DateTime utcNow)
        {
            if (state == null || cache == null)
            {
                throw new ArgumentNullException(
                    state == null ? nameof(state) : nameof(cache));
            }

            if (caCertificate == null || caCertificate.Length == 0
                || crl == null || crl.Length == 0)
            {
                throw new ArgumentException(
                    "The standby CA certificate and CRL are required.");
            }

            EnsureUtc(utcNow, nameof(utcNow));
            if (state.Role != CertificateAuthorityRole.Standby)
            {
                throw new InvalidDataException(
                    "The installed PKI state is not a standby.");
            }

            SiteCertificateAuthority.ValidateStoredCaCertificate(
                state,
                caCertificate,
                utcNow);
            EnsureStateAndCacheMatch(state, cache);
            byte[] expectedHash = cache.GetCrlSha256();
            byte[] actualHash;
            using (SHA256 sha256 = SHA256.Create())
            {
                actualHash = sha256.ComputeHash(crl);
            }

            try
            {
                if (!BytesEqual(expectedHash, actualHash))
                {
                    throw new InvalidDataException(
                        "The standby CRL hash differs from peer-cache.xml.");
                }
            }
            finally
            {
                Clear(expectedHash);
                Clear(actualHash);
            }

            ValidateCrlEnvelope(
                state.CrlNumber,
                caCertificate,
                crl,
                utcNow);
            ValidateCacheCertificates(
                cache,
                caCertificate,
                crl,
                utcNow);
        }

        private StandbyReadSnapshot ReadValidatedStandbySnapshot(
            DateTime utcNow)
        {
            EnsureStandbyFileSet();
            byte[] metadata = null;
            byte[] cacheBytes = null;
            byte[] caCertificate = null;
            byte[] crl = null;
            try
            {
                metadata = Read(
                    StateFileTarget.PkiMetadata,
                    CertificateAuthorityStateCodec.MaximumDocumentBytes);
                cacheBytes = Read(
                    StateFileTarget.PeerPkiCache,
                    PeerPkiCacheCodec.MaximumDocumentBytes);
                caCertificate = ReadCaCertificate();
                crl = ReadCrl();
                CertificateAuthorityState state = _stateCodec
                    .DeserializeState(metadata);
                PeerPkiCacheSnapshot cache = _cacheCodec.Deserialize(
                    cacheBytes);
                ValidateStandbySnapshot(
                    state,
                    cache,
                    caCertificate,
                    crl,
                    utcNow);
                return new StandbyReadSnapshot(
                    state,
                    cache,
                    caCertificate,
                    crl);
            }
            finally
            {
                Clear(metadata);
                Clear(cacheBytes);
                Clear(caCertificate);
                Clear(crl);
            }
        }

        private void EnsureStandbyFileSet()
        {
            StateFileTarget[] required =
            {
                StateFileTarget.PkiMetadata,
                StateFileTarget.PeerPkiCache,
                StateFileTarget.CertificateRevocationList,
                StateFileTarget.CaCertificate
            };
            foreach (StateFileTarget target in required)
            {
                if (!_writer.Exists(target))
                {
                    throw new FileNotFoundException(
                        "The standby PKI state is incomplete.");
                }
            }

            StateFileTarget[] forbidden =
            {
                StateFileTarget.CertificateLedger,
                StateFileTarget.CaPrivateKey
            };
            foreach (StateFileTarget target in forbidden)
            {
                if (_writer.Exists(target)
                    || _writer.BackupExists(target))
                {
                    throw new InvalidDataException(
                        "The standby contains an active-issuer-only PKI file.");
                }
            }
        }

        private sealed class StandbyReadSnapshot : IDisposable
        {
            internal StandbyReadSnapshot(
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
        }
    }
}
