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
        private void CommitReplacementWithSlots(
            CertificateAuthorityStoreSnapshot current,
            byte[][] repairBeforeImages,
            byte[][] otherRepairBeforeImages,
            CertificateAuthorityState nextState,
            CertificateLedgerSnapshot nextLedger,
            byte[] caCertificateDer,
            byte[] crlDer,
            byte[] protectedPrivateKey,
            byte[] otherCaCertificateDer,
            byte[] otherCrlDer,
            byte[] otherProtectedPrivateKey)
        {
            ValidateStateAndLedger(nextState, nextLedger);
            bool hasOther = otherCaCertificateDer != null
                || otherCrlDer != null
                || otherProtectedPrivateKey != null;
            if (hasOther
                != (otherCaCertificateDer != null
                    && otherCrlDer != null
                    && otherProtectedPrivateKey != null)
                || hasOther != (nextState.OtherAuthority != null))
            {
                throw new ArgumentException(
                    "Secondary CA artifacts do not match rotation state.");
            }

            byte[] metadata = _codec.SerializeState(nextState);
            byte[] ledger = _codec.SerializeLedger(nextLedger);
            try
            {
                var changes = new List<StateFileChange>(BuildChanges(
                    current,
                    repairBeforeImages,
                    metadata,
                    ledger,
                    caCertificateDer,
                    crlDer,
                    protectedPrivateKey));
                byte[][] previous = current == null
                    ? otherRepairBeforeImages ?? new byte[3][]
                    : new[]
                    {
                        current.OtherCrlDer,
                        current.OtherCaCertificateDer,
                        current.OtherProtectedPrivateKey
                    };
                if (previous.Length != 3)
                {
                    throw new ArgumentException(
                        "Secondary repair before images are invalid.",
                        nameof(otherRepairBeforeImages));
                }

                StateFileTarget[] targets =
                {
                    StateFileTarget.CertificateRevocationListB,
                    StateFileTarget.CaCertificateB,
                    StateFileTarget.CaPrivateKeyB
                };
                byte[][] after =
                {
                    otherCrlDer,
                    otherCaCertificateDer,
                    otherProtectedPrivateKey
                };
                for (int index = 0; index < targets.Length; index++)
                {
                    if (!ByteArraysEqual(previous[index], after[index]))
                    {
                        changes.Add(new StateFileChange(
                            targets[index],
                            previous[index] != null,
                            previous[index],
                            after[index] != null,
                            after[index]));
                    }
                }

                changes.Sort((left, right) =>
                    ((int)left.Target).CompareTo((int)right.Target));
                CertificateAuthorityStoreSnapshot applied = null;
                _journalManager.Commit(
                    changes.AsReadOnly(),
                    () => applied = ReadCurrent(false));
                ReplaceCurrent(applied);
            }
            finally
            {
                Clear(metadata);
                Clear(ledger);
            }
        }

        private byte[][] ReadOtherSlotRepairBeforeImages()
        {
            StateFileTarget[] targets =
            {
                StateFileTarget.CertificateRevocationListB,
                StateFileTarget.CaCertificateB,
                StateFileTarget.CaPrivateKeyB
            };
            int[] limits =
            {
                MaximumCrlBytes,
                MaximumCertificateBytes,
                DpapiMachineCaPrivateKeyProtector.MaximumProtectedBytes
            };
            var images = new byte[targets.Length][];
            try
            {
                for (int index = 0; index < targets.Length; index++)
                {
                    if (_fileWriter.Exists(targets[index]))
                    {
                        images[index] = _fileWriter.Read(
                            targets[index],
                            limits[index]);
                    }
                }

                return images;
            }
            catch
            {
                ClearImages(images);
                throw;
            }
        }

        internal CertificateAuthorityStoreSnapshot PrepareRotation(
            DateTime utcNow)
        {
            EnsureUtc(utcNow, nameof(utcNow));
            return _mutationGate.Execute(() =>
            {
                lock (_lifecycleGate)
                {
                    ThrowIfAvailable();
                    CertificateAuthorityState currentState = _current.State;
                    if (currentState.Role
                            != CertificateAuthorityRole.ActiveIssuer
                        || currentState.RotationPhase
                            != CertificateAuthorityRotationPhase.Stable
                        || !currentState.IsCurrentRevisionBackedUp)
                    {
                        throw new InvalidOperationException(
                            "The CA is not ready to prepare a rotation.");
                    }

                    if (_fileWriter.Exists(
                            StateFileTarget.CertificateRevocationListB)
                        || _fileWriter.Exists(StateFileTarget.CaCertificateB)
                        || _fileWriter.Exists(StateFileTarget.CaPrivateKeyB)
                        || _fileWriter.BackupExists(
                            StateFileTarget.CertificateRevocationListB)
                        || _fileWriter.BackupExists(
                            StateFileTarget.CaCertificateB)
                        || _fileWriter.BackupExists(
                            StateFileTarget.CaPrivateKeyB))
                    {
                        throw new InvalidDataException(
                            "The free CA slot contains unexpected artifacts.");
                    }

                    if (currentState.CurrentSlot
                        != CertificateAuthoritySlot.A)
                    {
                        throw new InvalidOperationException(
                            "The current CA slot must be normalized by maintenance before rotation preparation.");
                    }

                    var reserved = new HashSet<string>(
                        _current.Ledger.EntriesBySerial.Keys.Select(
                            serial => serial.Hex),
                        StringComparer.Ordinal)
                    {
                        currentState.CaSerialNumber.Hex
                    };
                    var random = new SecureRandom();
                    SiteCertificateAuthority authority =
                        SiteCertificateAuthority.Create(
                            currentState.SiteId,
                            utcNow,
                            random,
                            reserved.Contains);
                    byte[] privateKey = null;
                    byte[] protectedKey = null;
                    byte[] certificate = null;
                    byte[] crl = null;
                    byte[] metadata = null;
                    byte[] ledgerBytes = null;
                    try
                    {
                        privateKey = authority.ExportPrivateKeyPkcs8();
                        protectedKey = _secondaryProtector.Protect(privateKey);
                        certificate = authority.GetCertificateDer();
                        CertificateRevocationListArtifact initialCrl =
                            authority.CreateRevocationList(
                                1,
                                new RevokedCertificateEntry[0],
                                utcNow,
                                GetNextCrlUpdate(
                                    utcNow,
                                    authority.NotAfterUtc),
                                random);
                        crl = initialCrl.GetDerBytes();
                        var nextAuthority =
                            new CertificateAuthorityLiveState(
                                CertificateAuthoritySlot.B,
                                CertificateAuthorityLiveRole.Next,
                                authority.SerialNumber,
                                authority.GetSpkiSha256(),
                                authority.NotBeforeUtc,
                                authority.NotAfterUtc,
                                1);
                        CertificateAuthorityState nextState =
                            currentState.Publish(
                                Guid.NewGuid(),
                                utcNow,
                                nextAuthority);
                        var nextLedger = new CertificateLedgerSnapshot(
                            _current.Ledger.EntriesBySerial.Values,
                            nextState.PkiRevision,
                            currentState.CrlNumber);
                        metadata = _codec.SerializeState(nextState);
                        ledgerBytes = _codec.SerializeLedger(nextLedger);
                        var changes = new List<StateFileChange>
                        {
                            new StateFileChange(
                                StateFileTarget.PkiMetadata,
                                true,
                                _current.MetadataBytes,
                                true,
                                metadata),
                            new StateFileChange(
                                StateFileTarget.CertificateLedger,
                                true,
                                _current.LedgerBytes,
                                true,
                                ledgerBytes),
                            new StateFileChange(
                                StateFileTarget.CertificateRevocationListB,
                                false,
                                null,
                                true,
                                crl),
                            new StateFileChange(
                                StateFileTarget.CaCertificateB,
                                false,
                                null,
                                true,
                                certificate),
                            new StateFileChange(
                                StateFileTarget.CaPrivateKeyB,
                                false,
                                null,
                                true,
                                protectedKey)
                        };
                        CertificateAuthorityStoreSnapshot applied = null;
                        _journalManager.Commit(
                            changes.AsReadOnly(),
                            () => applied = ReadCurrent(false));
                        ReplaceCurrent(applied);
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
                        Clear(protectedKey);
                        Clear(certificate);
                        Clear(crl);
                        Clear(metadata);
                        Clear(ledgerBytes);
                    }
                }
            });
        }

        internal CertificateAuthorityStoreSnapshot CancelRotation(
            Guid rotationId)
        {
            if (rotationId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Rotation ID must not be empty.",
                    nameof(rotationId));
            }

            return _mutationGate.Execute(() =>
            {
                lock (_lifecycleGate)
                {
                    ThrowIfAvailable();
                    CertificateAuthorityState nextState =
                        _current.State.CancelPublished(rotationId);
                    var nextLedger = new CertificateLedgerSnapshot(
                        _current.Ledger.EntriesBySerial.Values,
                        nextState.PkiRevision,
                        nextState.CrlNumber);
                    byte[] metadata = _codec.SerializeState(nextState);
                    byte[] ledgerBytes = _codec.SerializeLedger(nextLedger);
                    try
                    {
                        var changes = new List<StateFileChange>
                        {
                            new StateFileChange(
                                StateFileTarget.PkiMetadata,
                                true,
                                _current.MetadataBytes,
                                true,
                                metadata),
                            new StateFileChange(
                                StateFileTarget.CertificateLedger,
                                true,
                                _current.LedgerBytes,
                                true,
                                ledgerBytes),
                            new StateFileChange(
                                StateFileTarget.CertificateRevocationListB,
                                true,
                                _current.OtherCrlDer,
                                false,
                                null),
                            new StateFileChange(
                                StateFileTarget.CaCertificateB,
                                true,
                                _current.OtherCaCertificateDer,
                                false,
                                null),
                            new StateFileChange(
                                StateFileTarget.CaPrivateKeyB,
                                true,
                                _current.OtherProtectedPrivateKey,
                                false,
                                null)
                        };
                        CertificateAuthorityStoreSnapshot applied = null;
                        _journalManager.Commit(
                            changes.AsReadOnly(),
                            () => applied = ReadCurrent(false));
                        ReplaceCurrent(applied);
                        return _current.Clone();
                    }
                    catch (RecoveryRequiredException)
                    {
                        _recoveryRequired = true;
                        throw;
                    }
                    finally
                    {
                        Clear(metadata);
                        Clear(ledgerBytes);
                    }
                }
            });
        }
    }
}
