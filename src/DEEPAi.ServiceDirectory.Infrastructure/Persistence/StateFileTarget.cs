using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal enum StateFileTarget
    {
        Directory = 0,
        Pending = 1,
        Config = 2,
        PeerSecret = 3
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
                        StateFileTarget.Pending,
                        "Pending",
                        "pending.xml",
                        "pending.before.bin",
                        "pending.after.bin",
                        "pending.primary.discard.bin",
                        "pending.backup.discard.bin"),
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
                        "peer.backup.discard.bin")
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
                if (descriptor.Target == StateFileTarget.PeerSecret)
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
