using System;
using System.IO;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal sealed class StateStoragePathPolicy
    {
        private const string JournalDirectoryName = "journal";
        private readonly string _stateDirectoryPathWithSeparator;

        internal StateStoragePathPolicy(string stateDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(stateDirectoryPath))
            {
                throw new ArgumentException(
                    "State directory path is required.",
                    nameof(stateDirectoryPath));
            }

            if (!IsFullyQualifiedLocalPath(stateDirectoryPath))
            {
                throw new ArgumentException(
                    "State directory path must be a fully qualified local drive path.",
                    nameof(stateDirectoryPath));
            }

            string fullPath = Path.GetFullPath(stateDirectoryPath);
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                || fullPath.StartsWith("//", StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    "State files must be stored on a local path.");
            }

            var drive = new DriveInfo(Path.GetPathRoot(fullPath));
            if (drive.DriveType == DriveType.Network)
            {
                throw new NotSupportedException(
                    "State files must not be stored on a mapped network drive.");
            }

            StateDirectoryPath = fullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string volumeRoot = Path.GetPathRoot(fullPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (StringComparer.OrdinalIgnoreCase.Equals(
                StateDirectoryPath,
                volumeRoot))
            {
                throw new ArgumentException(
                    "A volume root cannot be used as the state directory.",
                    nameof(stateDirectoryPath));
            }

            _stateDirectoryPathWithSeparator =
                StateDirectoryPath + Path.DirectorySeparatorChar;
            JournalRootPath = Path.Combine(
                StateDirectoryPath,
                JournalDirectoryName);
            EnsureStateDirectoryIsSafe();
        }

        internal string StateDirectoryPath { get; }

        internal string JournalRootPath { get; }

        internal void EnsureStateDirectoryIsSafe()
        {
            var directory = new DirectoryInfo(StateDirectoryPath);
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException(
                    "The installer-provisioned service directory state path does not exist.");
            }

            for (DirectoryInfo current = directory;
                current != null;
                current = current.Parent)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "The service directory state path and its parents must not be reparse points.");
                }
            }
        }

        internal void EnsureJournalRootExistsAndIsSafe()
        {
            EnsureStateDirectoryIsSafe();
            FileAttributes attributes;
            if (TryGetAttributes(JournalRootPath, out attributes))
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "The recovery journal root must not be a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) == 0)
                {
                    throw new IOException(
                        "The recovery journal root must be a directory.");
                }
            }
            else
            {
                Directory.CreateDirectory(JournalRootPath);
            }

            EnsureDirectoryIsSafe(JournalRootPath);
        }

        internal string GetTargetPath(StateFileTarget target)
        {
            StateFileTargetDescriptor descriptor = StateFileTargets.Get(target);
            string path = Path.GetFullPath(Path.Combine(
                StateDirectoryPath,
                descriptor.RelativePath));
            EnsurePathIsInsideStateDirectory(path);
            return path;
        }

        internal string GetBackupPath(StateFileTarget target)
        {
            return GetTargetPath(target) + ".bak";
        }

        internal void EnsureForbiddenLegacyStateIsAbsent()
        {
            string pendingPath = Path.Combine(
                StateDirectoryPath,
                "pending.xml");
            string pendingBackupPath = pendingPath + ".bak";
            EnsureExistingFileIsSafe(pendingPath);
            EnsureExistingFileIsSafe(pendingBackupPath);
            if (File.Exists(pendingPath)
                || File.Exists(pendingBackupPath))
            {
                throw new InvalidDataException(
                    "The target v1 state must not contain pending.xml artifacts.");
            }
        }

        internal void EnsureTargetParentIsSafe(StateFileTarget target)
        {
            EnsureStateDirectoryIsSafe();
            string parentPath = Path.GetDirectoryName(GetTargetPath(target));
            if (!Directory.Exists(parentPath))
            {
                throw new DirectoryNotFoundException(
                    "The installer-provisioned state target directory does not exist.");
            }

            EnsureDirectoryIsSafe(parentPath);
        }

        internal void EnsureDirectoryIsSafe(string directoryPath)
        {
            string fullPath = Path.GetFullPath(directoryPath);
            EnsurePathIsInsideStateDirectory(fullPath);
            var directory = new DirectoryInfo(fullPath);
            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException(
                    "The required state directory does not exist.");
            }

            for (DirectoryInfo current = directory;
                current != null;
                current = current.Parent)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "State directories must not be reparse points.");
                }

                if (StringComparer.OrdinalIgnoreCase.Equals(
                    current.FullName.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    StateDirectoryPath))
                {
                    return;
                }
            }

            throw new IOException(
                "The state directory escaped the configured state root.");
        }

        internal void EnsureExistingFileIsSafe(string filePath)
        {
            string fullPath = Path.GetFullPath(filePath);
            EnsurePathIsInsideStateDirectory(fullPath);
            FileAttributes attributes;
            if (!TryGetAttributes(fullPath, out attributes))
            {
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "State files, backups, journals and images must not be reparse points.");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new IOException(
                    "A state file path must not resolve to a directory.");
            }
        }

        private static bool TryGetAttributes(
            string path,
            out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                attributes = default(FileAttributes);
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                attributes = default(FileAttributes);
                return false;
            }
        }

        internal void EnsurePathIsInsideStateDirectory(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(
                    _stateDirectoryPathWithSeparator,
                    StringComparison.OrdinalIgnoreCase)
                && !StringComparer.OrdinalIgnoreCase.Equals(
                    fullPath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    StateDirectoryPath))
            {
                throw new IOException(
                    "A state path escaped the configured state root.");
            }
        }

        private static bool IsFullyQualifiedLocalPath(string path)
        {
            return path.Length >= 3
                && ((path[0] >= 'A' && path[0] <= 'Z')
                    || (path[0] >= 'a' && path[0] <= 'z'))
                && path[1] == Path.VolumeSeparatorChar
                && (path[2] == Path.DirectorySeparatorChar
                    || path[2] == Path.AltDirectorySeparatorChar);
        }
    }
}
