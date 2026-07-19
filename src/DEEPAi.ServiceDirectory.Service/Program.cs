using System;
using System.ServiceProcess;
using DEEPAi.ServiceDirectory.Infrastructure.Security;

namespace DEEPAi.ServiceDirectory.Service
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            NativeLibrarySearchPolicy.Apply();

            if (CertificateAuthorityRepairCommand.IsMaintenanceRequest(args))
            {
                return CertificateAuthorityRepairCommand.Execute(args);
            }

            IServiceDirectoryRuntimeFactory runtimeFactory =
                ProgramRuntimeFactory.Create();
            ServiceBase.Run(
                new ServiceBase[]
                {
                    new ServiceDirectoryWindowsService(runtimeFactory)
                });
            return 0;
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
