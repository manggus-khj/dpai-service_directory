using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed class CertificateAuthorityStoreSnapshot : IDisposable
    {
        private bool _disposed;

        internal CertificateAuthorityStoreSnapshot(
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger,
            byte[] metadataBytes,
            byte[] ledgerBytes,
            byte[] caCertificateDer,
            byte[] crlDer,
            byte[] protectedPrivateKey)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            MetadataBytes = CloneRequired(metadataBytes, nameof(metadataBytes));
            LedgerBytes = CloneRequired(ledgerBytes, nameof(ledgerBytes));
            CaCertificateDer = CloneRequired(
                caCertificateDer,
                nameof(caCertificateDer));
            CrlDer = CloneRequired(crlDer, nameof(crlDer));
            ProtectedPrivateKey = CloneRequired(
                protectedPrivateKey,
                nameof(protectedPrivateKey));
        }

        internal CertificateAuthorityState State { get; }

        internal CertificateLedgerSnapshot Ledger { get; }

        internal byte[] MetadataBytes { get; private set; }

        internal byte[] LedgerBytes { get; private set; }

        internal byte[] CaCertificateDer { get; private set; }

        internal byte[] CrlDer { get; private set; }

        internal byte[] ProtectedPrivateKey { get; private set; }

        internal CertificateAuthorityStoreSnapshot Clone()
        {
            ThrowIfDisposed();
            return new CertificateAuthorityStoreSnapshot(
                State,
                Ledger,
                MetadataBytes,
                LedgerBytes,
                CaCertificateDer,
                CrlDer,
                ProtectedPrivateKey);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Clear(MetadataBytes);
            Clear(LedgerBytes);
            Clear(CaCertificateDer);
            Clear(CrlDer);
            Clear(ProtectedPrivateKey);
            MetadataBytes = null;
            LedgerBytes = null;
            CaCertificateDer = null;
            CrlDer = null;
            ProtectedPrivateKey = null;
            _disposed = true;
        }

        private static byte[] CloneRequired(byte[] value, string name)
        {
            if (value == null || value.Length == 0)
            {
                throw new ArgumentException(
                    "PKI state bytes must not be empty.",
                    name);
            }

            return (byte[])value.Clone();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(CertificateAuthorityStoreSnapshot));
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

    internal sealed class CertificateAuthorityStore : IDisposable
    {
        internal const int MaximumCertificateBytes = 128 * 1024;
        internal const int MaximumCrlBytes = 16 * 1024 * 1024;
        internal const int CrlValidityDays = 7;

        private readonly object _lifecycleGate = new object();
        private readonly StateMutationGate _mutationGate;
        private readonly StateStoragePathPolicy _pathPolicy;
        private readonly AtomicFileWriter _fileWriter;
        private readonly RecoveryJournalManager _journalManager;
        private readonly CertificateAuthorityStateCodec _codec;
        private readonly ICaPrivateKeyProtector _protector;
        private readonly IPeerSecretAccessPolicy _accessPolicy;
        private CertificateAuthorityStoreSnapshot _current;
        private bool _recoveryRequired;
        private bool _disposed;

        internal CertificateAuthorityStore(
            string stateDirectoryPath,
            StateMutationGate mutationGate)
            : this(
                new StateStoragePathPolicy(stateDirectoryPath),
                mutationGate,
                new DpapiMachineCaPrivateKeyProtector(),
                PeerSecretAccessPolicy.ForInstalledMainService(),
                NoOpRecoveryJournalFaultInjector.Instance)
        {
        }

        internal CertificateAuthorityStore(
            StateStoragePathPolicy pathPolicy,
            StateMutationGate mutationGate,
            ICaPrivateKeyProtector protector,
            IPeerSecretAccessPolicy accessPolicy,
            IRecoveryJournalFaultInjector faultInjector)
        {
            _pathPolicy = pathPolicy
                ?? throw new ArgumentNullException(nameof(pathPolicy));
            _mutationGate = mutationGate
                ?? throw new ArgumentNullException(nameof(mutationGate));
            _protector = protector
                ?? throw new ArgumentNullException(nameof(protector));
            if (accessPolicy == null)
            {
                throw new ArgumentNullException(nameof(accessPolicy));
            }

            _accessPolicy = accessPolicy;

            _fileWriter = new AtomicFileWriter(pathPolicy, accessPolicy);
            _journalManager = new RecoveryJournalManager(
                pathPolicy,
                _fileWriter,
                faultInjector ?? NoOpRecoveryJournalFaultInjector.Instance);
            _codec = new CertificateAuthorityStateCodec();
        }

        internal bool TryLoad()
        {
            return _mutationGate.Execute(() =>
            {
                lock (_lifecycleGate)
                {
                    ThrowIfDisposed();
                    DisposeCurrent();
                    try
                    {
                        _journalManager.Recover(
                            ValidateRecoveryTargets,
                            ValidateAllInstalledState);
                        _current = ReadCurrent(true);
                        return _current != null;
                    }
                    catch
                    {
                        _recoveryRequired = true;
                        throw;
                    }
                }
            });
        }

        internal CertificateAuthorityStoreSnapshot GetCurrent()
        {
            lock (_lifecycleGate)
            {
                ThrowIfAvailable();
                return _current.Clone();
            }
        }

        internal void Provision(
            Guid instanceId,
            DateTime utcNow)
        {
            if (instanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Instance ID must not be empty.",
                    nameof(instanceId));
            }

            EnsureUtc(utcNow, nameof(utcNow));
            _mutationGate.Execute(() =>
            {
                lock (_lifecycleGate)
                {
                    ThrowIfDisposed();
                    if (_recoveryRequired || _current != null)
                    {
                        throw new InvalidOperationException(
                            "PKI state is already provisioned or requires recovery.");
                    }

                    EnsurePkiDirectories();
                    if (AnyTargetExists())
                    {
                        throw new InvalidDataException(
                            "Partial PKI state already exists.");
                    }

                    var random = new SecureRandom();
                    SiteCertificateAuthority authority =
                        SiteCertificateAuthority.Create(
                            Guid.NewGuid(),
                            utcNow,
                            random);
                    byte[] privateKey = null;
                    byte[] protectedKey = null;
                    byte[] certificateDer = null;
                    byte[] crlDer = null;
                    try
                    {
                        privateKey = authority.ExportPrivateKeyPkcs8();
                        protectedKey = _protector.Protect(privateKey);
                        certificateDer = authority.GetCertificateDer();
                        var ledger = new CertificateLedgerSnapshot(
                            new CertificateLedgerEntry[0],
                            1,
                            1);
                        DateTime nextUpdateUtc = GetNextCrlUpdate(
                            utcNow,
                            authority.NotAfterUtc);
                        CertificateRevocationListArtifact crl =
                            authority.CreateRevocationList(
                                1,
                                new RevokedCertificateEntry[0],
                                utcNow,
                                nextUpdateUtc,
                                random);
                        crlDer = crl.GetDerBytes();
                        var state = new CertificateAuthorityState(
                            authority.SiteId,
                            instanceId,
                            CertificateAuthorityRole.ActiveIssuer,
                            authority.SerialNumber,
                            authority.GetSpkiSha256(),
                            authority.NotBeforeUtc,
                            authority.NotAfterUtc,
                            ledger.PkiRevision,
                            ledger.CrlNumber,
                            null);
                        CommitReplacement(
                            null,
                            state,
                            ledger,
                            certificateDer,
                            crlDer,
                            protectedKey);
                    }
                    finally
                    {
                        Clear(privateKey);
                        Clear(protectedKey);
                        Clear(certificateDer);
                        Clear(crlDer);
                    }
                }
            });
        }

        internal CaBackupPayload CaptureBackupPayload(
            DateTime createdUtc,
            out ulong pkiRevision,
            out ulong crlNumber)
        {
            EnsureUtc(createdUtc, nameof(createdUtc));
            ulong capturedPkiRevision = 0;
            ulong capturedCrlNumber = 0;
            CaBackupPayload payload = _mutationGate.Execute(() =>
            {
                lock (_lifecycleGate)
                {
                    ThrowIfAvailable();
                    byte[] privateKey = null;
                    byte[] backupMetadata = null;
                    try
                    {
                        privateKey = _protector.Unprotect(
                            _current.ProtectedPrivateKey);
                        ValidateAuthority(
                            _current.State,
                            _current.CaCertificateDer,
                            privateKey,
                            DateTime.UtcNow);
                        capturedPkiRevision = _current.State.PkiRevision;
                        capturedCrlNumber = _current.State.CrlNumber;
                        backupMetadata = _codec.SerializeState(
                            _current.State.WithLastBackupUtc(createdUtc));
                        return new CaBackupPayload(
                            backupMetadata,
                            _current.LedgerBytes,
                            _current.CaCertificateDer,
                            _current.CrlDer,
                            privateKey);
                    }
                    finally
                    {
                        Clear(privateKey);
                        Clear(backupMetadata);
                    }
                }
            });
            pkiRevision = capturedPkiRevision;
            crlNumber = capturedCrlNumber;
            return payload;
        }

        internal bool MarkBackupCompleted(
            ulong expectedPkiRevision,
            ulong expectedCrlNumber,
            DateTime createdUtc)
        {
            EnsureUtc(createdUtc, nameof(createdUtc));
            return _mutationGate.Execute(() =>
            {
                lock (_lifecycleGate)
                {
                    ThrowIfAvailable();
                    if (_current.State.PkiRevision != expectedPkiRevision
                        || _current.State.CrlNumber != expectedCrlNumber)
                    {
                        return false;
                    }

                    CertificateAuthorityState nextState =
                        _current.State.WithLastBackupUtc(createdUtc);
                    byte[] metadata = _codec.SerializeState(nextState);
                    try
                    {
                        var changes = new List<StateFileChange>
                        {
                            new StateFileChange(
                                StateFileTarget.PkiMetadata,
                                true,
                                _current.MetadataBytes,
                                true,
                                metadata)
                        };
                        CertificateAuthorityStoreSnapshot applied = null;
                        _journalManager.Commit(
                            changes.AsReadOnly(),
                            () => applied = ReadCurrent(false));
                        ReplaceCurrent(applied);
                        return true;
                    }
                    catch (RecoveryRequiredException)
                    {
                        _recoveryRequired = true;
                        throw;
                    }
                    finally
                    {
                        Clear(metadata);
                    }
                }
            });
        }

        internal CertificateAuthorityStoreSnapshot Revoke(
            CertificateSerialNumber serialNumber,
            CertificateRevocationReason reason,
            DateTime revokedUtc,
            out bool alreadyRevoked)
        {
            if (!serialNumber.IsValid)
            {
                throw new ArgumentException(
                    "Certificate serial must be valid.",
                    nameof(serialNumber));
            }

            if (!IsOperatorReason(reason))
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }

            EnsureUtc(revokedUtc, nameof(revokedUtc));
            bool localAlreadyRevoked = false;
            CertificateAuthorityStoreSnapshot result =
                _mutationGate.Execute(() =>
                {
                    lock (_lifecycleGate)
                    {
                        ThrowIfAvailable();
                        if (_current.State.Role
                                != CertificateAuthorityRole.ActiveIssuer
                            || !_current.State.LastBackupUtc.HasValue)
                        {
                            throw new InvalidOperationException(
                                "The CA is not ready for revocation.");
                        }

                        CertificateLedgerEntry existing;
                        if (!_current.Ledger.TryGetBySerial(
                                serialNumber,
                                out existing))
                        {
                            throw new KeyNotFoundException(
                                "Certificate serial was not found.");
                        }

                        if (existing.Status
                            == CertificateLedgerStatus.Revoked)
                        {
                            if (existing.RevocationReason != reason)
                            {
                                throw new InvalidOperationException(
                                    "Certificate is already revoked for a different reason.");
                            }

                            localAlreadyRevoked = true;
                            return _current.Clone();
                        }

                        if (_current.State.PkiRevision == ulong.MaxValue
                            || _current.State.CrlNumber == ulong.MaxValue)
                        {
                            throw new OverflowException(
                                "PKI high-water value is exhausted.");
                        }

                        var entries = _current.Ledger
                            .EntriesBySerial
                            .Values
                            .Select(entry => entry.SerialNumber == serialNumber
                                ? entry.Revoke(revokedUtc, reason)
                                : entry)
                            .ToArray();
                        ulong nextPkiRevision =
                            _current.State.PkiRevision + 1;
                        ulong nextCrlNumber =
                            _current.State.CrlNumber + 1;
                        var nextLedger = new CertificateLedgerSnapshot(
                            entries,
                            nextPkiRevision,
                            nextCrlNumber);
                        CertificateAuthorityState nextState =
                            _current.State.WithHighWater(
                                nextPkiRevision,
                                nextCrlNumber);

                        byte[] privateKey = null;
                        byte[] crlDer = null;
                        try
                        {
                            privateKey = _protector.Unprotect(
                                _current.ProtectedPrivateKey);
                            SiteCertificateAuthority authority =
                                SiteCertificateAuthority.Restore(
                                    nextState.SiteId,
                                    _current.CaCertificateDer,
                                    privateKey,
                                    revokedUtc);
                            DateTime nextUpdateUtc = GetNextCrlUpdate(
                                revokedUtc,
                                authority.NotAfterUtc);
                            CertificateRevocationListArtifact crl =
                                authority.CreateRevocationList(
                                    nextCrlNumber,
                                    CreateRevokedEntries(nextLedger),
                                    revokedUtc,
                                    nextUpdateUtc,
                                    new SecureRandom());
                            crlDer = crl.GetDerBytes();
                            CommitReplacement(
                                _current,
                                nextState,
                                nextLedger,
                                _current.CaCertificateDer,
                                crlDer,
                                _current.ProtectedPrivateKey);
                            return _current.Clone();
                        }
                        catch (RecoveryRequiredException)
                        {
                            _recoveryRequired = true;
                            throw;
                        }
                        finally
                        {
                            Clear(privateKey);
                            Clear(crlDer);
                        }
                    }
                });
            alreadyRevoked = localAlreadyRevoked;
            return result;
        }

        internal void Restore(
            CaBackupPayload payload,
            Guid installedInstanceId,
            DateTime utcNow)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (installedInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Installed instance ID must not be empty.",
                    nameof(installedInstanceId));
            }

            EnsureUtc(utcNow, nameof(utcNow));
            CertificateAuthorityState state = _codec.DeserializeState(
                payload.Metadata);
            CertificateLedgerSnapshot ledger = _codec.DeserializeLedger(
                payload.Ledger);
            ValidateStateAndLedger(state, ledger);
            if (state.IssuerInstanceId != installedInstanceId)
            {
                throw new InvalidDataException(
                    "This repair backup belongs to another issuer instance.");
            }

            ValidateAuthority(
                state,
                payload.CaCertificateDer,
                payload.PrivateKeyPkcs8,
                utcNow);
            ValidateCrl(
                state,
                ledger,
                payload.CaCertificateDer,
                payload.CrlDer);
            byte[] protectedKey = null;
            try
            {
                protectedKey = _protector.Protect(
                    payload.PrivateKeyPkcs8);
                _mutationGate.Execute(() =>
                {
                    lock (_lifecycleGate)
                    {
                        ThrowIfDisposed();
                        if (_current != null
                            && (state.SiteId != _current.State.SiteId
                                || state.PkiRevision
                                    < _current.State.PkiRevision
                                || state.CrlNumber
                                    < _current.State.CrlNumber))
                        {
                            throw new InvalidDataException(
                                "CA repair backup would replace the site or lower a high-water value.");
                        }

                        EnsurePkiDirectories();
                        byte[][] repairBeforeImages = _current == null
                            ? ReadRepairBeforeImages()
                            : null;
                        try
                        {
                            CommitReplacement(
                                _current,
                                repairBeforeImages,
                                state,
                                ledger,
                                payload.CaCertificateDer,
                                payload.CrlDer,
                                protectedKey);
                        }
                        finally
                        {
                            ClearImages(repairBeforeImages);
                        }
                        _recoveryRequired = false;
                    }
                });
            }
            finally
            {
                Clear(protectedKey);
            }
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

                    DisposeCurrent();
                    _disposed = true;
                }
            });
        }

        private void CommitReplacement(
            CertificateAuthorityStoreSnapshot current,
            CertificateAuthorityState nextState,
            CertificateLedgerSnapshot nextLedger,
            byte[] caCertificateDer,
            byte[] crlDer,
            byte[] protectedPrivateKey)
        {
            CommitReplacement(
                current,
                null,
                nextState,
                nextLedger,
                caCertificateDer,
                crlDer,
                protectedPrivateKey);
        }

        private void CommitReplacement(
            CertificateAuthorityStoreSnapshot current,
            byte[][] repairBeforeImages,
            CertificateAuthorityState nextState,
            CertificateLedgerSnapshot nextLedger,
            byte[] caCertificateDer,
            byte[] crlDer,
            byte[] protectedPrivateKey)
        {
            ValidateStateAndLedger(nextState, nextLedger);
            byte[] metadata = _codec.SerializeState(nextState);
            byte[] ledger = _codec.SerializeLedger(nextLedger);
            try
            {
                IReadOnlyList<StateFileChange> changes = BuildChanges(
                    current,
                    repairBeforeImages,
                    metadata,
                    ledger,
                    caCertificateDer,
                    crlDer,
                    protectedPrivateKey);
                CertificateAuthorityStoreSnapshot applied = null;
                _journalManager.Commit(
                    changes,
                    () => applied = ReadCurrent(false));
                ReplaceCurrent(applied);
            }
            finally
            {
                Clear(metadata);
                Clear(ledger);
            }
        }

        private CertificateAuthorityStoreSnapshot ReadCurrent(
            bool allowMissing)
        {
            string pkiDirectory = Path.Combine(
                _pathPolicy.StateDirectoryPath,
                "pki");
            if (!Directory.Exists(pkiDirectory))
            {
                string caKeyPath = _pathPolicy.GetTargetPath(
                    StateFileTarget.CaPrivateKey);
                _pathPolicy.EnsureExistingFileIsSafe(caKeyPath);
                _pathPolicy.EnsureExistingFileIsSafe(caKeyPath + ".bak");
                if (File.Exists(caKeyPath)
                    || File.Exists(caKeyPath + ".bak")
                    || HasCaBackupArtifacts())
                {
                    throw new InvalidDataException(
                        "CA artifacts exist without the PKI state directory.");
                }

                if (allowMissing)
                {
                    return null;
                }

                throw new FileNotFoundException(
                    "Provisioned PKI state is missing.");
            }

            _pathPolicy.EnsureDirectoryIsSafe(pkiDirectory);
            StateFileTarget[] targets = GetPkiTargets();
            bool[] exists = targets.Select(_fileWriter.Exists).ToArray();
            int existingCount = exists.Count(value => value);
            if (existingCount == 0)
            {
                if (targets.Any(_fileWriter.BackupExists)
                    || HasCaBackupArtifacts())
                {
                    throw new InvalidDataException(
                        "PKI backup artifacts exist without primary PKI state.");
                }

                if (!allowMissing)
                {
                    throw new FileNotFoundException(
                        "Provisioned PKI state is missing.");
                }

                return null;
            }

            if (existingCount != targets.Length)
            {
                throw new InvalidDataException(
                    "Provisioned PKI state is incomplete.");
            }

            if (_fileWriter.BackupExists(StateFileTarget.CaPrivateKey))
            {
                throw new InvalidDataException(
                    "ca.key.bak is forbidden.");
            }

            byte[] metadata = null;
            byte[] ledgerBytes = null;
            byte[] certificate = null;
            byte[] crl = null;
            byte[] protectedKey = null;
            byte[] privateKey = null;
            try
            {
                metadata = _fileWriter.Read(
                    StateFileTarget.PkiMetadata,
                    CertificateAuthorityStateCodec.MaximumDocumentBytes);
                ledgerBytes = _fileWriter.Read(
                    StateFileTarget.CertificateLedger,
                    CertificateAuthorityStateCodec.MaximumDocumentBytes);
                certificate = _fileWriter.Read(
                    StateFileTarget.CaCertificate,
                    MaximumCertificateBytes);
                crl = _fileWriter.Read(
                    StateFileTarget.CertificateRevocationList,
                    MaximumCrlBytes);
                protectedKey = _fileWriter.Read(
                    StateFileTarget.CaPrivateKey,
                    DpapiMachineCaPrivateKeyProtector.MaximumProtectedBytes);
                _accessPolicy.ValidateExistingFile(
                    _pathPolicy.GetTargetPath(
                        StateFileTarget.CaPrivateKey));
                privateKey = _protector.Unprotect(protectedKey);

                CertificateAuthorityState state =
                    _codec.DeserializeState(metadata);
                CertificateLedgerSnapshot ledger =
                    _codec.DeserializeLedger(ledgerBytes);
                ValidateStateAndLedger(state, ledger);
                ValidateAuthority(
                    state,
                    certificate,
                    privateKey,
                    DateTime.UtcNow);
                ValidateCrl(state, ledger, certificate, crl);
                return new CertificateAuthorityStoreSnapshot(
                    state,
                    ledger,
                    metadata,
                    ledgerBytes,
                    certificate,
                    crl,
                    protectedKey);
            }
            finally
            {
                Clear(metadata);
                Clear(ledgerBytes);
                Clear(certificate);
                Clear(crl);
                Clear(protectedKey);
                Clear(privateKey);
            }
        }

        private static void ValidateAuthority(
            CertificateAuthorityState state,
            byte[] certificateDer,
            byte[] privateKeyPkcs8,
            DateTime utcNow)
        {
            SiteCertificateAuthority authority =
                SiteCertificateAuthority.Restore(
                    state.SiteId,
                    certificateDer,
                    privateKeyPkcs8,
                    utcNow);
            byte[] spki = authority.GetSpkiSha256();
            byte[] expectedSpki = state.GetCaSpkiSha256();
            try
            {
                if (authority.SerialNumber != state.CaSerialNumber
                    || authority.NotBeforeUtc != state.NotBeforeUtc
                    || authority.NotAfterUtc != state.NotAfterUtc
                    || !FixedTimeEquals(spki, expectedSpki))
                {
                    throw new InvalidDataException(
                        "CA certificate does not match its persisted metadata.");
                }
            }
            finally
            {
                Clear(spki);
                Clear(expectedSpki);
            }
        }

        private static void ValidateCrl(
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger,
            byte[] caCertificateDer,
            byte[] crlDer)
        {
            var certificate = new X509Certificate(caCertificateDer);
            var parser = new X509CrlParser();
            X509Crl crl;
            try
            {
                crl = parser.ReadCrl(crlDer);
                crl.Verify(certificate.GetPublicKey());
            }
            catch (Exception exception)
                when (exception is GeneralSecurityException
                    || exception is IOException
                    || exception is ArgumentException)
            {
                throw new InvalidDataException(
                    "Signed CRL validation failed.",
                    exception);
            }

            if (!crl.IssuerDN.Equivalent(certificate.SubjectDN))
            {
                throw new InvalidDataException(
                    "CRL issuer does not match the site CA.");
            }

            DateTime thisUpdateUtc = crl.ThisUpdate.ToUniversalTime();
            DateTime? nextUpdateUtc = crl.NextUpdate.HasValue
                ? crl.NextUpdate.Value.ToUniversalTime()
                : (DateTime?)null;
            if (thisUpdateUtc < state.NotBeforeUtc
                || thisUpdateUtc > state.NotAfterUtc
                || !nextUpdateUtc.HasValue
                || nextUpdateUtc.Value <= thisUpdateUtc
                || nextUpdateUtc.Value > state.NotAfterUtc)
            {
                throw new InvalidDataException(
                    "CRL validity is outside the site CA validity period.");
            }

            Asn1OctetString extension = crl.GetExtensionValue(
                X509Extensions.CrlNumber);
            if (extension == null)
            {
                throw new InvalidDataException("CRL number is missing.");
            }

            CrlNumber number = CrlNumber.GetInstance(
                Asn1Object.FromByteArray(extension.GetOctets()));
            if (number.PositiveValue.SignValue <= 0
                || !ulong.TryParse(
                    number.PositiveValue.ToString(),
                    out ulong parsedNumber)
                || parsedNumber != state.CrlNumber
                || parsedNumber != ledger.CrlNumber)
            {
                throw new InvalidDataException(
                    "CRL number does not match persisted high-water state.");
            }

            ISet<X509CrlEntry> revokedCertificates =
                crl.GetRevokedCertificates();
            int expectedRevokedCount = ledger.EntriesBySerial.Values.Count(
                entry => entry.Status == CertificateLedgerStatus.Revoked);
            int actualRevokedCount = revokedCertificates == null
                ? 0
                : revokedCertificates.Count;
            if (actualRevokedCount != expectedRevokedCount)
            {
                throw new InvalidDataException(
                    "CRL contains entries that do not match the certificate ledger.");
            }

            foreach (CertificateLedgerEntry entry in ledger
                .EntriesBySerial.Values)
            {
                PkiSerialNumber serial;
                if (!PkiSerialNumber.TryParse(
                        entry.SerialNumber.Hex,
                        out serial))
                {
                    throw new InvalidDataException(
                        "Ledger contains a non-canonical serial.");
                }

                X509CrlEntry revokedEntry =
                    crl.GetRevokedCertificate(serial.Value);
                bool listed = revokedEntry != null;
                if (listed
                    != (entry.Status == CertificateLedgerStatus.Revoked))
                {
                    throw new InvalidDataException(
                        "CRL membership does not match the certificate ledger.");
                }

                if (!listed)
                {
                    continue;
                }

                if (!entry.RevokedUtc.HasValue
                    || !entry.RevocationReason.HasValue
                    || !AreEqualToAsn1Second(
                        revokedEntry.RevocationDate,
                        entry.RevokedUtc.Value))
                {
                    throw new InvalidDataException(
                        "CRL revocation time does not match the certificate ledger.");
                }

                Asn1OctetString reasonExtension =
                    revokedEntry.GetExtensionValue(X509Extensions.ReasonCode);
                if (reasonExtension == null)
                {
                    throw new InvalidDataException(
                        "CRL revocation reason is missing.");
                }

                DerEnumerated reason;
                try
                {
                    reason = DerEnumerated.GetInstance(
                        Asn1Object.FromByteArray(
                            reasonExtension.GetOctets()));
                }
                catch (Exception exception)
                    when (exception is ArgumentException
                        || exception is IOException)
                {
                    throw new InvalidDataException(
                        "CRL revocation reason is invalid.",
                        exception);
                }

                if (reason.IntValueExact
                    != (int)entry.RevocationReason.Value)
                {
                    throw new InvalidDataException(
                        "CRL revocation reason does not match the certificate ledger.");
                }
            }
        }

        private static bool AreEqualToAsn1Second(
            DateTime left,
            DateTime right)
        {
            long leftTicks = left.ToUniversalTime().Ticks
                / TimeSpan.TicksPerSecond;
            long rightTicks = right.ToUniversalTime().Ticks
                / TimeSpan.TicksPerSecond;
            return leftTicks == rightTicks;
        }

        private static void ValidateStateAndLedger(
            CertificateAuthorityState state,
            CertificateLedgerSnapshot ledger)
        {
            if (state.PkiRevision != ledger.PkiRevision
                || state.CrlNumber != ledger.CrlNumber)
            {
                throw new InvalidDataException(
                    "CA metadata and certificate ledger high-water values differ.");
            }
        }

        private void ValidateAllInstalledState()
        {
            PeerConfigurationTransactionStore
                .ValidateInstalledNonPkiStateFiles(
                    _pathPolicy,
                    _accessPolicy);
            using (CertificateAuthorityStoreSnapshot state =
                ReadCurrent(true))
            {
            }
        }

        internal static void ValidateInstalledStateFiles(
            StateStoragePathPolicy pathPolicy,
            IPeerSecretAccessPolicy accessPolicy)
        {
            var store = new CertificateAuthorityStore(
                pathPolicy,
                new StateMutationGate(),
                new DpapiMachineCaPrivateKeyProtector(),
                accessPolicy,
                NoOpRecoveryJournalFaultInjector.Instance);
            try
            {
                using (CertificateAuthorityStoreSnapshot state =
                    store.ReadCurrent(true))
                {
                }
            }
            finally
            {
                store.Dispose();
            }
        }

        private static void ValidateRecoveryTargets(
            IReadOnlyList<StateFileTarget> targets)
        {
            foreach (StateFileTarget target in targets)
            {
                StateFileTargets.Get(target);
            }
        }

        private static IReadOnlyList<StateFileChange> BuildChanges(
            CertificateAuthorityStoreSnapshot current,
            byte[][] repairBeforeImages,
            byte[] metadata,
            byte[] ledger,
            byte[] certificate,
            byte[] crl,
            byte[] protectedKey)
        {
            byte[][] next =
            {
                metadata,
                ledger,
                crl,
                certificate,
                protectedKey
            };
            StateFileTarget[] targets = GetPkiTargets();
            byte[][] previous = current != null
                ? new[]
                {
                    current.MetadataBytes,
                    current.LedgerBytes,
                    current.CrlDer,
                    current.CaCertificateDer,
                    current.ProtectedPrivateKey
                }
                : repairBeforeImages ?? new byte[targets.Length][];
            if (previous.Length != targets.Length)
            {
                throw new ArgumentException(
                    "Repair before images do not match the PKI targets.",
                    nameof(repairBeforeImages));
            }
            var changes = new List<StateFileChange>(targets.Length);
            for (int index = 0; index < targets.Length; index++)
            {
                if (!ByteArraysEqual(previous[index], next[index]))
                {
                    changes.Add(new StateFileChange(
                        targets[index],
                        previous[index] != null,
                        previous[index],
                        true,
                        next[index]));
                }
            }

            if (changes.Count == 0)
            {
                throw new InvalidOperationException(
                    "PKI replacement contains no state change.");
            }

            return changes.AsReadOnly();
        }

        private byte[][] ReadRepairBeforeImages()
        {
            StateFileTarget[] targets = GetPkiTargets();
            int[] maximumBytes =
            {
                CertificateAuthorityStateCodec.MaximumDocumentBytes,
                CertificateAuthorityStateCodec.MaximumDocumentBytes,
                MaximumCrlBytes,
                MaximumCertificateBytes,
                DpapiMachineCaPrivateKeyProtector.MaximumProtectedBytes
            };
            var images = new byte[targets.Length][];
            try
            {
                for (int index = 0; index < targets.Length; index++)
                {
                    if (!_fileWriter.Exists(targets[index]))
                    {
                        continue;
                    }

                    // Repair may be required precisely because the secret
                    // file's DACL is no longer canonical.  The elevated,
                    // offline repair process captures any safely addressed
                    // readable bytes as the journal before-image, then the
                    // normal secret writer restores the exact DACL on the
                    // replacement.
                    images[index] = _fileWriter.Read(
                        targets[index],
                        maximumBytes[index]);
                }

                return images;
            }
            catch
            {
                ClearImages(images);
                throw;
            }
        }

        private bool HasCaBackupArtifacts()
        {
            string backupDirectory = Path.Combine(
                _pathPolicy.StateDirectoryPath,
                "backups",
                "ca");
            if (!Directory.Exists(backupDirectory))
            {
                return false;
            }

            _pathPolicy.EnsureDirectoryIsSafe(backupDirectory);
            return Directory.EnumerateFiles(
                    backupDirectory,
                    "site-ca-*.dpca",
                    SearchOption.TopDirectoryOnly)
                .Any();
        }

        private static void ClearImages(byte[][] images)
        {
            if (images == null)
            {
                return;
            }

            for (int index = 0; index < images.Length; index++)
            {
                Clear(images[index]);
            }
        }

        private static IEnumerable<RevokedCertificateEntry>
            CreateRevokedEntries(CertificateLedgerSnapshot ledger)
        {
            foreach (CertificateLedgerEntry entry in ledger
                .EntriesBySerial
                .Values
                .Where(item =>
                    item.Status == CertificateLedgerStatus.Revoked))
            {
                PkiSerialNumber serial;
                if (!PkiSerialNumber.TryParse(
                        entry.SerialNumber.Hex,
                        out serial)
                    || !entry.RevokedUtc.HasValue
                    || !entry.RevocationReason.HasValue)
                {
                    throw new InvalidDataException(
                        "Revoked ledger entry is incomplete.");
                }

                yield return new RevokedCertificateEntry(
                    serial,
                    entry.RevokedUtc.Value,
                    entry.RevocationReason.Value);
            }
        }

        private static bool IsOperatorReason(
            CertificateRevocationReason reason)
        {
            return reason == CertificateRevocationReason.KeyCompromise
                || reason == CertificateRevocationReason.CaCompromise
                || reason == CertificateRevocationReason.AffiliationChanged
                || reason == CertificateRevocationReason.PrivilegeWithdrawn
                || reason == CertificateRevocationReason.AaCompromise;
        }

        private void EnsurePkiDirectories()
        {
            string pkiDirectory = Path.Combine(
                _pathPolicy.StateDirectoryPath,
                "pki");
            string secretsDirectory = Path.Combine(
                _pathPolicy.StateDirectoryPath,
                "secrets");
            Directory.CreateDirectory(pkiDirectory);
            Directory.CreateDirectory(secretsDirectory);
            _pathPolicy.EnsureDirectoryIsSafe(pkiDirectory);
            _pathPolicy.EnsureDirectoryIsSafe(secretsDirectory);
        }

        private bool AnyTargetExists()
        {
            return GetPkiTargets().Any(_fileWriter.Exists);
        }

        private static StateFileTarget[] GetPkiTargets()
        {
            return new[]
            {
                StateFileTarget.PkiMetadata,
                StateFileTarget.CertificateLedger,
                StateFileTarget.CertificateRevocationList,
                StateFileTarget.CaCertificate,
                StateFileTarget.CaPrivateKey
            };
        }

        private static DateTime GetNextCrlUpdate(
            DateTime thisUpdateUtc,
            DateTime caNotAfterUtc)
        {
            DateTime candidate = thisUpdateUtc.AddDays(CrlValidityDays);
            if (candidate > caNotAfterUtc)
            {
                candidate = caNotAfterUtc;
            }

            if (candidate <= thisUpdateUtc)
            {
                throw new InvalidOperationException(
                    "The site CA expires before a new CRL can be published.");
            }

            return candidate;
        }

        private void ReplaceCurrent(
            CertificateAuthorityStoreSnapshot next)
        {
            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }

            CertificateAuthorityStoreSnapshot previous = _current;
            _current = next;
            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private void DisposeCurrent()
        {
            if (_current != null)
            {
                _current.Dispose();
                _current = null;
            }
        }

        private void ThrowIfAvailable()
        {
            ThrowIfDisposed();
            if (_recoveryRequired)
            {
                throw new RecoveryRequiredException(
                    "PKI state requires repair recovery.",
                    null);
            }

            if (_current == null)
            {
                throw new InvalidOperationException(
                    "PKI state is not provisioned.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(CertificateAuthorityStore));
            }
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

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

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            return ByteArraysEqual(left, right);
        }

        private static void EnsureUtc(DateTime value, string parameterName)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "PKI timestamps must use DateTimeKind.Utc.",
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
