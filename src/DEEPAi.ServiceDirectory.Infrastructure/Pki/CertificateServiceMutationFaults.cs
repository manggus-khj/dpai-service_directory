using DEEPAi.ServiceDirectory.Domain.Certificates;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal enum CertificateServiceMutationFaultPoint
    {
        BeforeRegistrationClaim = 0,
        SerialReserved = 1,
        CertificateSigned = 2,
        ResponseStarting = 3
    }

    internal interface ICertificateServiceMutationFaultInjector
    {
        void OnFault(
            CertificateServiceMutationFaultPoint faultPoint,
            CertificateServiceMutationOperation operation,
            CertificateSerialNumber? serialNumber);
    }

    internal sealed class NoOpCertificateServiceMutationFaultInjector
        : ICertificateServiceMutationFaultInjector
    {
        internal static readonly
            NoOpCertificateServiceMutationFaultInjector Instance =
                new NoOpCertificateServiceMutationFaultInjector();

        private NoOpCertificateServiceMutationFaultInjector()
        {
        }

        public void OnFault(
            CertificateServiceMutationFaultPoint faultPoint,
            CertificateServiceMutationOperation operation,
            CertificateSerialNumber? serialNumber)
        {
        }
    }
}
