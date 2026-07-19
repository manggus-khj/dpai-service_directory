using System;
using System.ServiceProcess;
using DEEPAi.ServiceDirectory.Infrastructure.Security;

namespace DEEPAi.ServiceDirectory.Service
{
    internal static class Program
    {
        private static void Main()
        {
            NativeLibrarySearchPolicy.Apply();

            IServiceDirectoryRuntimeFactory runtimeFactory =
                ProgramRuntimeFactory.Create();
            ServiceBase.Run(
                new ServiceBase[]
                {
                    new ServiceDirectoryWindowsService(runtimeFactory)
                });
        }
    }

    internal static class ProgramRuntimeFactory
    {
        internal static IServiceDirectoryRuntimeFactory Create()
        {
            return new ServiceDirectoryRuntimeFactory(
                new ServiceDirectoryApplicationFactory());
        }
    }
}
