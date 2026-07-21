using DEEPAi.ServiceDirectory.Domain.Certificates;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed partial class CertificateAuthorityStore
    {
        internal static void ValidateLedgerCertificates(
            CertificateLedgerSnapshot ledger,
            byte[] caCertificateDer)
        {
            foreach (CertificateLedgerEntry entry in
                ledger.EntriesBySerial.Values)
            {
                byte[] leafCertificate = entry.GetLeafCertificate();
                try
                {
                    SiteCertificateAuthority
                        .ValidateStoredServiceCertificate(
                            leafCertificate,
                            caCertificateDer,
                            entry);
                }
                finally
                {
                    Clear(leafCertificate);
                }
            }
        }
    }
}
