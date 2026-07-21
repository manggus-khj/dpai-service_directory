using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed partial class PeerPkiSynchronizationStore
        : IPeerTlsTrustProvider
    {
        private readonly StateMutationGate _mutationGate;
        private readonly AtomicFileWriter _writer;
        private readonly RecoveryJournalManager _journal;
        private readonly CertificateAuthorityStateCodec _stateCodec;
        private readonly PeerPkiCacheCodec _cacheCodec;

        internal PeerPkiSynchronizationStore(
            string stateDirectoryPath,
            StateMutationGate mutationGate)
            : this(
                new StateStoragePathPolicy(stateDirectoryPath),
                mutationGate,
                NoOpRecoveryJournalFaultInjector.Instance)
        {
        }

        internal PeerPkiSynchronizationStore(
            StateStoragePathPolicy pathPolicy,
            StateMutationGate mutationGate,
            IRecoveryJournalFaultInjector faultInjector)
        {
            if (pathPolicy == null)
            {
                throw new ArgumentNullException(nameof(pathPolicy));
            }

            _mutationGate = mutationGate
                ?? throw new ArgumentNullException(nameof(mutationGate));
            _writer = new AtomicFileWriter(pathPolicy);
            _journal = new RecoveryJournalManager(
                pathPolicy,
                _writer,
                faultInjector
                    ?? NoOpRecoveryJournalFaultInjector.Instance);
            _stateCodec = new CertificateAuthorityStateCodec();
            _cacheCodec = new PeerPkiCacheCodec();
        }

        internal CertificateAuthorityIssuerRole GetRole()
        {
            return _mutationGate.Execute(() =>
            {
                _journal.EnsureNoActiveTransaction();
                CertificateAuthorityState state = ReadState();
                return state.Role == CertificateAuthorityRole.ActiveIssuer
                    ? CertificateAuthorityIssuerRole.ActiveIssuer
                    : CertificateAuthorityIssuerRole.Standby;
            });
        }

        internal PeerPkiState GetActiveState()
        {
            return _mutationGate.Execute(() =>
            {
                _journal.EnsureNoActiveTransaction();
                CertificateAuthorityState state = ReadState();
                if (state.Role != CertificateAuthorityRole.ActiveIssuer)
                {
                    throw new InvalidOperationException(
                        "Only the active issuer can publish Peer PKI state.");
                }

                CertificateLedgerSnapshot ledger = ReadLedger();
                byte[] caCertificate = null;
                byte[] crl = null;
                try
                {
                    caCertificate = ReadCaCertificate();
                    crl = ReadCrl();
                    CertificateAuthorityStore.ValidateCrl(
                        state,
                        ledger,
                        caCertificate,
                        crl);
                    return CreatePeerState(state, ledger, crl);
                }
                finally
                {
                    Clear(caCertificate);
                    Clear(crl);
                }
            });
        }

        internal PeerPkiState GetKnownStandbyState()
        {
            return _mutationGate.Execute(() =>
            {
                _journal.EnsureNoActiveTransaction();
                CertificateAuthorityState state = ReadState();
                if (state.Role != CertificateAuthorityRole.Standby)
                {
                    throw new InvalidOperationException(
                        "Only a standby has a Peer PKI cache.");
                }

                PeerPkiCacheSnapshot cache = ReadCache();
                byte[] crl = ReadCrl();
                try
                {
                    EnsureStateAndCacheMatch(state, cache);
                    return CreatePeerState(cache, crl);
                }
                finally
                {
                    Clear(crl);
                }
            });
        }

        internal void ApplyStandbyState(
            PeerPkiState received,
            DateTime utcNow)
        {
            if (received == null)
            {
                throw new ArgumentNullException(nameof(received));
            }

            EnsureUtc(utcNow, nameof(utcNow));
            _mutationGate.Execute(() =>
            {
                _journal.EnsureNoActiveTransaction();
                CertificateAuthorityState currentState = ReadState();
                if (currentState.Role != CertificateAuthorityRole.Standby)
                {
                    throw new InvalidOperationException(
                        "Peer PKI state can only be applied to a standby.");
                }

                byte[] currentMetadata = null;
                byte[] currentCacheBytes = null;
                byte[] currentCrl = null;
                byte[] caCertificate = null;
                byte[] nextMetadata = null;
                byte[] nextCacheBytes = null;
                byte[] nextCrl = null;
                try
                {
                    currentMetadata = Read(
                        StateFileTarget.PkiMetadata,
                        CertificateAuthorityStateCodec.MaximumDocumentBytes);
                    currentCacheBytes = Read(
                        StateFileTarget.PeerPkiCache,
                        PeerPkiCacheCodec.MaximumDocumentBytes);
                    currentCrl = ReadCrl();
                    caCertificate = ReadCaCertificate();
                    SiteCertificateAuthority.ValidateStoredCaCertificate(
                        currentState,
                        caCertificate,
                        utcNow);
                    PeerPkiCacheSnapshot currentCache =
                        _cacheCodec.Deserialize(currentCacheBytes);
                    EnsureStateAndCacheMatch(currentState, currentCache);

                    if (received.IssuerInstanceId
                            != currentState.IssuerInstanceId
                        || received.PkiRevision
                            < currentState.PkiRevision
                        || received.CrlNumber < currentState.CrlNumber)
                    {
                        throw new InvalidDataException(
                            "The Peer PKI state changes the issuer or rolls back a high-water value.");
                    }

                    PeerPkiCacheSnapshot nextCache = CreateCache(received);
                    nextCacheBytes = _cacheCodec.Serialize(nextCache);
                    nextCrl = received.GetCrl();
                    ValidateReceivedState(
                        received,
                        nextCache,
                        caCertificate,
                        nextCrl,
                        utcNow);

                    if (received.PkiRevision == currentState.PkiRevision)
                    {
                        if (received.CrlNumber != currentState.CrlNumber
                            || !BytesEqual(
                                currentCacheBytes,
                                nextCacheBytes)
                            || !BytesEqual(currentCrl, nextCrl))
                        {
                            throw new InvalidDataException(
                                "The Peer PKI state differs at the current revision.");
                        }

                        return;
                    }

                    CertificateAuthorityState nextState =
                        currentState.WithHighWater(
                            received.PkiRevision,
                            received.CrlNumber);
                    nextMetadata = _stateCodec.SerializeState(nextState);
                    var changes = new List<StateFileChange>
                    {
                        new StateFileChange(
                            StateFileTarget.PkiMetadata,
                            true,
                            currentMetadata,
                            true,
                            nextMetadata),
                        new StateFileChange(
                            StateFileTarget.PeerPkiCache,
                            true,
                            currentCacheBytes,
                            true,
                            nextCacheBytes),
                        new StateFileChange(
                            StateFileTarget.CertificateRevocationList,
                            true,
                            currentCrl,
                            true,
                            nextCrl)
                    };
                    _journal.Commit(
                        changes.AsReadOnly(),
                        () => ValidateAppliedStandbyState(
                            nextState,
                            nextCache,
                            caCertificate,
                            utcNow));
                }
                finally
                {
                    Clear(currentMetadata);
                    Clear(currentCacheBytes);
                    Clear(currentCrl);
                    Clear(caCertificate);
                    Clear(nextMetadata);
                    Clear(nextCacheBytes);
                    Clear(nextCrl);
                }
            });
        }

        PeerTlsTrustSnapshot IPeerTlsTrustProvider.CapturePeerTlsTrust(
            string peerEndpoint,
            DateTime utcNow)
        {
            EnsureUtc(utcNow, nameof(utcNow));
            return _mutationGate.Execute(() =>
            {
                _journal.EnsureNoActiveTransaction();
                CertificateAuthorityState state = ReadState();
                byte[] caCertificate = null;
                byte[] crl = null;
                try
                {
                    caCertificate = ReadCaCertificate();
                    crl = ReadCrl();
                    SiteCertificateAuthority.ValidateStoredCaCertificate(
                        state,
                        caCertificate,
                        utcNow);
                    ValidateCrlEnvelope(
                        state.CrlNumber,
                        caCertificate,
                        crl,
                        utcNow);
                    return new PeerTlsTrustSnapshot(
                        peerEndpoint,
                        caCertificate,
                        crl);
                }
                finally
                {
                    Clear(caCertificate);
                    Clear(crl);
                }
            });
        }

        private void ValidateAppliedStandbyState(
            CertificateAuthorityState expectedState,
            PeerPkiCacheSnapshot expectedCache,
            byte[] caCertificate,
            DateTime utcNow)
        {
            CertificateAuthorityState state = ReadState();
            PeerPkiCacheSnapshot cache = ReadCache();
            byte[] crl = ReadCrl();
            try
            {
                if (state.Role != CertificateAuthorityRole.Standby
                    || state.SiteId != expectedState.SiteId
                    || state.IssuerInstanceId
                        != expectedState.IssuerInstanceId
                    || state.PkiRevision != expectedState.PkiRevision
                    || state.CrlNumber != expectedState.CrlNumber)
                {
                    throw new InvalidDataException(
                        "The applied standby PKI metadata is inconsistent.");
                }

                EnsureStateAndCacheMatch(state, cache);
                byte[] actualCache = null;
                byte[] expectedCacheBytes = null;
                try
                {
                    actualCache = _cacheCodec.Serialize(cache);
                    expectedCacheBytes = _cacheCodec.Serialize(expectedCache);
                    if (!BytesEqual(actualCache, expectedCacheBytes))
                    {
                        throw new InvalidDataException(
                            "The applied standby Peer cache differs from the committed state.");
                    }
                }
                finally
                {
                    Clear(actualCache);
                    Clear(expectedCacheBytes);
                }

                ValidateCrlEnvelope(
                    state.CrlNumber,
                    caCertificate,
                    crl,
                    utcNow);
            }
            finally
            {
                Clear(crl);
            }
        }

        private static void ValidateReceivedState(
            PeerPkiState state,
            PeerPkiCacheSnapshot cache,
            byte[] caCertificate,
            byte[] crl,
            DateTime utcNow)
        {
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

        private static void ValidateCacheCertificates(
            PeerPkiCacheSnapshot cache,
            byte[] caCertificate,
            byte[] crl,
            DateTime utcNow)
        {
            var authority = new X509Certificate(caCertificate);
            X509Crl parsedCrl = new X509CrlParser().ReadCrl(crl);
            foreach (PeerPkiCacheCertificate certificate in
                cache.ActiveCertificates)
            {
                if (certificate.NotAfterUtc <= utcNow
                    || certificate.NotAfterUtc
                        > authority.NotAfter.ToUniversalTime()
                    || certificate.SerialNumber.Hex ==
                        authority.SerialNumber.ToString(16)
                            .PadLeft(32, '0')
                            .ToUpperInvariant())
                {
                    throw new InvalidDataException(
                        "The Peer PKI state contains an expired CURRENT certificate.");
                }

                PkiSerialNumber serial;
                if (!PkiSerialNumber.TryParse(
                        certificate.SerialNumber.Hex,
                        out serial)
                    || parsedCrl.GetRevokedCertificate(serial.Value) != null)
                {
                    throw new InvalidDataException(
                        "The Peer PKI CURRENT mapping contains a revoked serial.");
                }
            }
        }

        internal static void ValidateCrlEnvelope(
            ulong expectedCrlNumber,
            byte[] caCertificate,
            byte[] crlDer,
            DateTime utcNow)
        {
            var authority = new X509Certificate(caCertificate);
            X509Crl crl;
            try
            {
                crl = new X509CrlParser().ReadCrl(crlDer);
                if (crl == null)
                {
                    throw new InvalidDataException(
                        "The Peer PKI CRL is missing.");
                }

                crl.Verify(authority.GetPublicKey());
            }
            catch (Exception exception) when (
                exception is GeneralSecurityException
                || exception is IOException
                || exception is ArgumentException)
            {
                throw new InvalidDataException(
                    "The Peer PKI CRL signature is invalid.",
                    exception);
            }

            byte[] canonical = crl.GetEncoded();
            try
            {
                Asn1OctetString extension = crl.GetExtensionValue(
                    X509Extensions.CrlNumber);
                if (extension == null)
                {
                    throw new InvalidDataException(
                        "The Peer PKI CRL number is missing.");
                }

                DerInteger number = DerInteger.GetInstance(
                    Asn1Object.FromByteArray(extension.GetOctets()));
                ulong parsedNumber;
                DateTime thisUpdateUtc = crl.ThisUpdate.ToUniversalTime();
                DateTime? nextUpdateUtc = crl.NextUpdate.HasValue
                    ? crl.NextUpdate.Value.ToUniversalTime()
                    : (DateTime?)null;
                if (!BytesEqual(canonical, crlDer)
                    || !crl.IssuerDN.Equivalent(authority.SubjectDN)
                    || number.PositiveValue.SignValue <= 0
                    || !ulong.TryParse(
                        number.PositiveValue.ToString(),
                        out parsedNumber)
                    || parsedNumber != expectedCrlNumber
                    || thisUpdateUtc
                        < authority.NotBefore.ToUniversalTime()
                    || thisUpdateUtc > utcNow
                    || !nextUpdateUtc.HasValue
                    || nextUpdateUtc.Value <= utcNow
                    || nextUpdateUtc.Value
                        > authority.NotAfter.ToUniversalTime())
                {
                    throw new InvalidDataException(
                        "The Peer PKI CRL issuer, number, encoding, or validity is invalid.");
                }
            }
            finally
            {
                Clear(canonical);
            }
        }

        private static PeerPkiState CreatePeerState(
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger,
            byte[] crl)
        {
            var certificates = new List<PeerActiveCertificate>();
            foreach (CertificateLedgerEntry entry in ledger.EntriesBySerial
                .Values
                .Where(value => value.Status
                    == CertificateLedgerStatus.Current)
                .OrderBy(value => value.ProductCode.Value,
                    StringComparer.Ordinal))
            {
                byte[] leafSha256 = entry.GetLeafCertificateSha256();
                try
                {
                    certificates.Add(new PeerActiveCertificate(
                        entry.ProductCode.Value,
                        entry.SerialNumber.Hex,
                        Convert.ToBase64String(leafSha256),
                        entry.NotAfterUtc));
                }
                finally
                {
                    Clear(leafSha256);
                }
            }

            byte[] crlSha256 = ComputeSha256(crl);
            try
            {
                return new PeerPkiState(
                    state.IssuerInstanceId,
                    state.PkiRevision,
                    state.CrlNumber,
                    Convert.ToBase64String(crlSha256),
                    crl,
                    certificates.AsReadOnly());
            }
            finally
            {
                Clear(crlSha256);
            }
        }

        private static PeerPkiState CreatePeerState(
            PeerPkiCacheSnapshot cache,
            byte[] crl)
        {
            var certificates = new List<PeerActiveCertificate>();
            foreach (PeerPkiCacheCertificate entry in cache.ActiveCertificates)
            {
                byte[] leafSha256 = entry.GetLeafSha256();
                try
                {
                    certificates.Add(new PeerActiveCertificate(
                        entry.ProductCode.Value,
                        entry.SerialNumber.Hex,
                        Convert.ToBase64String(leafSha256),
                        entry.NotAfterUtc));
                }
                finally
                {
                    Clear(leafSha256);
                }
            }

            byte[] crlSha256 = cache.GetCrlSha256();
            try
            {
                return new PeerPkiState(
                    cache.IssuerInstanceId,
                    cache.PkiRevision,
                    cache.CrlNumber,
                    Convert.ToBase64String(crlSha256),
                    crl,
                    certificates.AsReadOnly());
            }
            finally
            {
                Clear(crlSha256);
            }
        }

        private static PeerPkiCacheSnapshot CreateCache(PeerPkiState state)
        {
            byte[] crlSha256 = Convert.FromBase64String(state.CrlSha256);
            try
            {
                var certificates = new List<PeerPkiCacheCertificate>();
                foreach (PeerActiveCertificate certificate in
                    state.ActiveCertificates)
                {
                    ProductCode productCode;
                    CertificateSerialNumber serialNumber;
                    if (!ProductCode.TryCreate(
                            certificate.ProductCode,
                            out productCode)
                        || !CertificateSerialNumber.TryCreate(
                            certificate.SerialNumber,
                            out serialNumber))
                    {
                        throw new InvalidDataException(
                            "The Peer PKI CURRENT mapping is invalid.");
                    }

                    byte[] leafSha256 = Convert.FromBase64String(
                        certificate.LeafSha256);
                    try
                    {
                        certificates.Add(new PeerPkiCacheCertificate(
                            productCode,
                            serialNumber,
                            leafSha256,
                            certificate.NotAfterUtc));
                    }
                    finally
                    {
                        Clear(leafSha256);
                    }
                }

                return new PeerPkiCacheSnapshot(
                    state.IssuerInstanceId,
                    state.PkiRevision,
                    state.CrlNumber,
                    crlSha256,
                    certificates);
            }
            finally
            {
                Clear(crlSha256);
            }
        }

        private CertificateAuthorityState ReadState()
        {
            byte[] contents = Read(
                StateFileTarget.PkiMetadata,
                CertificateAuthorityStateCodec.MaximumDocumentBytes);
            try
            {
                return _stateCodec.DeserializeState(contents);
            }
            finally
            {
                Clear(contents);
            }
        }

        private CertificateLedgerSnapshot ReadLedger()
        {
            byte[] contents = Read(
                StateFileTarget.CertificateLedger,
                CertificateAuthorityStateCodec.MaximumDocumentBytes);
            try
            {
                return _stateCodec.DeserializeLedger(contents);
            }
            finally
            {
                Clear(contents);
            }
        }

        private PeerPkiCacheSnapshot ReadCache()
        {
            byte[] contents = Read(
                StateFileTarget.PeerPkiCache,
                PeerPkiCacheCodec.MaximumDocumentBytes);
            try
            {
                return _cacheCodec.Deserialize(contents);
            }
            finally
            {
                Clear(contents);
            }
        }

        private byte[] ReadCaCertificate()
        {
            return Read(
                StateFileTarget.CaCertificate,
                CertificateAuthorityStore.MaximumCertificateBytes);
        }

        private byte[] ReadCrl()
        {
            return Read(
                StateFileTarget.CertificateRevocationList,
                CertificateAuthorityStore.MaximumCrlBytes);
        }

        private byte[] Read(StateFileTarget target, int maximumBytes)
        {
            return _writer.Read(target, maximumBytes);
        }

        private static void EnsureStateAndCacheMatch(
            CertificateAuthorityState state,
            PeerPkiCacheSnapshot cache)
        {
            if (state.IssuerInstanceId != cache.IssuerInstanceId
                || state.PkiRevision != cache.PkiRevision
                || state.CrlNumber != cache.CrlNumber)
            {
                throw new InvalidDataException(
                    "The standby metadata and Peer PKI cache differ.");
            }
        }

        private static byte[] ComputeSha256(byte[] value)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(value);
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
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

        private static void EnsureUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Peer PKI time must use DateTimeKind.Utc.",
                    parameterName);
            }
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
