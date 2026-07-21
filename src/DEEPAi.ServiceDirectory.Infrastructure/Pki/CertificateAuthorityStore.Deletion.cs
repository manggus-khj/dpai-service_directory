using System;
using System.Collections.Generic;
using System.Linq;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.Domain.Registration;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal interface ICertificateServiceMutationAdministration
    {
        CertificateServiceDeletionResult DeleteService(
            ProductCode productCode,
            DateTime utcNow);
    }

    internal enum CertificateServiceDeletionStatus
    {
        Deleted = 0,
        NotFound = 1,
        Conflict = 2,
        LimitExceeded = 3
    }

    internal sealed class CertificateServiceDeletionResult
    {
        private CertificateServiceDeletionResult(
            CertificateServiceDeletionStatus status,
            CertificateSerialNumber revokedSerialNumber)
        {
            bool deleted = status == CertificateServiceDeletionStatus.Deleted;
            if (!Enum.IsDefined(
                    typeof(CertificateServiceDeletionStatus),
                    status)
                || deleted != revokedSerialNumber.IsValid)
            {
                throw new ArgumentException(
                    "The certificate service deletion result is inconsistent.");
            }

            Status = status;
            RevokedSerialNumber = revokedSerialNumber;
        }

        internal CertificateServiceDeletionStatus Status { get; }

        internal CertificateSerialNumber RevokedSerialNumber { get; }

        internal static CertificateServiceDeletionResult Success(
            CertificateSerialNumber revokedSerialNumber)
        {
            return new CertificateServiceDeletionResult(
                CertificateServiceDeletionStatus.Deleted,
                revokedSerialNumber);
        }

        internal static CertificateServiceDeletionResult Failure(
            CertificateServiceDeletionStatus status)
        {
            if (status == CertificateServiceDeletionStatus.Deleted)
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }

            return new CertificateServiceDeletionResult(
                status,
                default(CertificateSerialNumber));
        }
    }

    internal sealed partial class CertificateAuthorityStore
    {
        internal CertificateServiceDeletionResult DeleteService(
            StateMutationCoordinator directoryState,
            Guid issuerInstanceId,
            ProductCode productCode,
            DateTime utcNow)
        {
            if (directoryState == null)
            {
                throw new ArgumentNullException(nameof(directoryState));
            }

            if (issuerInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The issuer instance ID must not be empty.",
                    nameof(issuerInstanceId));
            }

            if (!productCode.IsValid)
            {
                throw new ArgumentException(
                    "The product code must be valid.",
                    nameof(productCode));
            }

            EnsureUtc(utcNow, nameof(utcNow));
            return _mutationGate.Execute(() => DeleteServiceCore(
                directoryState,
                issuerInstanceId,
                productCode,
                utcNow));
        }

        private CertificateServiceDeletionResult DeleteServiceCore(
            StateMutationCoordinator directoryState,
            Guid issuerInstanceId,
            ProductCode productCode,
            DateTime utcNow)
        {
            DirectorySnapshot expectedDirectory;
            if (!directoryState.TryGetReadySnapshot(out expectedDirectory))
            {
                return CertificateServiceDeletionResult.Failure(
                    CertificateServiceDeletionStatus.Conflict);
            }

            using (CertificateAuthorityStoreSnapshot snapshot = GetCurrent())
            {
                if (snapshot.State.Role !=
                        CertificateAuthorityRole.ActiveIssuer
                    || !snapshot.State.LastBackupUtc.HasValue
                    || snapshot.State.IssuerInstanceId != issuerInstanceId)
                {
                    return CertificateServiceDeletionResult.Failure(
                        CertificateServiceDeletionStatus.Conflict);
                }

                DeleteResult deletion = RegistrationStateMachine.Delete(
                    expectedDirectory,
                    productCode,
                    issuerInstanceId,
                    utcNow);
                if (!deletion.IsSuccess)
                {
                    CertificateServiceDeletionStatus status =
                        deletion.ErrorCode == DomainErrorCode.NotFound
                            ? CertificateServiceDeletionStatus.NotFound
                            : deletion.ErrorCode ==
                                DomainErrorCode.LogicalClockExhausted
                                ? CertificateServiceDeletionStatus
                                    .LimitExceeded
                                : CertificateServiceDeletionStatus.Conflict;
                    return CertificateServiceDeletionResult.Failure(status);
                }

                CertificateLedgerEntry currentEntry;
                if (!snapshot.Ledger.TryGetCurrent(
                        productCode,
                        out currentEntry)
                    || snapshot.Ledger.EntriesBySerial.Values.Any(entry =>
                        entry.ProductCode == productCode
                        && entry.Status !=
                            CertificateLedgerStatus.Revoked
                        && utcNow < entry.IssuedUtc))
                {
                    return CertificateServiceDeletionResult.Failure(
                        CertificateServiceDeletionStatus.Conflict);
                }

                if (snapshot.State.PkiRevision == ulong.MaxValue
                    || snapshot.State.CrlNumber == ulong.MaxValue)
                {
                    return CertificateServiceDeletionResult.Failure(
                        CertificateServiceDeletionStatus.LimitExceeded);
                }

                List<CertificateLedgerEntry> entries = snapshot.Ledger
                    .EntriesBySerial.Values
                    .Select(entry => entry.ProductCode == productCode
                            && entry.Status !=
                                CertificateLedgerStatus.Revoked
                        ? entry.Revoke(
                            utcNow,
                            CertificateRevocationReason
                                .CessationOfOperation)
                        : entry)
                    .ToList();
                ulong nextPkiRevision = snapshot.State.PkiRevision + 1;
                ulong nextCrlNumber = snapshot.State.CrlNumber + 1;
                var nextLedger = new CertificateLedgerSnapshot(
                    entries,
                    nextPkiRevision,
                    nextCrlNumber);
                if (!new CertificateAuthorityStateCodec()
                        .IsLedgerWithinDocumentLimit(nextLedger))
                {
                    return CertificateServiceDeletionResult.Failure(
                        CertificateServiceDeletionStatus.LimitExceeded);
                }

                CertificateAuthorityState nextState =
                    snapshot.State.WithHighWater(
                        nextPkiRevision,
                        nextCrlNumber);
                byte[] privateKey = null;
                byte[] nextCrlDer = null;
                try
                {
                    privateKey = _protector.Unprotect(
                        snapshot.ProtectedPrivateKey);
                    SiteCertificateAuthority authority =
                        SiteCertificateAuthority.Restore(
                            snapshot.State.SiteId,
                            snapshot.CaCertificateDer,
                            privateKey,
                            utcNow);
                    var random = new SecureRandom();
                    CertificateRevocationListArtifact crl =
                        authority.CreateRevocationList(
                            nextCrlNumber,
                            CreateRevokedEntries(nextLedger),
                            utcNow,
                            GetNextCrlUpdate(
                                utcNow,
                                authority.NotAfterUtc),
                            random);
                    nextCrlDer = crl.GetDerBytes();
                    using (CertificateAuthorityStoreSnapshot committed =
                        CommitServiceMutation(
                            directoryState,
                            CertificateServiceMutationOperation.Deletion,
                            expectedDirectory,
                            deletion.NextSnapshot,
                            nextState,
                            nextLedger,
                            nextCrlDer,
                            utcNow))
                    {
                    }

                    _serviceMutationFaultInjector.OnFault(
                        CertificateServiceMutationFaultPoint
                            .ResponseStarting,
                        CertificateServiceMutationOperation.Deletion,
                        currentEntry.SerialNumber);
                    return CertificateServiceDeletionResult.Success(
                        currentEntry.SerialNumber);
                }
                finally
                {
                    Clear(privateKey);
                    Clear(nextCrlDer);
                }
            }
        }
    }
}
