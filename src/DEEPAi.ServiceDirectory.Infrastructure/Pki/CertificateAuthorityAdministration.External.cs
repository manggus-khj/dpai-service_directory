using System;
using System.Collections.Generic;
using System.IO;
using DEEPAi.ServiceDirectory.Application.Registration;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed partial class CertificateAuthorityAdministration
    {
        internal ExternalTrustInfo GetExternalTrustInfo()
        {
            ThrowIfDisposed();
            EnsureProvisioned();
            using (CertificateAuthorityStoreSnapshot snapshot =
                _store.GetCurrent())
            {
                return new ExternalTrustInfo(
                    snapshot.State.SiteId,
                    snapshot.CaCertificateDer,
                    snapshot.State.GetCaSpkiSha256(),
                    SiteCertificateAuthority.CrlRelativePath);
            }
        }

        internal ExternalTrustSnapshot GetExternalTrustSnapshot()
        {
            ThrowIfDisposed();
            EnsureProvisioned();
            using (CertificateAuthorityStoreSnapshot snapshot =
                _store.GetCurrent())
            {
                var trustInfo = new ExternalTrustInfo(
                    snapshot.State.SiteId,
                    snapshot.CaCertificateDer,
                    snapshot.State.GetCaSpkiSha256(),
                    SiteCertificateAuthority.CrlRelativePath);
                var authorities = new List<ExternalTrustAuthority>
                {
                    CreateExternalTrustAuthority(
                        snapshot.State.CurrentAuthority,
                        snapshot.CaCertificateDer)
                };
                if (snapshot.State.OtherAuthority != null)
                {
                    authorities.Add(CreateExternalTrustAuthority(
                        snapshot.State.OtherAuthority,
                        snapshot.OtherCaCertificateDer));
                }

                var trustBundle = new ExternalTrustBundle(
                    snapshot.State.SiteId,
                    snapshot.State.TrustRevision,
                    snapshot.State.RotationId,
                    MapExternalRotationPhase(
                        snapshot.State.RotationPhase),
                    snapshot.State.PublishedUtc,
                    snapshot.State.ActivationNotBeforeUtc,
                    snapshot.State.ActivatedUtc,
                    snapshot.State.RetirementNotBeforeUtc,
                    authorities);
                return new ExternalTrustSnapshot(trustInfo, trustBundle);
            }
        }

        private static ExternalTrustAuthority CreateExternalTrustAuthority(
            CertificateAuthorityLiveState authority,
            byte[] certificate)
        {
            return new ExternalTrustAuthority(
                    MapExternalAuthorityRole(authority.Role),
                    authority.CaSerialNumber.Hex,
                    certificate,
                    authority.GetCaSpkiSha256(),
                    SiteCertificateAuthority.GetIssuerCrlRelativePath(
                        authority.CaSerialNumber),
                    authority.NotBeforeUtc,
                    authority.NotAfterUtc);
        }

        private static ExternalCaRotationPhase MapExternalRotationPhase(
            CertificateAuthorityRotationPhase phase)
        {
            switch (phase)
            {
                case CertificateAuthorityRotationPhase.Stable:
                    return ExternalCaRotationPhase.Stable;
                case CertificateAuthorityRotationPhase.Published:
                    return ExternalCaRotationPhase.Published;
                case CertificateAuthorityRotationPhase.Activated:
                    return ExternalCaRotationPhase.Activated;
                default:
                    throw new ArgumentOutOfRangeException(nameof(phase));
            }
        }

        private static ExternalTrustAuthorityRole MapExternalAuthorityRole(
            CertificateAuthorityLiveRole role)
        {
            switch (role)
            {
                case CertificateAuthorityLiveRole.Current:
                    return ExternalTrustAuthorityRole.Current;
                case CertificateAuthorityLiveRole.Next:
                    return ExternalTrustAuthorityRole.Next;
                case CertificateAuthorityLiveRole.Retiring:
                    return ExternalTrustAuthorityRole.Retiring;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role));
            }
        }

        internal byte[] GetExternalCertificateRevocationList()
        {
            ThrowIfDisposed();
            EnsureProvisioned();
            using (CertificateAuthorityStoreSnapshot snapshot =
                _store.GetCurrent())
            {
                if (snapshot.CrlDer.Length >
                    ExternalApiContract.MaximumCrlResponseBytes)
                {
                    throw new InvalidDataException(
                        "The current CRL exceeds the External response limit.");
                }

                return (byte[])snapshot.CrlDer.Clone();
            }
        }

        internal byte[] GetExternalCertificateRevocationList(
            string caSerialNumber)
        {
            ThrowIfDisposed();
            EnsureProvisioned();
            if (string.IsNullOrEmpty(caSerialNumber))
            {
                throw new ArgumentException(
                    "CA serial number is required.",
                    nameof(caSerialNumber));
            }

            using (CertificateAuthorityStoreSnapshot snapshot =
                _store.GetCurrent())
            {
                byte[] crl;
                if (StringComparer.Ordinal.Equals(
                        caSerialNumber,
                        snapshot.State.CaSerialNumber.Hex))
                {
                    crl = snapshot.CrlDer;
                }
                else if (snapshot.State.OtherAuthority != null
                    && StringComparer.Ordinal.Equals(
                        caSerialNumber,
                        snapshot.State.OtherAuthority.CaSerialNumber.Hex))
                {
                    crl = snapshot.OtherCrlDer;
                }
                else
                {
                    return null;
                }

                if (crl.Length > ExternalApiContract.MaximumCrlResponseBytes)
                {
                    throw new InvalidDataException(
                        "The issuer CRL exceeds the External response limit.");
                }

                return (byte[])crl.Clone();
            }
        }

        internal byte[] GetExternalCertificateRevocationList(
            StateMutationCoordinator directoryState,
            Guid issuerInstanceId,
            DateTime utcNow)
        {
            ThrowIfDisposed();
            EnsureProvisioned();
            _store.PublishDueScheduledRetirements(
                directoryState,
                issuerInstanceId,
                utcNow);
            return GetExternalCertificateRevocationList();
        }

        internal ExternalRegistrationServiceResult RegisterExternalService(
            ExternalRegistrationRequest request,
            StateMutationCoordinator directoryState,
            RegistrationModeOwner registrationModeOwner,
            DirectoryEndpointIdentity directoryIdentity,
            Guid issuerInstanceId,
            DateTime utcNow)
        {
            ThrowIfDisposed();
            EnsureProvisioned();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            ServiceEndpointIdentity serviceIdentity;
            EndpointIdentityValidationError identityError;
            if (!ServiceEndpointIdentity.TryCreate(
                    request.ServiceHostName,
                    request.ServiceIpv4Address,
                    out serviceIdentity,
                    out identityError))
            {
                return ExternalRegistrationServiceResult.Failure(
                    ExternalRegistrationServiceStatus
                        .CertificateRequestInvalid);
            }

            ServiceDefinition serviceDefinition;
            ServiceDefinitionValidationError definitionError;
            if (!ServiceDefinition.TryCreate(
                    request.Name,
                    request.ProductCode,
                    serviceIdentity,
                    request.Port,
                    out serviceDefinition,
                    out definitionError))
            {
                return ExternalRegistrationServiceResult.Failure(
                    ExternalRegistrationServiceStatus
                        .CertificateRequestInvalid);
            }

            byte[] certificateSigningRequest =
                request.CertificateSigningRequest;
            try
            {
                ValidatedCertificateSigningRequest validatedRequest;
                CertificateSigningRequestValidationError csrError;
                if (!CertificateSigningRequestValidator.TryValidate(
                        certificateSigningRequest,
                        serviceIdentity,
                        out validatedRequest,
                        out csrError))
                {
                    return ExternalRegistrationServiceResult.Failure(
                        ExternalRegistrationServiceStatus
                            .CertificateRequestInvalid);
                }

                CertificateIssuanceRequestEvidence evidence =
                    CertificateIssuanceRequestEvidence.CreateRegistration(
                        request.RegistrationRequestId,
                        serviceDefinition,
                        certificateSigningRequest);
                return _store.RegisterService(
                    directoryState,
                    registrationModeOwner,
                    directoryIdentity,
                    issuerInstanceId,
                    serviceDefinition,
                    validatedRequest,
                    evidence,
                    utcNow);
            }
            finally
            {
                Array.Clear(
                    certificateSigningRequest,
                    0,
                    certificateSigningRequest.Length);
            }
        }

        internal ExternalRegistrationServiceResult RenewExternalService(
            ExternalCertificateRenewalRequest request,
            StateMutationCoordinator directoryState,
            DirectoryEndpointIdentity directoryIdentity,
            Guid issuerInstanceId,
            DateTime utcNow)
        {
            ThrowIfDisposed();
            EnsureProvisioned();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            ServiceEndpointIdentity serviceIdentity;
            EndpointIdentityValidationError identityError;
            if (!ServiceEndpointIdentity.TryCreate(
                    request.ServiceHostName,
                    request.ServiceIpv4Address,
                    out serviceIdentity,
                    out identityError))
            {
                return ExternalRegistrationServiceResult.Failure(
                    ExternalRegistrationServiceStatus
                        .CertificateRequestInvalid);
            }

            ServiceDefinition serviceDefinition;
            ServiceDefinitionValidationError definitionError;
            if (!ServiceDefinition.TryCreate(
                    request.Name,
                    request.ProductCode,
                    serviceIdentity,
                    request.Port,
                    out serviceDefinition,
                    out definitionError))
            {
                return ExternalRegistrationServiceResult.Failure(
                    ExternalRegistrationServiceStatus
                        .CertificateRequestInvalid);
            }

            CertificateSerialNumber currentSerialNumber;
            if (!CertificateSerialNumber.TryCreate(
                    request.CurrentSerialNumber,
                    out currentSerialNumber))
            {
                return ExternalRegistrationServiceResult.Failure(
                    ExternalRegistrationServiceStatus
                        .CertificateNotRenewable);
            }

            byte[] certificateSigningRequest =
                request.CertificateSigningRequest;
            try
            {
                ValidatedCertificateSigningRequest validatedRequest;
                CertificateSigningRequestValidationError csrError;
                if (!CertificateSigningRequestValidator.TryValidate(
                        certificateSigningRequest,
                        serviceIdentity,
                        out validatedRequest,
                        out csrError))
                {
                    return ExternalRegistrationServiceResult.Failure(
                        ExternalRegistrationServiceStatus
                            .CertificateRequestInvalid);
                }

                CertificateIssuanceRequestEvidence evidence =
                    CertificateIssuanceRequestEvidence.CreateRenewal(
                        request.RenewalRequestId,
                        currentSerialNumber,
                        serviceDefinition,
                        certificateSigningRequest);
                return _store.RenewService(
                    directoryState,
                    directoryIdentity,
                    issuerInstanceId,
                    serviceDefinition,
                    validatedRequest,
                    evidence,
                    request,
                    utcNow);
            }
            finally
            {
                Array.Clear(
                    certificateSigningRequest,
                    0,
                    certificateSigningRequest.Length);
            }
        }

        internal CertificateServiceDeletionResult DeleteService(
            StateMutationCoordinator directoryState,
            Guid issuerInstanceId,
            ProductCode productCode,
            DateTime utcNow)
        {
            ThrowIfDisposed();
            EnsureProvisioned();
            return _store.DeleteService(
                directoryState,
                issuerInstanceId,
                productCode,
                utcNow);
        }

        private void EnsureProvisioned()
        {
            if (!_provisioned)
            {
                throw new InvalidOperationException(
                    "PKI state is not provisioned.");
            }
        }
    }
}
