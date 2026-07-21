using System;
using System.Collections.Generic;
using System.Linq;
using DEEPAi.ServiceDirectory.Application.State;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.Domain.Certificates;
using Org.BouncyCastle.Security;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal sealed partial class CertificateAuthorityStore
    {
        private bool PublishDueScheduledRetirementsCore(
            StateMutationCoordinator directoryState,
            Guid issuerInstanceId,
            DateTime utcNow)
        {
            DirectorySnapshot directory;
            if (!directoryState.TryGetReadySnapshot(out directory))
            {
                throw new InvalidOperationException(
                    "Directory state is unavailable for scheduled certificate retirement.");
            }

            using (CertificateAuthorityStoreSnapshot snapshot = GetCurrent())
            {
                if (!IsReadyActiveIssuer(snapshot, issuerInstanceId))
                {
                    throw new InvalidOperationException(
                        "The active issuer is unavailable for scheduled certificate retirement.");
                }

                CertificateLedgerEntry[] due = snapshot.Ledger
                    .EntriesBySerial.Values
                    .Where(entry => entry.Status
                            == CertificateLedgerStatus.Retiring
                        && entry.ScheduledRevocationUtc.HasValue
                        && entry.ScheduledRevocationUtc.Value <= utcNow)
                    .ToArray();
                if (due.Length == 0)
                {
                    return false;
                }

                if (snapshot.State.PkiRevision == ulong.MaxValue
                    || snapshot.State.CrlNumber == ulong.MaxValue)
                {
                    throw new OverflowException(
                        "Certificate retirement high-water values are exhausted.");
                }

                var dueSerials = new HashSet<CertificateSerialNumber>(
                    due.Select(entry => entry.SerialNumber));
                CertificateLedgerEntry[] entries = snapshot.Ledger
                    .EntriesBySerial.Values
                    .Select(entry => dueSerials.Contains(entry.SerialNumber)
                        ? entry.Revoke(
                            entry.ScheduledRevocationUtc.Value,
                            CertificateRevocationReason.Superseded)
                        : entry)
                    .ToArray();
                ulong nextPkiRevision =
                    snapshot.State.PkiRevision + 1;
                ulong nextCrlNumber = snapshot.State.CrlNumber + 1;
                var nextLedger = new CertificateLedgerSnapshot(
                    entries,
                    nextPkiRevision,
                    nextCrlNumber);
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
                            CertificateServiceMutationOperation
                                .ScheduledRetirement,
                            directory,
                            directory,
                            nextState,
                            nextLedger,
                            nextCrlDer,
                            utcNow))
                    {
                    }

                    return true;
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
