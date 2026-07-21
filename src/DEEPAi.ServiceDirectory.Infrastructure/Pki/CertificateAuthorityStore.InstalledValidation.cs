using System;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed partial class CertificateAuthorityStore
    {
        internal static void ValidateInstalledStateFiles(
            StateStoragePathPolicy pathPolicy,
            IPeerSecretAccessPolicy accessPolicy)
        {
            string pkiDirectory = System.IO.Path.Combine(
                pathPolicy.StateDirectoryPath,
                "pki");
            if (System.IO.Directory.Exists(pkiDirectory))
            {
                var writer = new AtomicFileWriter(pathPolicy, accessPolicy);
                if (writer.Exists(StateFileTarget.PkiMetadata))
                {
                    byte[] metadata = writer.Read(
                        StateFileTarget.PkiMetadata,
                        CertificateAuthorityStateCodec.MaximumDocumentBytes);
                    try
                    {
                        CertificateAuthorityState state =
                            new CertificateAuthorityStateCodec()
                                .DeserializeState(metadata);
                        if (state.Role == CertificateAuthorityRole.Standby)
                        {
                            new PeerPkiSynchronizationStore(
                                pathPolicy,
                                new StateMutationGate(),
                                NoOpRecoveryJournalFaultInjector.Instance)
                                .ValidateInstalledStandbyFiles(DateTime.UtcNow);
                            return;
                        }
                    }
                    finally
                    {
                        Clear(metadata);
                    }
                }
            }

            var store = new CertificateAuthorityStore(
                pathPolicy,
                new StateMutationGate(),
                new DpapiMachineCaPrivateKeyProtector(),
                accessPolicy,
                NoOpRecoveryJournalFaultInjector.Instance);
            try
            {
                using (CertificateAuthorityStoreSnapshot state =
                    store.ReadCurrent(true))
                {
                }
            }
            finally
            {
                store.Dispose();
            }
        }
    }
}
