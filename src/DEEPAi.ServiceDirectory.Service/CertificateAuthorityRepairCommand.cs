using System;
using System.Globalization;
using System.ServiceProcess;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;

namespace DEEPAi.ServiceDirectory.Service
{
    internal static class CertificateAuthorityRepairCommand
    {
        internal const string ProvisionArgument =
            "--repair-pki-provision";
        internal const string RestoreArgument =
            "--repair-pki-restore";

        private const string WatchdogServiceName =
            "DEEPAi.ServiceDirectory.Watchdog";
        private const int MaximumPasswordCharacters = 256;

        internal static bool IsMaintenanceRequest(string[] arguments)
        {
            return arguments != null
                && arguments.Length != 0
                && (StringComparer.Ordinal.Equals(
                        arguments[0],
                        ProvisionArgument)
                    || StringComparer.Ordinal.Equals(
                        arguments[0],
                        RestoreArgument));
        }

        internal static int Execute(string[] arguments)
        {
            try
            {
                ValidateArguments(arguments);
                EnsureServiceStopped(
                    ServiceDirectoryWindowsService.MainServiceName);
                EnsureServiceStopped(WatchdogServiceName);
                if (!Console.IsInputRedirected)
                {
                    throw new InvalidOperationException(
                        "The repair password must be supplied through standard input.");
                }

                string dataRoot = ServiceDirectoryRuntimeComposition
                    .GetInstalledDataRootPath();
                Guid installedInstanceId = CertificateAuthorityRepair
                    .ReadInstalledInstanceId(dataRoot);
                string password = ReadPasswordFromStandardInput();
                if (StringComparer.Ordinal.Equals(
                    arguments[0],
                    ProvisionArgument))
                {
                    CertificateAuthorityBackupResult result =
                        CertificateAuthorityRepair
                            .ProvisionAndCreateInitialBackup(
                                dataRoot,
                                installedInstanceId,
                                password,
                                DateTime.UtcNow);
                    byte[] hash = result.GetSha256();
                    try
                    {
                        Console.Out.WriteLine(
                            "PKI_PROVISIONED {0} {1} {2}",
                            result.FileName,
                            result.CreatedUtc.ToString(
                                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                                CultureInfo.InvariantCulture),
                            BitConverter.ToString(hash)
                                .Replace("-", string.Empty));
                    }
                    finally
                    {
                        Array.Clear(hash, 0, hash.Length);
                    }
                }
                else
                {
                    CertificateAuthorityRepair.RestoreFromEncryptedBackup(
                        dataRoot,
                        installedInstanceId,
                        arguments[1],
                        password,
                        DateTime.UtcNow);
                    Console.Out.WriteLine("PKI_RESTORED");
                }

                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "PKI_REPAIR_FAILED {0}",
                    exception.GetType().Name);
                return 2;
            }
        }

        private static void ValidateArguments(string[] arguments)
        {
            bool provision = arguments != null
                && arguments.Length == 1
                && StringComparer.Ordinal.Equals(
                    arguments[0],
                    ProvisionArgument);
            bool restore = arguments != null
                && arguments.Length == 2
                && StringComparer.Ordinal.Equals(
                    arguments[0],
                    RestoreArgument);
            if (!provision && !restore)
            {
                throw new ArgumentException(
                    "The PKI repair command is invalid.",
                    nameof(arguments));
            }
        }

        private static void EnsureServiceStopped(string serviceName)
        {
            using (var controller = new ServiceController(serviceName))
            {
                controller.Refresh();
                if (controller.Status != ServiceControllerStatus.Stopped)
                {
                    throw new InvalidOperationException(
                        "Both Service Directory services must be stopped for PKI repair.");
                }
            }
        }

        private static string ReadPasswordFromStandardInput()
        {
            var characters = new char[MaximumPasswordCharacters + 1];
            int count = 0;
            try
            {
                while (true)
                {
                    int value = Console.In.Read();
                    if (value < 0 || value == '\n')
                    {
                        break;
                    }

                    if (value == '\r')
                    {
                        int next = Console.In.Read();
                        if (next != '\n' && next >= 0)
                        {
                            throw new ArgumentException(
                                "The repair password input has an invalid line ending.");
                        }

                        break;
                    }

                    if (count >= MaximumPasswordCharacters)
                    {
                        throw new ArgumentException(
                            "The repair password input is too long.");
                    }

                    characters[count++] = (char)value;
                }

                if (count == 0)
                {
                    throw new ArgumentException(
                        "The repair password input is empty.");
                }

                return new string(characters, 0, count);
            }
            finally
            {
                Array.Clear(characters, 0, characters.Length);
            }
        }
    }
}
