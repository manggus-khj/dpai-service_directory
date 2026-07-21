using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal enum StateFileTarget
    {
        Directory = 0,
        Config = 1,
        PeerSecret = 2,
        PkiMetadata = 3,
        CertificateLedger = 4,
        PeerPkiCache = 5,
        CertificateRevocationList = 6,
        CaCertificate = 7,
        CaPrivateKey = 8,
        CertificateRevocationListB = 9,
        CaCertificateB = 10,
        CaPrivateKeyB = 11,
        RetiredAuthorities = 12
    }

    internal sealed class StateFileTargetDescriptor
    {
        internal StateFileTargetDescriptor(
            StateFileTarget target,
            string journalName,
            string relativePath,
            string beforeImageFileName,
            string afterImageFileName,
            string primaryDiscardFileName,
            string backupDiscardFileName)
        {
            Target = target;
            JournalName = journalName;
            RelativePath = relativePath;
            BeforeImageFileName = beforeImageFileName;
            AfterImageFileName = afterImageFileName;
            PrimaryDiscardFileName = primaryDiscardFileName;
            BackupDiscardFileName = backupDiscardFileName;
        }

        internal StateFileTarget Target { get; }

        internal string JournalName { get; }

        internal string RelativePath { get; }

        internal string BeforeImageFileName { get; }

        internal string AfterImageFileName { get; }

        internal string PrimaryDiscardFileName { get; }

        internal string BackupDiscardFileName { get; }
    }

    internal static class StateFileTargets
    {
        private static readonly IReadOnlyList<StateFileTargetDescriptor> Descriptors =
            new ReadOnlyCollection<StateFileTargetDescriptor>(
                new[]
                {
                    new StateFileTargetDescriptor(
                        StateFileTarget.Directory,
                        "Directory",
                        "directory.xml",
                        "directory.before.bin",
                        "directory.after.bin",
                        "directory.primary.discard.bin",
                        "directory.backup.discard.bin"),
                    new StateFileTargetDescriptor(
                        StateFileTarget.Config,
                        "Config",
                        "config.xml",
                        "config.before.bin",
                        "config.after.bin",
                        "config.primary.discard.bin",
                        "config.backup.discard.bin"),
                    new StateFileTargetDescriptor(
                        StateFileTarget.PeerSecret,
                        "PeerSecret",
                        @"secrets\peer.dat",
                        "peer.before.bin",
                        "peer.after.bin",
                        "peer.primary.discard.bin",
                        "peer.backup.discard.bin"),
                    new StateFileTargetDescriptor(
                        StateFileTarget.PkiMetadata,
                        "PkiMetadata",
                        @"pki\state.xml",
                        "pki-state.before.bin",
                        "pki-state.after.bin",
                        "pki-state.primary.discard.bin",
                        "pki-state.backup.discard.bin"),
                    new StateFileTargetDescriptor(
                        StateFileTarget.CertificateLedger,
                        "CertificateLedger",
                        @"pki\ledger.xml",
                        "ledger.before.bin",
                        "ledger.after.bin",
                        "ledger.primary.discard.bin",
                        "ledger.backup.discard.bin"),
                    new StateFileTargetDescriptor(
                        StateFileTarget.PeerPkiCache,
                        "PeerPkiCache",
                        @"pki\peer-cache.xml",
                        "peer-pki.before.bin",
                        "peer-pki.after.bin",
                        "peer-pki.primary.discard.bin",
                        "peer-pki.backup.discard.bin"),
                    new StateFileTargetDescriptor(
                        StateFileTarget.CertificateRevocationList,
                        "CertificateRevocationListA",
                        @"pki\crl-a.der",
                        "crl-a.before.bin",
                        "crl-a.after.bin",
                        "crl-a.primary.discard.bin",
                        "crl-a.backup.discard.bin"),
                    new StateFileTargetDescriptor(
                        StateFileTarget.CaCertificate,
                        "CaCertificateA",
                        @"pki\ca-a.der",
                        "ca-a.before.bin",
                        "ca-a.after.bin",
                        "ca-a.primary.discard.bin",
                        "ca-a.backup.discard.bin"),
                    new StateFileTargetDescriptor(
                        StateFileTarget.CaPrivateKey,
                        "CaPrivateKeyA",
                        @"secrets\ca-a.key",
                        "ca-a-key.before.bin",
                        "ca-a-key.after.bin",
                        "ca-a-key.primary.discard.bin",
                        "ca-a-key.backup.discard.bin"),
                    new StateFileTargetDescriptor(
                        StateFileTarget.CertificateRevocationListB,
                        "CertificateRevocationListB",
                        @"pki\crl-b.der",
                        "crl-b.before.bin",
                        "crl-b.after.bin",
                        "crl-b.primary.discard.bin",
                        "crl-b.backup.discard.bin"),
                    new StateFileTargetDescriptor(
                        StateFileTarget.CaCertificateB,
                        "CaCertificateB",
                        @"pki\ca-b.der",
                        "ca-b.before.bin",
                        "ca-b.after.bin",
                        "ca-b.primary.discard.bin",
                        "ca-b.backup.discard.bin"),
                    new StateFileTargetDescriptor(
                        StateFileTarget.CaPrivateKeyB,
                        "CaPrivateKeyB",
                        @"secrets\ca-b.key",
                        "ca-b-key.before.bin",
                        "ca-b-key.after.bin",
                        "ca-b-key.primary.discard.bin",
                        "ca-b-key.backup.discard.bin"),
                    new StateFileTargetDescriptor(
                        StateFileTarget.RetiredAuthorities,
                        "RetiredAuthorities",
                        @"pki\retired-authorities.xml",
                        "retired-authorities.before.bin",
                        "retired-authorities.after.bin",
                        "retired-authorities.primary.discard.bin",
                        "retired-authorities.backup.discard.bin")
                });

        internal static IReadOnlyList<StateFileTargetDescriptor> All => Descriptors;

        internal static StateFileTargetDescriptor Get(StateFileTarget target)
        {
            int index = (int)target;
            if (index < 0
                || index >= Descriptors.Count
                || Descriptors[index].Target != target)
            {
                throw new ArgumentOutOfRangeException(nameof(target));
            }

            return Descriptors[index];
        }

        internal static bool IsSecret(StateFileTarget target)
        {
            return target == StateFileTarget.PeerSecret
                || target == StateFileTarget.CaPrivateKey
                || target == StateFileTarget.CaPrivateKeyB;
        }

        internal static bool TryParseJournalName(
            string value,
            out StateFileTarget target)
        {
            foreach (StateFileTargetDescriptor descriptor in Descriptors)
            {
                if (StringComparer.Ordinal.Equals(descriptor.JournalName, value))
                {
                    target = descriptor.Target;
                    return true;
                }
            }

            target = default(StateFileTarget);
            return false;
        }

        internal static bool TryParseXmlFileName(
            string value,
            out StateFileTarget target)
        {
            foreach (StateFileTargetDescriptor descriptor in Descriptors)
            {
                if (IsSecret(descriptor.Target)
                    || !descriptor.RelativePath.EndsWith(
                        ".xml",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (StringComparer.Ordinal.Equals(descriptor.RelativePath, value))
                {
                    target = descriptor.Target;
                    return true;
                }
            }

            target = default(StateFileTarget);
            return false;
        }
    }
}
