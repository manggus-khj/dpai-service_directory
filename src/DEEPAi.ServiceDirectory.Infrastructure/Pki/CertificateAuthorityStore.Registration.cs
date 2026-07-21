using System;
using System.Collections.Generic;
using System.Linq;
using DEEPAi.ServiceDirectory.Application.Registration;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed partial class CertificateAuthorityStore
    {
        private const string RegistrationFailureReason =
            "CERTIFICATE_ISSUANCE_FAILED";

        internal ExternalRegistrationServiceResult RegisterService(
            StateMutationCoordinator directoryState,
            RegistrationModeOwner registrationModeOwner,
            DirectoryEndpointIdentity directoryIdentity,
            Guid issuerInstanceId,
            ServiceDefinition serviceDefinition,
            ValidatedCertificateSigningRequest signingRequest,
            CertificateIssuanceRequestEvidence evidence,
            DateTime utcNow)
        {
            ValidateRegistrationArguments(
                directoryState,
                registrationModeOwner,
                directoryIdentity,
                issuerInstanceId,
                serviceDefinition,
                signingRequest,
                evidence,
                utcNow);

            return _mutationGate.Execute(() => RegisterServiceCore(
                directoryState,
                registrationModeOwner,
                directoryIdentity,
                issuerInstanceId,
                serviceDefinition,
                signingRequest,
                evidence,
                utcNow));
        }

        private ExternalRegistrationServiceResult RegisterServiceCore(
            StateMutationCoordinator directoryState,
            RegistrationModeOwner registrationModeOwner,
            DirectoryEndpointIdentity directoryIdentity,
            Guid issuerInstanceId,
            ServiceDefinition serviceDefinition,
            ValidatedCertificateSigningRequest signingRequest,
            CertificateIssuanceRequestEvidence evidence,
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
                if (snapshot.State.Role !=
                        CertificateAuthorityRole.ActiveIssuer
                    || !snapshot.State.LastBackupUtc.HasValue
                    || snapshot.State.IssuerInstanceId != issuerInstanceId)
                {
                    return ExternalRegistrationServiceResult.Failure(
                        ExternalRegistrationServiceStatus.Conflict);
                }

                CertificateLedgerEntry replayEntry;
                CertificateIssuanceReplayStatus replayStatus =
                    snapshot.Ledger.ResolveIssuanceRequest(
                        evidence,
                        out replayEntry);
                if (replayStatus ==
                    CertificateIssuanceReplayStatus.ExactReplay)
                {
                    return CreateRegistrationResult(
                        ExternalRegistrationServiceStatus.Replayed,
                        replayEntry,
                        snapshot.CaCertificateDer,
                        snapshot.State.CaSerialNumber);
                }

                if (replayStatus ==
                    CertificateIssuanceReplayStatus.Conflict)
                {
                    return ExternalRegistrationServiceResult.Failure(
                        ExternalRegistrationServiceStatus.Conflict);
                }

                CertificateLedgerEntry currentEntry;
                bool isReregistration = snapshot.Ledger.TryGetCurrent(
                    serviceDefinition.ProductCode,
                    out currentEntry);
                if ((!isReregistration
                        && (snapshot.Ledger.CurrentCount >=
                                CertificateLedgerSnapshot
                                    .CurrentCertificateLimit
                            || expectedDirectory.ActiveCount >=
                                DirectorySnapshot.ActiveServiceLimit))
                    || snapshot.State.PkiRevision == ulong.MaxValue
                    || expectedDirectory.LogicalClock == ulong.MaxValue
                    || (isReregistration
                        && snapshot.State.CrlNumber == ulong.MaxValue))
                {
                    return ExternalRegistrationServiceResult.Failure(
                        ExternalRegistrationServiceStatus.LimitExceeded);
                }

                if (isReregistration && utcNow < currentEntry.IssuedUtc)
                {
                    return ExternalRegistrationServiceResult.Failure(
                        ExternalRegistrationServiceStatus.Conflict);
                }

                DateTime notBeforeUtc;
                DateTime notAfterUtc;
                try
                {
                    notBeforeUtc = utcNow.AddMinutes(
                        -SiteCertificateAuthority
                            .ActivationBackdateMinutes);
                    notAfterUtc = notBeforeUtc.AddYears(
                        SiteCertificateAuthority.LeafValidityYears);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return ExternalRegistrationServiceResult.Failure(
                        ExternalRegistrationServiceStatus.Conflict);
                }

                if (notBeforeUtc < snapshot.State.NotBeforeUtc
                    || notAfterUtc > snapshot.State.NotAfterUtc)
                {
                    return ExternalRegistrationServiceResult.Failure(
                        ExternalRegistrationServiceStatus.Conflict);
                }

                ulong nextPkiRevision =
                    snapshot.State.PkiRevision + 1;
                ulong nextCrlNumber = snapshot.State.CrlNumber
                    + (isReregistration ? 1UL : 0UL);
                List<CertificateLedgerEntry> projectedEntries =
                    CreateProjectedRegistrationEntries(
                        snapshot.Ledger,
                        currentEntry,
                        isReregistration,
                        utcNow);
                byte[] subjectPublicKeyInfoSha256 =
                    signingRequest.GetSubjectPublicKeyInfoSha256();
                try
                {
                    try
                    {
                        CertificateLedgerEntry ignoredReplayEntry;
                        CertificateIssuancePreflight.Evaluate(
                            snapshot.Ledger,
                            projectedEntries,
                            nextPkiRevision,
                            nextCrlNumber,
                            snapshot.State.CaSerialNumber,
                            evidence,
                            subjectPublicKeyInfoSha256,
                            utcNow,
                            notBeforeUtc,
                            notAfterUtc,
                            out ignoredReplayEntry);
                    }
                    catch (CertificateLedgerCapacityExceededException)
                    {
                        return ExternalRegistrationServiceResult.Failure(
                            ExternalRegistrationServiceStatus.LimitExceeded);
                    }

                    CertificateServiceMutationOperation operation =
                        isReregistration
                            ? CertificateServiceMutationOperation
                                .Reregistration
                            : CertificateServiceMutationOperation
                                .Registration;
                    _serviceMutationFaultInjector.OnFault(
                        CertificateServiceMutationFaultPoint
                            .BeforeRegistrationClaim,
                        operation,
                        null);
                    RegistrationModeClaimResult claimResult =
                        registrationModeOwner.TryClaimValidatedRequest(true);
                    if (!claimResult.IsClaimed)
                    {
                        ExternalRegistrationServiceStatus failureStatus =
                            claimResult.Status ==
                                RegistrationModeClaimStatus.InvalidRequest
                                ? ExternalRegistrationServiceStatus
                                    .CertificateRequestInvalid
                                : claimResult.Status ==
                                    RegistrationModeClaimStatus
                                        .AlreadyClaimed
                                    ? ExternalRegistrationServiceStatus
                                        .Conflict
                                    : ExternalRegistrationServiceStatus
                                        .RegistrationModeClosed;
                        return ExternalRegistrationServiceResult.Failure(
                            failureStatus);
                    }

                    using (RegistrationModeClaim claim = claimResult.Claim)
                    {
                        ExternalRegistrationServiceResult result;
                        try
                        {
                            result = IssueAndCommitRegistration(
                                directoryState,
                                directoryIdentity,
                                issuerInstanceId,
                                serviceDefinition,
                                signingRequest,
                                evidence,
                                snapshot,
                                expectedDirectory,
                                projectedEntries,
                                subjectPublicKeyInfoSha256,
                                nextPkiRevision,
                                nextCrlNumber,
                                isReregistration,
                                utcNow);
                        }
                        catch
                        {
                            claim.Complete(
                                RegistrationModeLastResult.Failure(
                                    utcNow,
                                    RegistrationFailureReason));
                            throw;
                        }

                        claim.Complete(
                            CreateSuccessfulCompletion(
                                result,
                                isReregistration,
                                utcNow));
                        if (!CertificateSerialNumber.TryCreate(
                                result.Certificate.SerialNumber,
                                out CertificateSerialNumber resultSerial))
                        {
                            throw new InvalidOperationException(
                                "The committed certificate result has an invalid serial.");
                        }

                        _serviceMutationFaultInjector.OnFault(
                            CertificateServiceMutationFaultPoint
                                .ResponseStarting,
                            operation,
                            resultSerial);
                        return result;
                    }
                }
                finally
                {
                    Clear(subjectPublicKeyInfoSha256);
                }
            }
        }

        private ExternalRegistrationServiceResult IssueAndCommitRegistration(
            StateMutationCoordinator directoryState,
            DirectoryEndpointIdentity directoryIdentity,
            Guid issuerInstanceId,
            ServiceDefinition serviceDefinition,
            ValidatedCertificateSigningRequest signingRequest,
            CertificateIssuanceRequestEvidence evidence,
            CertificateAuthorityStoreSnapshot snapshot,
            DirectorySnapshot expectedDirectory,
            ICollection<CertificateLedgerEntry> projectedEntries,
            byte[] subjectPublicKeyInfoSha256,
            ulong nextPkiRevision,
            ulong nextCrlNumber,
            bool isReregistration,
            DateTime utcNow)
        {
            byte[] privateKey = null;
            byte[] leafCertificate = null;
            byte[] csrSha256 = null;
            byte[] requestPayloadSha256 = null;
            byte[] nextCrlDer = null;
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
                CertificateServiceMutationOperation operation =
                    isReregistration
                        ? CertificateServiceMutationOperation.Reregistration
                        : CertificateServiceMutationOperation.Registration;
                _serviceMutationFaultInjector.OnFault(
                    CertificateServiceMutationFaultPoint.SerialReserved,
                    operation,
                    serialNumber.ToLedgerSerialNumber());
                issued = authority.IssueServiceLeaf(
                    signingRequest,
                    directoryIdentity,
                    serialNumber,
                    utcNow,
                    random);
                _serviceMutationFaultInjector.OnFault(
                    CertificateServiceMutationFaultPoint.CertificateSigned,
                    operation,
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
                        CertificateIssuanceKind.Registration,
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
                    nextCrlNumber);
                CertificateAuthorityState nextState =
                    snapshot.State.WithHighWater(
                        nextPkiRevision,
                        nextCrlNumber);
                DirectorySnapshot nextDirectory =
                    CreateNextRegistrationDirectory(
                        expectedDirectory,
                        serviceDefinition,
                        issuerInstanceId,
                        utcNow);

                if (isReregistration)
                {
                    DateTime nextUpdateUtc = GetNextCrlUpdate(
                        utcNow,
                        authority.NotAfterUtc);
                    CertificateRevocationListArtifact crl =
                        authority.CreateRevocationList(
                            nextCrlNumber,
                            CreateRevokedEntries(nextLedger),
                            utcNow,
                            nextUpdateUtc,
                            random);
                    nextCrlDer = crl.GetDerBytes();
                }
                else
                {
                    nextCrlDer = (byte[])snapshot.CrlDer.Clone();
                }

                ExternalRegistrationServiceStatus resultStatus =
                    isReregistration
                        ? ExternalRegistrationServiceStatus.Reregistered
                        : ExternalRegistrationServiceStatus.Registered;
                ExternalRegistrationServiceResult result =
                    CreateRegistrationResult(
                        resultStatus,
                        issuedEntry,
                        snapshot.CaCertificateDer,
                        snapshot.State.CaSerialNumber);
                using (CertificateAuthorityStoreSnapshot committed =
                    CommitServiceMutation(
                        directoryState,
                        operation,
                        expectedDirectory,
                        nextDirectory,
                        nextState,
                        nextLedger,
                        nextCrlDer,
                        utcNow))
                {
                }

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
                Clear(nextCrlDer);
            }
        }

        private static List<CertificateLedgerEntry>
            CreateProjectedRegistrationEntries(
                CertificateLedgerSnapshot ledger,
                CertificateLedgerEntry currentEntry,
                bool isReregistration,
                DateTime utcNow)
        {
            return ledger.EntriesBySerial.Values
                .Select(entry => isReregistration
                    && entry.SerialNumber == currentEntry.SerialNumber
                        ? entry.Revoke(
                            utcNow,
                            CertificateRevocationReason.Superseded)
                        : entry)
                .ToList();
        }

        private static DirectorySnapshot CreateNextRegistrationDirectory(
            DirectorySnapshot current,
            ServiceDefinition serviceDefinition,
            Guid issuerInstanceId,
            DateTime utcNow)
        {
            ulong nextLogicalClock = current.LogicalClock + 1;
            var records = current.Records.Values
                .Where(record => record.Definition.ProductCode !=
                    serviceDefinition.ProductCode)
                .ToList();
            records.Add(ServiceRecord.CreateActive(
                serviceDefinition,
                utcNow,
                nextLogicalClock,
                issuerInstanceId));
            return new DirectorySnapshot(
                records,
                new PendingRegistration[0],
                nextLogicalClock);
        }

        private static ExternalRegistrationServiceResult
            CreateRegistrationResult(
                ExternalRegistrationServiceStatus status,
                CertificateLedgerEntry entry,
                byte[] caCertificateDer,
                CertificateSerialNumber issuerCaSerialNumber)
        {
            ServiceDefinition definition = entry.ServiceDefinition;
            var service = new ExternalServiceItem(
                definition.Name,
                definition.ProductCode.Value,
                definition.ServiceHostName,
                definition.ServiceIpv4Address,
                definition.Port,
                entry.IssuedUtc);
            var certificate = new ExternalIssuedCertificate(
                entry.GetLeafCertificate(),
                caCertificateDer,
                entry.SerialNumber.Hex,
                entry.NotBeforeUtc,
                entry.NotAfterUtc,
                SiteCertificateAuthority.GetIssuerCrlRelativePath(
                    issuerCaSerialNumber));
            return ExternalRegistrationServiceResult.Success(
                status,
                service,
                certificate);
        }

        private static RegistrationModeLastResult
            CreateSuccessfulCompletion(
                ExternalRegistrationServiceResult result,
                bool isReregistration,
                DateTime utcNow)
        {
            return RegistrationModeLastResult.Success(
                utcNow,
                isReregistration
                    ? RegistrationModeCompletionOutcome.Reregistered
                    : RegistrationModeCompletionOutcome.Registered,
                result.Service.ProductCode,
                result.Service.ServiceHostName,
                result.Service.ServiceIpv4Address,
                result.Certificate.SerialNumber,
                result.Certificate.NotAfterUtc);
        }

        private static void ValidateRegistrationArguments(
            StateMutationCoordinator directoryState,
            RegistrationModeOwner registrationModeOwner,
            DirectoryEndpointIdentity directoryIdentity,
            Guid issuerInstanceId,
            ServiceDefinition serviceDefinition,
            ValidatedCertificateSigningRequest signingRequest,
            CertificateIssuanceRequestEvidence evidence,
            DateTime utcNow)
        {
            if (directoryState == null
                || registrationModeOwner == null
                || directoryIdentity == null
                || serviceDefinition == null
                || signingRequest == null
                || evidence == null)
            {
                throw new ArgumentNullException(
                    directoryState == null
                        ? nameof(directoryState)
                        : registrationModeOwner == null
                            ? nameof(registrationModeOwner)
                            : directoryIdentity == null
                                ? nameof(directoryIdentity)
                                : serviceDefinition == null
                                    ? nameof(serviceDefinition)
                                    : signingRequest == null
                                        ? nameof(signingRequest)
                                        : nameof(evidence));
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
