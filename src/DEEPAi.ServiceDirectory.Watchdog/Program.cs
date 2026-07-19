using System.ServiceProcess;
using DEEPAi.ServiceDirectory.Infrastructure.Security;

namespace DEEPAi.ServiceDirectory.Watchdog
{
    internal static class Program
    {
        private static void Main()
        {
            NativeLibrarySearchPolicy.Apply();

            ServiceBase.Run(
                new ServiceBase[]
                {
                    new WatchdogWindowsService()
                });
        }
    }
}
