using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace DEEPAi.ServiceDirectory.Infrastructure.Pki
{
    internal static class DirectoryPrivateKeyAccessPolicy
    {
        internal const string MainServiceAccountName =
            @"NT SERVICE\DEEPAi.ServiceDirectory";

        internal static string GetPath(X509Certificate2 certificate)
        {
            if (certificate == null)
            {
                throw new ArgumentNullException(nameof(certificate));
            }

            using (RSA rsa = certificate.GetRSAPrivateKey())
            {
                var csp = rsa as RSACryptoServiceProvider;
                if (csp != null)
                {
                    CspKeyContainerInfo keyInfo = csp.CspKeyContainerInfo;
                    if (!keyInfo.MachineKeyStore)
                    {
                        throw new CryptographicException(
                            "The Directory private key was not installed in the machine key store.");
                    }

                    return ValidatePath(
                        Path.Combine(
                            GetCommonApplicationDataPath(),
                            @"Microsoft\Crypto\RSA\MachineKeys"),
                        keyInfo.UniqueKeyContainerName);
                }

                var cng = rsa as RSACng;
                if (cng != null)
                {
                    return ValidatePath(
                        Path.Combine(
                            GetCommonApplicationDataPath(),
                            @"Microsoft\Crypto\Keys"),
                        cng.Key.UniqueName);
                }

                throw new NotSupportedException(
                    "The Directory certificate uses an unsupported Windows RSA key provider.");
            }
        }

        internal static void Apply(string path)
        {
            var administrators = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null);
            var system = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            SecurityIdentifier serviceSid = GetMainServiceSid();
            var security = new FileSecurity();
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(true, false);
            AddAccessRule(security, system, FileSystemRights.FullControl);
            AddAccessRule(
                security,
                administrators,
                FileSystemRights.FullControl);
            AddAccessRule(security, serviceSid, FileSystemRights.Read);
            File.SetAccessControl(path, security);
            Verify(path);
        }

        internal static void Verify(string path)
        {
            EnsureNoReparsePoints(path);
            FileSecurity actual = File.GetAccessControl(
                path,
                AccessControlSections.Owner | AccessControlSections.Access);
            var administrators = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null);
            var system = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            var expected = new FileSecurity();
            expected.SetOwner(administrators);
            expected.SetAccessRuleProtection(true, false);
            AddAccessRule(expected, system, FileSystemRights.FullControl);
            AddAccessRule(
                expected,
                administrators,
                FileSystemRights.FullControl);
            AddAccessRule(
                expected,
                GetMainServiceSid(),
                FileSystemRights.Read);
            AccessControlSections sections =
                AccessControlSections.Owner | AccessControlSections.Access;
            if (!actual.AreAccessRulesProtected
                || !StringComparer.Ordinal.Equals(
                    actual.GetSecurityDescriptorSddlForm(sections),
                    expected.GetSecurityDescriptorSddlForm(sections)))
            {
                throw new UnauthorizedAccessException(
                    "The Directory private-key file does not have the exact service ACL.");
            }
        }

        internal static void Delete(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            EnsureNoReparsePoints(path);
            File.Delete(path);
            if (File.Exists(path))
            {
                throw new IOException(
                    "The Directory private-key file remains after certificate removal.");
            }
        }

        private static string ValidatePath(
            string expectedDirectory,
            string fileName)
        {
            if (string.IsNullOrEmpty(fileName)
                || !StringComparer.Ordinal.Equals(
                    Path.GetFileName(fileName),
                    fileName))
            {
                throw new InvalidDataException(
                    "Windows returned an invalid machine private-key file name.");
            }

            string directory = Path.GetFullPath(expectedDirectory);
            string path = Path.GetFullPath(Path.Combine(directory, fileName));
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetDirectoryName(path),
                    directory)
                || !File.Exists(path))
            {
                throw new FileNotFoundException(
                    "The Directory machine private-key file was not found.",
                    path);
            }

            EnsureNoReparsePoints(path);
            return path;
        }

        private static void EnsureNoReparsePoints(string path)
        {
            for (FileSystemInfo current = new FileInfo(path);
                current != null;
                current = current is FileInfo
                    ? ((FileInfo)current).Directory
                    : ((DirectoryInfo)current).Parent)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "The machine private-key path must not contain reparse points.");
                }
            }
        }

        private static SecurityIdentifier GetMainServiceSid()
        {
            return (SecurityIdentifier)new NTAccount(
                MainServiceAccountName).Translate(
                    typeof(SecurityIdentifier));
        }

        private static void AddAccessRule(
            FileSecurity security,
            SecurityIdentifier sid,
            FileSystemRights rights)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                rights,
                AccessControlType.Allow));
        }

        private static string GetCommonApplicationDataPath()
        {
            string value = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new DirectoryNotFoundException(
                    "The common application data path is unavailable.");
            }

            return Path.GetFullPath(value);
        }
    }
}
