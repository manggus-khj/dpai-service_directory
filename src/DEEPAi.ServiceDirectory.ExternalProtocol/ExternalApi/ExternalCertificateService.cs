using System;

namespace DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi
{
    internal enum ExternalRegistrationServiceStatus
    {
        Registered = 1,
        Reregistered = 2,
        Replayed = 3,
        RegistrationModeClosed = 4,
        Conflict = 5,
        CertificateRequestInvalid = 6,
        LimitExceeded = 7,
        Renewed = 8,
        CertificateNotRenewable = 9,
        InvalidCertificateProof = 10
    }

    internal sealed class ExternalRegistrationServiceResult
    {
        private ExternalRegistrationServiceResult(
            ExternalRegistrationServiceStatus status,
            ExternalServiceItem service,
            ExternalIssuedCertificate certificate)
        {
            bool success = status == ExternalRegistrationServiceStatus.Registered
                || status == ExternalRegistrationServiceStatus.Reregistered
                || status == ExternalRegistrationServiceStatus.Replayed
                || status == ExternalRegistrationServiceStatus.Renewed;
            if (!Enum.IsDefined(
                    typeof(ExternalRegistrationServiceStatus),
                    status)
                || success != (service != null)
                || success != (certificate != null))
            {
                throw new ArgumentException(
                    "The External registration service result is inconsistent.");
            }

            Status = status;
            Service = service;
            Certificate = certificate;
        }

        internal ExternalRegistrationServiceStatus Status { get; }

        internal ExternalServiceItem Service { get; }

        internal ExternalIssuedCertificate Certificate { get; }

        internal static ExternalRegistrationServiceResult Success(
            ExternalRegistrationServiceStatus status,
            ExternalServiceItem service,
            ExternalIssuedCertificate certificate)
        {
            return new ExternalRegistrationServiceResult(
                status,
                service,
                certificate);
        }

        internal static ExternalRegistrationServiceResult Failure(
            ExternalRegistrationServiceStatus status)
        {
            return new ExternalRegistrationServiceResult(
                status,
                null,
                null);
        }
    }

    internal interface IExternalCertificateService
    {
        ExternalTrustInfo GetTrustInfo();

        byte[] GetCertificateRevocationList();

        ExternalRegistrationServiceResult Register(
            ExternalRegistrationRequest request,
            DateTime utcNow);

        ExternalRegistrationServiceResult Renew(
            ExternalCertificateRenewalRequest request,
            DateTime utcNow);
    }
}
