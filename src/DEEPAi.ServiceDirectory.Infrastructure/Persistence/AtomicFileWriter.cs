using System;
using System.Collections.Generic;
using System.IO;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal sealed class AtomicFileWriter
    {
        private const string BackupSuffix = ".bak";
        private static readonly ISet<string> AllowedFileNames = new HashSet<string>(
            new[] { "config.xml", "directory.xml", "pending.xml" },
            StringComparer.OrdinalIgnoreCase);
        private readonly string _stateDirectoryPath;

        public AtomicFileWriter(string stateDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(stateDirectoryPath))
            {
                throw new ArgumentException("State directory path is required.", nameof(stateDirectoryPath));
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
                throw new NotSupportedException("State files must be stored on a local path.");
            }

            var drive = new DriveInfo(Path.GetPathRoot(fullPath));
            if (drive.DriveType == DriveType.Network)
            {
                throw new NotSupportedException(
                    "State files must not be stored on a mapped network drive.");
            }

            _stateDirectoryPath = fullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string volumeRoot = Path.GetPathRoot(fullPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (StringComparer.OrdinalIgnoreCase.Equals(_stateDirectoryPath, volumeRoot))
            {
                throw new ArgumentException("A volume root cannot be used as the state directory.", nameof(stateDirectoryPath));
            }

            EnsureStateDirectoryIsSafe();
        }

        public void Write(string fileName, byte[] contents)
        {
            if (!AllowedFileNames.Contains(fileName))
            {
                throw new ArgumentException(
                    "File name is not an approved service directory state document.",
                    nameof(fileName));
            }

            if (contents == null)
            {
                throw new ArgumentNullException(nameof(contents));
            }

            EnsureStateDirectoryIsSafe();
            string fullDestinationPath = Path.Combine(_stateDirectoryPath, fileName);
            string fullBackupPath = fullDestinationPath + BackupSuffix;
            EnsureExistingFileIsNotReparsePoint(fullDestinationPath);
            EnsureExistingFileIsNotReparsePoint(fullBackupPath);
            string temporaryPath = Path.Combine(
                _stateDirectoryPath,
                Path.GetFileName(fullDestinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            bool temporaryFileCreated = false;
            bool writeCompleted = false;
            Exception writeFailure = null;
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    temporaryFileCreated = true;
                    stream.Write(contents, 0, contents.Length);
                    stream.Flush(true);
                }

                if (File.Exists(fullDestinationPath))
                {
                    File.Replace(temporaryPath, fullDestinationPath, fullBackupPath, false);
                }
                else
                {
                    File.Move(temporaryPath, fullDestinationPath);
                }

                writeCompleted = true;
            }
            catch (Exception exception)
            {
                writeFailure = exception;
                throw;
            }
            finally
            {
                if (!writeCompleted && temporaryFileCreated)
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (Exception cleanupFailure)
                    {
                        if (writeFailure != null)
                        {
                            throw new AggregateException(
                                "The state write and temporary file cleanup both failed.",
                                writeFailure,
                                cleanupFailure);
                        }

                        throw;
                    }
                }
            }
        }

        private void EnsureStateDirectoryIsSafe()
        {
            var directory = new DirectoryInfo(_stateDirectoryPath);
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

        private static bool IsFullyQualifiedLocalPath(string path)
        {
            return path.Length >= 3
                && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z'))
                && path[1] == Path.VolumeSeparatorChar
                && (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar);
        }

        private static void EnsureExistingFileIsNotReparsePoint(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("State files and backups must not be reparse points.");
            }
        }
    }
}
