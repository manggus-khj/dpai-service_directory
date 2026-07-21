using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed partial class CertificateAuthorityRoleRepairStore
    {
        private static readonly StateFileTarget[] RoleTargets =
        {
            StateFileTarget.PkiMetadata,
            StateFileTarget.CertificateLedger,
            StateFileTarget.PeerPkiCache,
            StateFileTarget.CertificateRevocationList,
            StateFileTarget.CaCertificate,
            StateFileTarget.CaPrivateKey
        };

        private readonly StateMutationGate _mutationGate;
        private readonly AtomicFileWriter _writer;
        private readonly RecoveryJournalManager _journal;
        private readonly CertificateAuthorityStateCodec _stateCodec;
        private readonly PeerPkiCacheCodec _cacheCodec;
        private readonly ICaPrivateKeyProtector _protector;
        private readonly CaBackupFileStore _backupFileStore;

        internal CertificateAuthorityRoleRepairStore(
            StateStoragePathPolicy pathPolicy,
            StateMutationGate mutationGate,
            ICaPrivateKeyProtector protector,
            ISecretFileAccessPolicy accessPolicy,
            IRecoveryJournalFaultInjector faultInjector)
        {
            if (pathPolicy == null)
            {
                throw new ArgumentNullException(nameof(pathPolicy));
            }

            _mutationGate = mutationGate
                ?? throw new ArgumentNullException(nameof(mutationGate));
            _protector = protector
                ?? throw new ArgumentNullException(nameof(protector));
            if (accessPolicy == null)
            {
                throw new ArgumentNullException(nameof(accessPolicy));
            }

            _writer = new AtomicFileWriter(pathPolicy, accessPolicy);
            _journal = new RecoveryJournalManager(
                pathPolicy,
                _writer,
                faultInjector
                    ?? NoOpRecoveryJournalFaultInjector.Instance);
            _stateCodec = new CertificateAuthorityStateCodec();
            _cacheCodec = new PeerPkiCacheCodec();
            _backupFileStore = new CaBackupFileStore(
                pathPolicy,
                accessPolicy);
        }

        internal CertificateAuthorityRoleRepairStateResult
            ConfigureStandby(
                CaBackupPayload payload,
                byte[] encryptedBackup,
                DirectoryEndpointIdentity directoryIdentity,
                DateTime utcNow)
        {
            ValidateOperationArguments(
                payload,
                encryptedBackup,
                directoryIdentity,
                utcNow);
            return _mutationGate.Execute(() =>
            {
                _journal.EnsureNoActiveTransaction();
                CertificateAuthorityBackupState backup =
                    ValidateBackup(payload, utcNow);
                EnsureExistingSiteMatchesBackup(
                    backup,
                    payload,
                    utcNow);

                CertificateAuthorityState standbyState =
                    CreateStandbyState(backup.State);
                PeerPkiCacheSnapshot cache = CreateCache(
                    backup.State,
                    backup.Ledger,
                    payload.CrlDer,
                    utcNow);
                IssuedCertificateArtifact directoryCertificate =
                    IssueDirectoryCertificate(
                        backup.State,
                        backup.Ledger,
                        payload,
                        directoryIdentity,
                        utcNow);
                CaBackupFileArtifact backupArtifact = null;
                try
                {
                    backupArtifact = _backupFileStore.WriteNew(
                        backup.State.SiteId,
                        utcNow,
                        encryptedBackup);
                    CommitStandby(
                        standbyState,
                        cache,
                        payload.CaCertificateDer,
                        payload.CrlDer,
                        utcNow);
                    return new CertificateAuthorityRoleRepairStateResult(
                        directoryCertificate,
                        backupArtifact,
                        false);
                }
                catch (Exception exception)
                {
                    directoryCertificate.Dispose();
                    if (backupArtifact != null
                        && !(exception is RecoveryRequiredException))
                    {
                        _backupFileStore.DeleteUnapproved(
                            backupArtifact.FileName);
                    }

                    throw;
                }
            });
        }

        internal CertificateAuthorityRoleRepairStateResult PromoteStandby(
            CaBackupPayload payload,
            string password,
            Guid installedInstanceId,
            DirectoryEndpointIdentity directoryIdentity,
            DateTime utcNow)
        {
            ValidateOperationArguments(
                payload,
                directoryIdentity,
                utcNow);
            if (installedInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Installed instance ID must not be empty.",
                    nameof(installedInstanceId));
            }

            CaBackupCodec.ValidatePassword(password);
            return _mutationGate.Execute(() =>
            {
                _journal.EnsureNoActiveTransaction();
                CertificateAuthorityBackupState backup =
                    ValidateBackup(payload, utcNow);
                CertificateAuthorityStandbyState current =
                    ReadAndValidateStandby(utcNow);
                try
                {
                    EnsurePromotionIsCurrent(
                        current,
                        backup,
                        payload,
                        utcNow);
                }
                finally
                {
                    current.Dispose();
                }

                if (backup.State.PkiRevision == ulong.MaxValue)
                {
                    throw new InvalidOperationException(
                        "The PKI revision is exhausted and standby promotion is forbidden.");
                }

                ulong promotedRevision = checked(
                    backup.State.PkiRevision + 1UL);
                CertificateLedgerSnapshot promotedLedger =
                    new CertificateLedgerSnapshot(
                        backup.Ledger.EntriesBySerial.Values,
                        promotedRevision,
                        backup.Ledger.CrlNumber);
                CertificateAuthorityState promotedState =
                    CreatePromotedState(
                        backup.State,
                        installedInstanceId,
                        promotedRevision,
                        utcNow);
                ValidatePromotionDirectory(promotedLedger);
                IssuedCertificateArtifact directoryCertificate =
                    IssueDirectoryCertificate(
                        promotedState,
                        promotedLedger,
                        payload,
                        directoryIdentity,
                        utcNow);
                byte[] encryptedPromotionBackup = null;
                CaBackupFileArtifact backupArtifact = null;
                try
                {
                    using (var promotionPayload = new CaBackupPayload(
                        _stateCodec.SerializeState(promotedState),
                        _stateCodec.SerializeLedger(promotedLedger),
                        payload.CaCertificateDer,
                        payload.CrlDer,
                        payload.PrivateKeyPkcs8))
                    {
                        encryptedPromotionBackup = new CaBackupCodec()
                            .Encrypt(promotionPayload, password);
                    }

                    backupArtifact = _backupFileStore.WriteNew(
                        promotedState.SiteId,
                        utcNow,
                        encryptedPromotionBackup);
                    CommitPromotion(
                        promotedState,
                        promotedLedger,
                        payload,
                        utcNow);
                    return new CertificateAuthorityRoleRepairStateResult(
                        directoryCertificate,
                        backupArtifact,
                        true);
                }
                catch (Exception exception)
                {
                    directoryCertificate.Dispose();
                    if (backupArtifact != null
                        && !(exception is RecoveryRequiredException))
                    {
                        _backupFileStore.DeleteUnapproved(
                            backupArtifact.FileName);
                    }

                    throw;
                }
                finally
                {
                    Clear(encryptedPromotionBackup);
                }
            });
        }

        private void CommitStandby(
            CertificateAuthorityState state,
            PeerPkiCacheSnapshot cache,
            byte[] caCertificate,
            byte[] crl,
            DateTime utcNow)
        {
            byte[] metadata = null;
            byte[] cacheBytes = null;
            byte[][] before = null;
            try
            {
                metadata = _stateCodec.SerializeState(state);
                cacheBytes = _cacheCodec.Serialize(cache);
                before = ReadBeforeImages();
                var changes = new List<StateFileChange>
                {
                    CreateChange(0, before, true, metadata),
                    CreateChange(2, before, true, cacheBytes),
                    CreateChange(3, before, true, crl),
                    CreateChange(4, before, true, caCertificate)
                };
                AddDeletionIfPresent(changes, 1, before);
                AddDeletionIfPresent(changes, 5, before);
                changes.Sort((left, right) =>
                    ((int)left.Target).CompareTo((int)right.Target));
                _journal.Commit(
                    changes.AsReadOnly(),
                    () => ValidateCommittedStandby(state, cache, utcNow));
            }
            finally
            {
                Clear(metadata);
                Clear(cacheBytes);
                Clear(before);
            }
        }

        private void CommitPromotion(
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger,
            CaBackupPayload payload,
            DateTime utcNow)
        {
            byte[] metadata = null;
            byte[] ledgerBytes = null;
            byte[] protectedKey = null;
            byte[][] before = null;
            try
            {
                metadata = _stateCodec.SerializeState(state);
                ledgerBytes = _stateCodec.SerializeLedger(ledger);
                protectedKey = _protector.Protect(
                    payload.PrivateKeyPkcs8);
                before = ReadBeforeImages();
                var changes = new List<StateFileChange>
                {
                    CreateChange(0, before, true, metadata),
                    CreateChange(1, before, true, ledgerBytes),
                    CreateChange(2, before, false, null),
                    CreateChange(5, before, true, protectedKey)
                };
                if (!BytesEqual(before[3], payload.CrlDer))
                {
                    changes.Add(CreateChange(
                        3,
                        before,
                        true,
                        payload.CrlDer));
                }

                if (!BytesEqual(
                        before[4],
                        payload.CaCertificateDer))
                {
                    changes.Add(CreateChange(
                        4,
                        before,
                        true,
                        payload.CaCertificateDer));
                }

                changes.Sort((left, right) =>
                    ((int)left.Target).CompareTo((int)right.Target));
                _journal.Commit(
                    changes.AsReadOnly(),
                    () => ValidateCommittedPromotion(
                        state,
                        ledger,
                        payload.CaCertificateDer,
                        payload.CrlDer,
                        utcNow));
            }
            finally
            {
                Clear(metadata);
                Clear(ledgerBytes);
                Clear(protectedKey);
                Clear(before);
            }
        }

        private void ValidateCommittedStandby(
            CertificateAuthorityState expectedState,
            PeerPkiCacheSnapshot expectedCache,
            DateTime utcNow)
        {
            CertificateAuthorityStandbyState actual =
                ReadAndValidateStandby(utcNow);
            byte[] actualState = null;
            byte[] expectedStateBytes = null;
            byte[] actualCache = null;
            byte[] expectedCacheBytes = null;
            try
            {
                actualState = _stateCodec.SerializeState(actual.State);
                expectedStateBytes = _stateCodec.SerializeState(
                    expectedState);
                actualCache = _cacheCodec.Serialize(actual.Cache);
                expectedCacheBytes = _cacheCodec.Serialize(expectedCache);
                if (!BytesEqual(actualState, expectedStateBytes)
                    || !BytesEqual(actualCache, expectedCacheBytes))
                {
                    throw new InvalidDataException(
                        "The committed standby state differs from the requested state.");
                }
            }
            finally
            {
                Clear(actualState);
                Clear(expectedStateBytes);
                Clear(actualCache);
                Clear(expectedCacheBytes);
                actual.Dispose();
            }
        }

        private void ValidateCommittedPromotion(
            CertificateAuthorityState expectedState,
            CertificateLedgerSnapshot expectedLedger,
            byte[] expectedCaCertificate,
            byte[] expectedCrl,
            DateTime utcNow)
        {
            EnsureRequiredRoleFiles(
                CertificateAuthorityRole.ActiveIssuer);
            byte[] metadata = null;
            byte[] ledgerBytes = null;
            byte[] caCertificate = null;
            byte[] crl = null;
            byte[] protectedKey = null;
            byte[] privateKey = null;
            try
            {
                metadata = ReadRequired(
                    StateFileTarget.PkiMetadata,
                    CertificateAuthorityStateCodec.MaximumDocumentBytes);
                ledgerBytes = ReadRequired(
                    StateFileTarget.CertificateLedger,
                    CertificateAuthorityStateCodec.MaximumDocumentBytes);
                caCertificate = ReadRequired(
                    StateFileTarget.CaCertificate,
                    CertificateAuthorityStore.MaximumCertificateBytes);
                crl = ReadRequired(
                    StateFileTarget.CertificateRevocationList,
                    CertificateAuthorityStore.MaximumCrlBytes);
                protectedKey = ReadRequired(
                    StateFileTarget.CaPrivateKey,
                    DpapiMachineCaPrivateKeyProtector.MaximumProtectedBytes);
                privateKey = _protector.Unprotect(protectedKey);
                CertificateAuthorityState actualState =
                    _stateCodec.DeserializeState(metadata);
                CertificateLedgerSnapshot actualLedger =
                    _stateCodec.DeserializeLedger(ledgerBytes);
                if (actualState.Role
                        != CertificateAuthorityRole.ActiveIssuer
                    || actualState.SiteId != expectedState.SiteId
                    || actualState.IssuerInstanceId
                        != expectedState.IssuerInstanceId
                    || actualState.PkiRevision
                        != expectedState.PkiRevision
                    || actualLedger.PkiRevision
                        != expectedLedger.PkiRevision
                    || !BytesEqual(caCertificate, expectedCaCertificate)
                    || !BytesEqual(crl, expectedCrl))
                {
                    throw new InvalidDataException(
                        "The committed promoted issuer state is inconsistent.");
                }

                ValidateActiveState(
                    actualState,
                    actualLedger,
                    caCertificate,
                    crl,
                    privateKey,
                    utcNow);
                ValidatePromotionDirectory(actualLedger);
            }
            finally
            {
                Clear(metadata);
                Clear(ledgerBytes);
                Clear(caCertificate);
                Clear(crl);
                Clear(protectedKey);
                Clear(privateKey);
            }
        }

        private CertificateAuthorityBackupState ValidateBackup(
            CaBackupPayload payload,
            DateTime utcNow)
        {
            CertificateAuthorityState state = _stateCodec.DeserializeState(
                payload.Metadata);
            CertificateLedgerSnapshot ledger = _stateCodec
                .DeserializeLedger(payload.Ledger);
            if (state.Role != CertificateAuthorityRole.ActiveIssuer
                || !state.LastBackupUtc.HasValue
                || state.LastBackupUtc.Value > utcNow
                || state.PkiRevision != ledger.PkiRevision
                || state.CrlNumber != ledger.CrlNumber)
            {
                throw new InvalidDataException(
                    "The selected backup is not a complete active issuer backup.");
            }

            ValidateActiveState(
                state,
                ledger,
                payload.CaCertificateDer,
                payload.CrlDer,
                payload.PrivateKeyPkcs8,
                utcNow);
            PeerPkiSynchronizationStore.ValidateCrlEnvelope(
                state.CrlNumber,
                payload.CaCertificateDer,
                payload.CrlDer,
                utcNow);
            return new CertificateAuthorityBackupState(state, ledger);
        }

        private static void ValidateActiveState(
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger,
            byte[] caCertificate,
            byte[] crl,
            byte[] privateKey,
            DateTime utcNow)
        {
            if (state.PkiRevision != ledger.PkiRevision
                || state.CrlNumber != ledger.CrlNumber)
            {
                throw new InvalidDataException(
                    "CA metadata and ledger high-water values differ.");
            }

            CertificateAuthorityStore.ValidateAuthority(
                state,
                caCertificate,
                privateKey,
                utcNow);
            CertificateAuthorityStore.ValidateLedgerCertificates(
                ledger,
                caCertificate);
            CertificateAuthorityStore.ValidateCrl(
                state,
                ledger,
                caCertificate,
                crl);
        }

        private CertificateAuthorityStandbyState
            ReadAndValidateStandby(DateTime utcNow)
        {
            EnsureRequiredRoleFiles(CertificateAuthorityRole.Standby);
            byte[] metadata = null;
            byte[] cacheBytes = null;
            byte[] caCertificate = null;
            byte[] crl = null;
            try
            {
                metadata = ReadRequired(
                    StateFileTarget.PkiMetadata,
                    CertificateAuthorityStateCodec.MaximumDocumentBytes);
                cacheBytes = ReadRequired(
                    StateFileTarget.PeerPkiCache,
                    PeerPkiCacheCodec.MaximumDocumentBytes);
                caCertificate = ReadRequired(
                    StateFileTarget.CaCertificate,
                    CertificateAuthorityStore.MaximumCertificateBytes);
                crl = ReadRequired(
                    StateFileTarget.CertificateRevocationList,
                    CertificateAuthorityStore.MaximumCrlBytes);
                CertificateAuthorityState state = _stateCodec
                    .DeserializeState(metadata);
                PeerPkiCacheSnapshot cache = _cacheCodec.Deserialize(
                    cacheBytes);
                PeerPkiSynchronizationStore.ValidateStandbySnapshot(
                    state,
                    cache,
                    caCertificate,
                    crl,
                    utcNow);
                return new CertificateAuthorityStandbyState(
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

        private void EnsurePromotionIsCurrent(
            CertificateAuthorityStandbyState current,
            CertificateAuthorityBackupState backup,
            CaBackupPayload payload,
            DateTime utcNow)
        {
            byte[] currentSpki = current.State.GetCaSpkiSha256();
            byte[] backupSpki = backup.State.GetCaSpkiSha256();
            try
            {
                if (current.State.SiteId != backup.State.SiteId
                    || current.State.CaSerialNumber
                        != backup.State.CaSerialNumber
                    || !BytesEqual(currentSpki, backupSpki)
                    || !BytesEqual(
                        current.CaCertificate,
                        payload.CaCertificateDer)
                    || backup.State.PkiRevision
                        < current.State.PkiRevision
                    || backup.State.CrlNumber
                        < current.State.CrlNumber)
                {
                    throw new InvalidDataException(
                        "The selected backup is not the same site CA or is older than the standby high-water state.");
                }

                if (backup.State.CrlNumber == current.State.CrlNumber
                    && !BytesEqual(current.Crl, payload.CrlDer))
                {
                    throw new InvalidDataException(
                        "The selected backup CRL differs at the observed CRL number.");
                }

                if (backup.State.PkiRevision
                    == current.State.PkiRevision)
                {
                    PeerPkiCacheSnapshot backupCache = CreateCache(
                        backup.State,
                        backup.Ledger,
                        payload.CrlDer,
                        utcNow);
                    byte[] currentCache = null;
                    byte[] backupCacheBytes = null;
                    try
                    {
                        currentCache = _cacheCodec.Serialize(current.Cache);
                        backupCacheBytes = _cacheCodec.Serialize(
                            backupCache);
                        if (!BytesEqual(currentCache, backupCacheBytes))
                        {
                            throw new InvalidDataException(
                                "The selected backup differs at the observed PKI revision.");
                        }
                    }
                    finally
                    {
                        Clear(currentCache);
                        Clear(backupCacheBytes);
                    }
                }
            }
            finally
            {
                Clear(currentSpki);
                Clear(backupSpki);
            }
        }

        private void EnsureExistingSiteMatchesBackup(
            CertificateAuthorityBackupState backup,
            CaBackupPayload payload,
            DateTime utcNow)
        {
            if (!_writer.Exists(StateFileTarget.PkiMetadata))
            {
                return;
            }

            byte[] metadata = null;
            byte[] caCertificate = null;
            byte[] currentSpki = null;
            byte[] backupSpki = null;
            try
            {
                metadata = ReadRequired(
                    StateFileTarget.PkiMetadata,
                    CertificateAuthorityStateCodec.MaximumDocumentBytes);
                CertificateAuthorityState current = _stateCodec
                    .DeserializeState(metadata);
                currentSpki = current.GetCaSpkiSha256();
                backupSpki = backup.State.GetCaSpkiSha256();
                if (current.SiteId != backup.State.SiteId
                    || current.CaSerialNumber
                        != backup.State.CaSerialNumber
                    || !BytesEqual(currentSpki, backupSpki))
                {
                    throw new InvalidDataException(
                        "The selected standby backup belongs to a different site CA.");
                }

                if (backup.State.PkiRevision < current.PkiRevision
                    || backup.State.CrlNumber < current.CrlNumber)
                {
                    throw new InvalidDataException(
                        "The selected standby backup is older than installed PKI high-water state.");
                }

                if (_writer.Exists(StateFileTarget.CaCertificate))
                {
                    caCertificate = ReadRequired(
                        StateFileTarget.CaCertificate,
                        CertificateAuthorityStore.MaximumCertificateBytes);
                    if (!BytesEqual(
                            caCertificate,
                            payload.CaCertificateDer))
                    {
                        throw new InvalidDataException(
                            "The selected standby backup CA certificate differs from installed state.");
                    }
                }

                if (current.Role == CertificateAuthorityRole.Standby)
                {
                    using (CertificateAuthorityStandbyState standby =
                        ReadAndValidateStandby(utcNow))
                    {
                        EnsurePromotionIsCurrent(
                            standby,
                            backup,
                            payload,
                            utcNow);
                    }
                }
                else
                {
                    EnsureEqualActiveHighWaterBytes(
                        current,
                        backup,
                        payload);
                }
            }
            finally
            {
                Clear(metadata);
                Clear(caCertificate);
                Clear(currentSpki);
                Clear(backupSpki);
            }
        }

        private static CertificateAuthorityState CreateStandbyState(
            CertificateAuthorityState backup)
        {
            return new CertificateAuthorityState(
                backup.SiteId,
                backup.IssuerInstanceId,
                CertificateAuthorityRole.Standby,
                backup.CaSerialNumber,
                backup.GetCaSpkiSha256(),
                backup.NotBeforeUtc,
                backup.NotAfterUtc,
                backup.PkiRevision,
                backup.CrlNumber,
                backup.LastBackupUtc);
        }

        private static CertificateAuthorityState CreatePromotedState(
            CertificateAuthorityState backup,
            Guid installedInstanceId,
            ulong promotedRevision,
            DateTime backupUtc)
        {
            return new CertificateAuthorityState(
                backup.SiteId,
                installedInstanceId,
                CertificateAuthorityRole.ActiveIssuer,
                backup.CaSerialNumber,
                backup.GetCaSpkiSha256(),
                backup.NotBeforeUtc,
                backup.NotAfterUtc,
                promotedRevision,
                backup.CrlNumber,
                backupUtc);
        }

        private static PeerPkiCacheSnapshot CreateCache(
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger,
            byte[] crl,
            DateTime utcNow)
        {
            var certificates = new List<PeerPkiCacheCertificate>();
            foreach (CertificateLedgerEntry entry in ledger.EntriesBySerial
                .Values
                .Where(value => value.Status
                    == CertificateLedgerStatus.Current)
                .OrderBy(value => value.ProductCode.Value,
                    StringComparer.Ordinal))
            {
                if (entry.NotAfterUtc <= utcNow)
                {
                    throw new InvalidDataException(
                        "The backup contains an expired CURRENT certificate.");
                }

                byte[] leafHash = entry.GetLeafCertificateSha256();
                try
                {
                    certificates.Add(new PeerPkiCacheCertificate(
                        entry.ProductCode,
                        entry.SerialNumber,
                        leafHash,
                        entry.NotAfterUtc));
                }
                finally
                {
                    Clear(leafHash);
                }
            }

            byte[] crlHash;
            using (SHA256 sha256 = SHA256.Create())
            {
                crlHash = sha256.ComputeHash(crl);
            }

            try
            {
                return new PeerPkiCacheSnapshot(
                    state.IssuerInstanceId,
                    state.PkiRevision,
                    state.CrlNumber,
                    crlHash,
                    certificates);
            }
            finally
            {
                Clear(crlHash);
            }
        }

        private static IssuedCertificateArtifact IssueDirectoryCertificate(
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger,
            CaBackupPayload payload,
            DirectoryEndpointIdentity identity,
            DateTime utcNow)
        {
            SiteCertificateAuthority authority =
                SiteCertificateAuthority.Restore(
                    state.SiteId,
                    payload.CaCertificateDer,
                    payload.PrivateKeyPkcs8,
                    utcNow);
            var random = new SecureRandom();
            PkiSerialNumber serial = PkiSerialNumber.CreateRandom(
                random,
                value => StringComparer.Ordinal.Equals(
                        value,
                        state.CaSerialNumber.Hex)
                    || ledger.EntriesBySerial.Keys.Any(existing =>
                        StringComparer.Ordinal.Equals(
                            existing.Hex,
                            value)));
            return authority.CreateDirectoryLeaf(
                identity,
                serial,
                utcNow,
                random);
        }

        private void ValidatePromotionDirectory(
            CertificateLedgerSnapshot ledger)
        {
            byte[] directoryBytes = null;
            try
            {
                directoryBytes = ReadRequired(
                    StateFileTarget.Directory,
                    RecoveryJournalManager.MaximumImageBytes);
                DirectorySnapshot directory = new StateXmlCodec()
                    .DeserializeSnapshot(directoryBytes);
                StorageV1ConsistencyValidator
                    .ValidateActiveDirectoryAndLedger(
                        directory,
                        ledger);
            }
            finally
            {
                Clear(directoryBytes);
            }
        }

        private void EnsureRequiredRoleFiles(CertificateAuthorityRole role)
        {
            bool active = role == CertificateAuthorityRole.ActiveIssuer;
            foreach (StateFileTarget target in RoleTargets)
            {
                bool required = target == StateFileTarget.PkiMetadata
                    || target == StateFileTarget.CertificateRevocationList
                    || target == StateFileTarget.CaCertificate
                    || (active
                        && (target == StateFileTarget.CertificateLedger
                            || target == StateFileTarget.CaPrivateKey))
                    || (!active
                        && target == StateFileTarget.PeerPkiCache);
                bool exists = _writer.Exists(target);
                if (exists != required)
                {
                    throw new InvalidDataException(
                        "Installed PKI files do not match the selected CA role.");
                }

                if (target == StateFileTarget.CaPrivateKey
                    && _writer.BackupExists(target))
                {
                    throw new InvalidDataException(
                        "ca.key.bak is forbidden for every CA role.");
                }

                if (!exists && _writer.BackupExists(target))
                {
                    throw new RecoveryRequiredException(
                        "A PKI backup file exists without its required primary state.",
                        null);
                }
            }
        }

        private byte[][] ReadBeforeImages()
        {
            var images = new byte[RoleTargets.Length][];
            try
            {
                for (int index = 0;
                    index < RoleTargets.Length;
                    index++)
                {
                    StateFileTarget target = RoleTargets[index];
                    if (!_writer.Exists(target))
                    {
                        if (_writer.BackupExists(target))
                        {
                            throw new RecoveryRequiredException(
                                "A PKI backup file exists without its primary state.",
                                null);
                        }

                        continue;
                    }

                    images[index] = _writer.Read(
                        target,
                        RecoveryJournalManager.MaximumImageBytes);
                }

                return images;
            }
            catch
            {
                Clear(images);
                throw;
            }
        }

        private static StateFileChange CreateChange(
            int index,
            byte[][] before,
            bool afterExists,
            byte[] afterBytes)
        {
            return new StateFileChange(
                RoleTargets[index],
                before[index] != null,
                before[index],
                afterExists,
                afterBytes);
        }

        private static void AddDeletionIfPresent(
            ICollection<StateFileChange> changes,
            int index,
            byte[][] before)
        {
            if (before[index] != null)
            {
                changes.Add(CreateChange(
                    index,
                    before,
                    false,
                    null));
            }
        }

        private byte[] ReadRequired(
            StateFileTarget target,
            int maximumBytes)
        {
            return _writer.Read(target, maximumBytes);
        }
    }
}
