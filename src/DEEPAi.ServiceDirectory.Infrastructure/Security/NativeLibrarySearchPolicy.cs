using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DEEPAi.ServiceDirectory.Infrastructure.Security
{
    public static class NativeLibrarySearchPolicy
    {
        private const uint LoadLibrarySearchApplicationDir = 0x00000200;
        private const uint LoadLibrarySearchSystem32 = 0x00000800;
        private const uint AllowedSearchDirectories =
            LoadLibrarySearchApplicationDir |
            LoadLibrarySearchSystem32;

        private static readonly object Synchronization = new object();
        private static bool _applied;

        public static void Apply()
        {
            lock (Synchronization)
            {
                if (_applied)
                {
                    return;
                }

                if (!SetDefaultDllDirectories(AllowedSearchDirectories))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The native-library search policy could not be applied.");
                }

                _applied = true;
            }
        }

        [DllImport(
            "kernel32.dll",
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDefaultDllDirectories(uint directoryFlags);
    }
}
