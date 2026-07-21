using System;
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
