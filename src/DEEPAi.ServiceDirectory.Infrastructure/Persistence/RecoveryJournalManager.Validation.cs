using System;
using System.Collections.Generic;
using System.IO;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal sealed partial class RecoveryJournalManager
    {
        private static void ValidateChanges(
            IReadOnlyList<StateFileChange> changes)
        {
            if (changes.Count == 0
                || changes.Count > StateFileTargets.All.Count)
            {
                throw new ArgumentException(
                    "A transaction must contain one to four state file changes.",
                    nameof(changes));
            }

            int previousOrder = -1;
            foreach (StateFileChange change in changes)
            {
                if (change == null
                    || (int)change.Target <= previousOrder)
                {
                    throw new ArgumentException(
                        "State file changes must be unique and canonically ordered.",
                        nameof(changes));
                }

                previousOrder = (int)change.Target;
                if ((change.BeforeBytes != null
                        && change.BeforeBytes.Length > MaximumImageBytes)
                    || (change.AfterBytes != null
                        && change.AfterBytes.Length > MaximumImageBytes))
                {
                    throw new InvalidDataException(
                        "A state file change exceeds the recovery image size limit.");
                }
            }
        }

        private static bool IsCanonicalSuffixedDirectoryName(
            string value,
            string suffix)
        {
            if (value == null
                || !value.EndsWith(
                    suffix,
                    StringComparison.Ordinal))
            {
                return false;
            }

            string transactionIdText = value.Substring(
                0,
                value.Length - suffix.Length);
            Guid transactionId;
            return Guid.TryParseExact(
                    transactionIdText,
                    "D",
                    out transactionId)
                && StringComparer.Ordinal.Equals(
                    transactionIdText,
                    transactionId.ToString("D"));
        }

        private static bool IsCanonicalManifestTemporaryFileName(
            string value)
        {
            const string prefix = "journal.";
            const string suffix = ".tmp";
            if (value == null
                || value.Length != prefix.Length + 32 + suffix.Length
                || !value.StartsWith(prefix, StringComparison.Ordinal)
                || !value.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }

            string transactionIdText = value.Substring(
                prefix.Length,
                value.Length - prefix.Length - suffix.Length);
            Guid transactionId;
            return Guid.TryParseExact(
                    transactionIdText,
                    "N",
                    out transactionId)
                && transactionId != Guid.Empty
                && StringComparer.Ordinal.Equals(
                    transactionIdText,
                    transactionId.ToString("N"));
        }

        private static bool ImageMatches(
            bool actualExists,
            byte[] actualBytes,
            bool expectedExists,
            byte[] expectedBytes)
        {
            return actualExists == expectedExists
                && (!actualExists
                    || ByteArraysEqual(actualBytes, expectedBytes));
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (left == null
                || right == null
                || left.Length != right.Length)
            {
                return false;
            }

            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class PreflightEntry
        {
            internal PreflightEntry(
                RecoveryJournalEntry entry,
                byte[] beforeBytes,
                byte[] afterBytes,
                bool currentExists,
                byte[] currentBytes)
            {
                Entry = entry;
                BeforeBytes = beforeBytes;
                AfterBytes = afterBytes;
                CurrentExists = currentExists;
                CurrentBytes = currentBytes;
            }

            internal RecoveryJournalEntry Entry { get; }

            internal byte[] BeforeBytes { get; }

            internal byte[] AfterBytes { get; }

            private bool CurrentExists { get; }

            private byte[] CurrentBytes { get; }

            internal bool CurrentMatches(
                bool expectedExists,
                byte[] expectedBytes)
            {
                return ImageMatches(
                    CurrentExists,
                    CurrentBytes,
                    expectedExists,
                    expectedBytes);
            }
        }

        private sealed class PreflightTransaction
        {
            internal PreflightTransaction(
                RecoveryJournalState state,
                IReadOnlyList<PreflightEntry> entries)
            {
                State = state;
                Entries = entries;
            }

            internal RecoveryJournalState State { get; }

            internal IReadOnlyList<PreflightEntry> Entries { get; }
        }
    }
}
