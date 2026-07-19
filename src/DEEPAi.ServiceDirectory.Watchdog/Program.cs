using System.ServiceProcess;

namespace DEEPAi.ServiceDirectory.Watchdog
{
    internal static class Program
    {
        private static void Main()
        {
            ServiceBase.Run(
                new ServiceBase[]
                {
                    new WatchdogWindowsService()
                });
        }
    }
}
