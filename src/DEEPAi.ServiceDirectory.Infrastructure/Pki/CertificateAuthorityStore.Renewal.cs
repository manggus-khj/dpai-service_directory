using System;
using System.Collections.Generic;
using System.Linq;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed partial class CertificateAuthorityStore
    {
        internal const int UnchangedIdentityOverlapDays = 7;
        internal const int ChangedIdentityOverlapHours = 24;

        internal ExternalRegistrationServiceResult RenewService(
            StateMutationCoordinator directoryState,
            DirectoryEndpointIdentity directoryIdentity,
            Guid issuerInstanceId,
            ServiceDefinition serviceDefinition,
            ValidatedCertificateSigningRequest signingRequest,
            CertificateIssuanceRequestEvidence evidence,
            ExternalCertificateRenewalRequest request,
            DateTime utcNow)
        {
            ValidateRenewalArguments(
                directoryState,
                directoryIdentity,
                issuerInstanceId,
                serviceDefinition,
                signingRequest,
                evidence,
                request,
                utcNow);

            return _mutationGate.Execute(() =>
            {
                PublishDueScheduledRetirementsCore(
                    directoryState,
                    issuerInstanceId,
                    utcNow);
                return RenewServiceCore(
                    directoryState,
                    directoryIdentity,
                    issuerInstanceId,
                    serviceDefinition,
                    signingRequest,
                    evidence,
                    request,
                    utcNow);
            });
        }

        internal bool PublishDueScheduledRetirements(
            StateMutationCoordinator directoryState,
            Guid issuerInstanceId,
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

            EnsureUtc(utcNow, nameof(utcNow));
            return _mutationGate.Execute(() =>
                PublishDueScheduledRetirementsCore(
                    directoryState,
                    issuerInstanceId,
                    utcNow));
        }

        private ExternalRegistrationServiceResult RenewServiceCore(
            StateMutationCoordinator directoryState,
            DirectoryEndpointIdentity directoryIdentity,
            Guid issuerInstanceId,
            ServiceDefinition serviceDefinition,
            ValidatedCertificateSigningRequest signingRequest,
            CertificateIssuanceRequestEvidence evidence,
            ExternalCertificateRenewalRequest request,
            DateTime utcNow)
        {
            DirectorySnapshot expectedDirectory;
            if (!directoryState.TryGetReadySnapshot(out expectedDirectory))
            {
                return ExternalRegistrationServiceResult.Failure(
                    ExternalRegistrationServiceStatus.Conflict);
            }

            using (CertificateAuthorityStoreSnapshot snapshot = GetCurrent())
            {
                if (!IsReadyActiveIssuer(snapshot, issuerInstanceId))
                {
                    return ExternalRegistrationServiceResult.Failure(
                        ExternalRegistrationServiceStatus.Conflict);
                }

                CertificateSerialNumber currentSerialNumber;
                if (!CertificateSerialNumber.TryCreate(
                        request.CurrentSerialNumber,
                        out currentSerialNumber))
                {
                    return NotRenewable();
                }

                CertificateLedgerEntry proofCertificate;
                if (!snapshot.Ledger.TryGetBySerial(
                        currentSerialNumber,
                        out proofCertificate)
                    || proofCertificate.ProductCode
                        != serviceDefinition.ProductCode
                    || (proofCertificate.Status
                            != CertificateLedgerStatus.Current
                        && proofCertificate.Status
                            != CertificateLedgerStatus.Retiring)
                    || utcNow < proofCertificate.IssuedUtc
                    || utcNow < proofCertificate.NotBeforeUtc
                    || utcNow >= proofCertificate.NotAfterUtc)
                {
                    return NotRenewable();
                }

                if (!CertificateRenewalProofValidator.TryValidate(
                        request,
                        serviceDefinition,
                        proofCertificate,
                        utcNow))
                {
                    return ExternalRegistrationServiceResult.Failure(
                        ExternalRegistrationServiceStatus
                            .InvalidCertificateProof);
                }

                CertificateLedgerEntry replayEntry;
                CertificateIssuanceReplayStatus replayStatus =
                    snapshot.Ledger.ResolveIssuanceRequest(
                        evidence,
                        out replayEntry);
                if (replayStatus ==
                    CertificateIssuanceReplayStatus.Conflict)
                {
                    return ExternalRegistrationServiceResult.Failure(
                        ExternalRegistrationServiceStatus.Conflict);
                }

                if (replayStatus ==
                    CertificateIssuanceReplayStatus.ExactReplay)
                {
                    ExternalRegistrationServiceResult nonceFailure =
                        RegisterRenewalNonce(
                            currentSerialNumber,
                            request.Nonce);
                    return nonceFailure
                        ?? CreateRegistrationResult(
                            ExternalRegistrationServiceStatus.Replayed,
                            replayEntry,
                            snapshot.CaCertificateDer,
                            snapshot.State.CaSerialNumber);
                }

                CertificateLedgerEntry currentEntry;
                if (proofCertificate.Status
                        != CertificateLedgerStatus.Current
                    || !snapshot.Ledger.TryGetCurrent(
                        serviceDefinition.ProductCode,
                        out currentEntry)
                    || currentEntry.SerialNumber != currentSerialNumber)
                {
                    return NotRenewable();
                }

                bool directoryChanged = !currentEntry.ServiceDefinition
                    .Equals(serviceDefinition);
                if (snapshot.State.PkiRevision == ulong.MaxValue
                    || (directoryChanged
                        && expectedDirectory.LogicalClock
                            == ulong.MaxValue))
                {
                    return ExternalRegistrationServiceResult.Failure(
                        ExternalRegistrationServiceStatus.LimitExceeded);
                }

                DateTime notBeforeUtc;
                DateTime notAfterUtc;
                DateTime scheduledRevocationUtc;
                try
                {
                    notBeforeUtc = utcNow.AddMinutes(
                        -SiteCertificateAuthority
                            .ActivationBackdateMinutes);
                    notAfterUtc = notBeforeUtc.AddYears(
                        SiteCertificateAuthority.LeafValidityYears);
                    DateTime requestedRetirement =
                        currentEntry.ServiceIdentity.Equals(
                            serviceDefinition.ServiceEndpointIdentity)
                            ? utcNow.AddDays(
                                UnchangedIdentityOverlapDays)
                            : utcNow.AddHours(
                                ChangedIdentityOverlapHours);
                    scheduledRevocationUtc = requestedRetirement
                        <= currentEntry.NotAfterUtc
                            ? requestedRetirement
                            : currentEntry.NotAfterUtc;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return NotRenewable();
                }

                if (notBeforeUtc < snapshot.State.NotBeforeUtc
                    || notAfterUtc > snapshot.State.NotAfterUtc
                    || scheduledRevocationUtc <= currentEntry.IssuedUtc)
                {
                    return NotRenewable();
                }

                ulong nextPkiRevision =
                    snapshot.State.PkiRevision + 1;
                List<CertificateLedgerEntry> projectedEntries =
                    snapshot.Ledger.EntriesBySerial.Values
                        .Select(entry => entry.SerialNumber
                                == currentEntry.SerialNumber
                            ? entry.ScheduleRevocation(
                                scheduledRevocationUtc)
                            : entry)
                        .ToList();
                byte[] subjectPublicKeyInfoSha256 =
                    signingRequest.GetSubjectPublicKeyInfoSha256();
                try
                {
                    try
                    {
                        CertificateLedgerCapacityGuard
                            .EnsureMaximumIssuanceFits(
                                projectedEntries,
                                nextPkiRevision,
                                snapshot.State.CrlNumber,
                                snapshot.State.CaSerialNumber,
                                evidence,
                                subjectPublicKeyInfoSha256,
                                utcNow,
                                notBeforeUtc,
                                notAfterUtc);
                    }
                    catch (CertificateLedgerCapacityExceededException)
                    {
                        return ExternalRegistrationServiceResult.Failure(
                            ExternalRegistrationServiceStatus
                                .LimitExceeded);
                    }

                    ExternalRegistrationServiceResult nonceFailure =
                        RegisterRenewalNonce(
                            currentSerialNumber,
                            request.Nonce);
                    if (nonceFailure != null)
                    {
                        return nonceFailure;
                    }

                    return IssueAndCommitRenewal(
                        directoryState,
                        directoryIdentity,
                        serviceDefinition,
                        signingRequest,
                        evidence,
                        snapshot,
                        expectedDirectory,
                        projectedEntries,
                        subjectPublicKeyInfoSha256,
                        nextPkiRevision,
                        directoryChanged,
                        utcNow);
                }
                finally
                {
                    Clear(subjectPublicKeyInfoSha256);
                }
            }
        }

        private ExternalRegistrationServiceResult IssueAndCommitRenewal(
            StateMutationCoordinator directoryState,
            DirectoryEndpointIdentity directoryIdentity,
            ServiceDefinition serviceDefinition,
            ValidatedCertificateSigningRequest signingRequest,
            CertificateIssuanceRequestEvidence evidence,
            CertificateAuthorityStoreSnapshot snapshot,
            DirectorySnapshot expectedDirectory,
            ICollection<CertificateLedgerEntry> projectedEntries,
            byte[] subjectPublicKeyInfoSha256,
            ulong nextPkiRevision,
            bool directoryChanged,
            DateTime utcNow)
        {
            byte[] privateKey = null;
            byte[] leafCertificate = null;
            byte[] csrSha256 = null;
            byte[] requestPayloadSha256 = null;
            IssuedCertificateArtifact issued = null;
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
                var reservedSerials = new HashSet<string>(
                    snapshot.Ledger.EntriesBySerial.Keys
                        .Select(serial => serial.Hex),
                    StringComparer.Ordinal)
                {
                    snapshot.State.CaSerialNumber.Hex
                };
                PkiSerialNumber serialNumber =
                    PkiSerialNumber.CreateRandom(
                        random,
                        reservedSerials.Contains);
                _serviceMutationFaultInjector.OnFault(
                    CertificateServiceMutationFaultPoint.SerialReserved,
                    CertificateServiceMutationOperation.Renewal,
                    serialNumber.ToLedgerSerialNumber());
                issued = authority.IssueServiceLeaf(
                    signingRequest,
                    directoryIdentity,
                    serialNumber,
                    utcNow,
                    random);
                _serviceMutationFaultInjector.OnFault(
                    CertificateServiceMutationFaultPoint.CertificateSigned,
                    CertificateServiceMutationOperation.Renewal,
                    serialNumber.ToLedgerSerialNumber());
                leafCertificate = issued.GetCertificateDer();
                csrSha256 = evidence.GetCsrSha256();
                requestPayloadSha256 =
                    evidence.GetRequestPayloadSha256();
                CertificateLedgerEntry issuedEntry =
                    CertificateLedgerEntry.CreateIssued(
                        serialNumber.ToLedgerSerialNumber(),
                        snapshot.State.CaSerialNumber,
                        serviceDefinition,
                        evidence.RequestId,
                        CertificateIssuanceKind.Renewal,
                        csrSha256,
                        requestPayloadSha256,
                        subjectPublicKeyInfoSha256,
                        leafCertificate,
                        utcNow,
                        issued.NotBeforeUtc,
                        issued.NotAfterUtc);
                projectedEntries.Add(issuedEntry);
                var nextLedger = new CertificateLedgerSnapshot(
                    projectedEntries,
                    nextPkiRevision,
                    snapshot.State.CrlNumber);
                CertificateAuthorityState nextState =
                    snapshot.State.WithHighWater(
                        nextPkiRevision,
                        snapshot.State.CrlNumber);
                DirectorySnapshot nextDirectory = directoryChanged
                    ? CreateNextRegistrationDirectory(
                        expectedDirectory,
                        serviceDefinition,
                        snapshot.State.IssuerInstanceId,
                        utcNow)
                    : expectedDirectory;
                ExternalRegistrationServiceResult result =
                    CreateRegistrationResult(
                        ExternalRegistrationServiceStatus.Renewed,
                        issuedEntry,
                        snapshot.CaCertificateDer,
                        snapshot.State.CaSerialNumber);
                using (CertificateAuthorityStoreSnapshot committed =
                    CommitServiceMutation(
                        directoryState,
                        CertificateServiceMutationOperation.Renewal,
                        expectedDirectory,
                        nextDirectory,
                        nextState,
                        nextLedger,
                        snapshot.CrlDer,
                        utcNow))
                {
                }

                _serviceMutationFaultInjector.OnFault(
                    CertificateServiceMutationFaultPoint.ResponseStarting,
                    CertificateServiceMutationOperation.Renewal,
                    issuedEntry.SerialNumber);
                return result;
            }
            finally
            {
                if (issued != null)
                {
                    issued.Dispose();
                }

                Clear(privateKey);
                Clear(leafCertificate);
                Clear(csrSha256);
                Clear(requestPayloadSha256);
            }
        }

        private ExternalRegistrationServiceResult RegisterRenewalNonce(
            CertificateSerialNumber currentSerialNumber,
            byte[] nonce)
        {
            try
            {
                RenewalNonceRegistrationStatus status =
                    _renewalNonceReplayCache.Register(
                        currentSerialNumber,
                        nonce);
                if (status == RenewalNonceRegistrationStatus.Accepted)
                {
                    return null;
                }

                return ExternalRegistrationServiceResult.Failure(
                    status ==
                        RenewalNonceRegistrationStatus.CapacityExceeded
                        ? ExternalRegistrationServiceStatus.LimitExceeded
                        : ExternalRegistrationServiceStatus
                            .InvalidCertificateProof);
            }
            finally
            {
                Clear(nonce);
            }
        }

        private static bool IsReadyActiveIssuer(
            CertificateAuthorityStoreSnapshot snapshot,
            Guid issuerInstanceId)
        {
            return snapshot.State.Role
                    == CertificateAuthorityRole.ActiveIssuer
                && snapshot.State.LastBackupUtc.HasValue
                && snapshot.State.IssuerInstanceId == issuerInstanceId;
        }

        private static ExternalRegistrationServiceResult NotRenewable()
        {
            return ExternalRegistrationServiceResult.Failure(
                ExternalRegistrationServiceStatus.CertificateNotRenewable);
        }

        private static void ValidateRenewalArguments(
            StateMutationCoordinator directoryState,
            DirectoryEndpointIdentity directoryIdentity,
            Guid issuerInstanceId,
            ServiceDefinition serviceDefinition,
            ValidatedCertificateSigningRequest signingRequest,
            CertificateIssuanceRequestEvidence evidence,
            ExternalCertificateRenewalRequest request,
            DateTime utcNow)
        {
            if (directoryState == null
                || directoryIdentity == null
                || serviceDefinition == null
                || signingRequest == null
                || evidence == null
                || request == null)
            {
                throw new ArgumentNullException(
                    directoryState == null
                        ? nameof(directoryState)
                        : directoryIdentity == null
                            ? nameof(directoryIdentity)
                            : serviceDefinition == null
                                ? nameof(serviceDefinition)
                                : signingRequest == null
                                    ? nameof(signingRequest)
                                    : evidence == null
                                        ? nameof(evidence)
                                        : nameof(request));
            }

            if (issuerInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The issuer instance ID must not be empty.",
                    nameof(issuerInstanceId));
            }

            EnsureUtc(utcNow, nameof(utcNow));
        }
    }
}
