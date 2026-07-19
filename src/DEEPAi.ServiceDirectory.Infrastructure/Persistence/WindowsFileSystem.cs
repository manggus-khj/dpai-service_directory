using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal static class WindowsFileSystem
    {
        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileWriteThrough = 0x8;

        internal static void MoveWriteThrough(
            string sourcePath,
            string destinationPath,
            bool replaceExisting)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException(
                    "A source path is required.",
                    nameof(sourcePath));
            }

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                throw new ArgumentException(
                    "A destination path is required.",
                    nameof(destinationPath));
            }

            string fullSourcePath = Path.GetFullPath(sourcePath);
            string fullDestinationPath = Path.GetFullPath(destinationPath);
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetPathRoot(fullSourcePath),
                Path.GetPathRoot(fullDestinationPath)))
            {
                throw new IOException(
                    "A write-through state move must stay on one volume.");
            }

            int flags = MoveFileWriteThrough;
            if (replaceExisting)
            {
                flags |= MoveFileReplaceExisting;
            }

            if (!MoveFileEx(
                    fullSourcePath,
                    fullDestinationPath,
                    flags))
            {
                int hresult = Marshal.GetHRForLastWin32Error();
                throw new IOException(
                    "The write-through state move failed.",
                    hresult);
            }

            if (File.Exists(fullSourcePath)
                || Directory.Exists(fullSourcePath)
                || (!File.Exists(fullDestinationPath)
                    && !Directory.Exists(fullDestinationPath)))
            {
                throw new IOException(
                    "The write-through state move did not produce the expected paths.");
            }
        }

        [DllImport(
            "kernel32.dll",
            EntryPoint = "MoveFileExW",
            CharSet = CharSet.Unicode,
            SetLastError = true,
            BestFitMapping = false,
            ThrowOnUnmappableChar = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            int flags);
    }
}
