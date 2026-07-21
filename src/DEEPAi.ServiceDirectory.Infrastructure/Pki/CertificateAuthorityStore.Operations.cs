using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed partial class CertificateAuthorityStore
    {
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
            out ulong trustRevision,
            out ulong pkiRevision,
            out ulong crlNumber)
        {
            EnsureUtc(createdUtc, nameof(createdUtc));
            ulong capturedPkiRevision = 0;
            ulong capturedCrlNumber = 0;
            ulong capturedTrustRevision = 0;
            CaBackupPayload payload = _mutationGate.Execute(() =>
            {
                lock (_lifecycleGate)
                {
                    ThrowIfAvailable();
                    byte[] privateKey = null;
                    byte[] otherPrivateKey = null;
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
                        capturedTrustRevision =
                            _current.State.TrustRevision;
                        if (_current.State.OtherAuthority != null)
                        {
                            otherPrivateKey = _secondaryProtector.Unprotect(
                                _current.OtherProtectedPrivateKey);
                            CertificateAuthorityState otherState =
                                CreateAuthorityValidationState(
                                    _current.State,
                                    _current.State.OtherAuthority);
                            ValidateAuthority(
                                otherState,
                                _current.OtherCaCertificateDer,
                                otherPrivateKey,
                                DateTime.UtcNow);
                        }

                        backupMetadata = _codec.SerializeState(
                            _current.State.WithLastBackupUtc(createdUtc));
                        return new CaBackupPayload(
                            backupMetadata,
                            _current.LedgerBytes,
                            _current.CaCertificateDer,
                            _current.CrlDer,
                            privateKey,
                            _current.OtherCaCertificateDer,
                            _current.OtherCrlDer,
                            otherPrivateKey);
                    }
                    finally
                    {
                        Clear(privateKey);
                        Clear(otherPrivateKey);
                        Clear(backupMetadata);
                    }
                }
            });
            trustRevision = capturedTrustRevision;
            pkiRevision = capturedPkiRevision;
            crlNumber = capturedCrlNumber;
            return payload;
        }

        internal bool MarkBackupCompleted(
            ulong expectedTrustRevision,
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
                    if (_current.State.TrustRevision
                            != expectedTrustRevision
                        || _current.State.PkiRevision != expectedPkiRevision
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
                payload.Ledger,
                state.CrlNumber);
            ValidateStateAndLedger(state, ledger);
            if (state.IssuerInstanceId != installedInstanceId)
            {
                throw new InvalidDataException(
                    "This repair backup belongs to another issuer instance.");
            }

            if ((state.OtherAuthority != null) != payload.HasOtherAuthority)
            {
                throw new InvalidDataException(
                    "CA backup slot components do not match rotation state.");
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
            byte[] otherProtectedKey = null;
            try
            {
                protectedKey = _protector.Protect(
                    payload.PrivateKeyPkcs8);
                if (payload.HasOtherAuthority)
                {
                    CertificateAuthorityState otherState =
                        CreateAuthorityValidationState(
                            state,
                            state.OtherAuthority);
                    ValidateAuthority(
                        otherState,
                        payload.OtherCaCertificateDer,
                        payload.OtherPrivateKeyPkcs8,
                        utcNow);
                    var emptyOtherLedger = new CertificateLedgerSnapshot(
                        new CertificateLedgerEntry[0],
                        state.PkiRevision,
                        state.OtherAuthority.CrlNumber);
                    ValidateCrl(
                        otherState,
                        emptyOtherLedger,
                        payload.OtherCaCertificateDer,
                        payload.OtherCrlDer);
                    otherProtectedKey = _secondaryProtector.Protect(
                        payload.OtherPrivateKeyPkcs8);
                }

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
                        byte[][] otherRepairBeforeImages = _current == null
                            ? ReadOtherSlotRepairBeforeImages()
                            : null;
                        try
                        {
                            CommitReplacementWithSlots(
                                _current,
                                repairBeforeImages,
                                otherRepairBeforeImages,
                                state,
                                ledger,
                                payload.CaCertificateDer,
                                payload.CrlDer,
                                protectedKey,
                                payload.OtherCaCertificateDer,
                                payload.OtherCrlDer,
                                otherProtectedKey);
                        }
                        finally
                        {
                            ClearImages(repairBeforeImages);
                            ClearImages(otherRepairBeforeImages);
                        }
                        _recoveryRequired = false;
                    }
                });
            }
            finally
            {
                Clear(protectedKey);
                Clear(otherProtectedKey);
            }
        }
    }
}
