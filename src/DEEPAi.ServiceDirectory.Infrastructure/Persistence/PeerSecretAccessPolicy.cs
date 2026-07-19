using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal interface ISecretFileAccessPolicy
    {
        void ProtectExistingFile(string path);

        void ValidateExistingFile(string path);
    }

    internal interface IPeerSecretAccessPolicy : ISecretFileAccessPolicy
    {
    }

    internal sealed class PeerSecretAccessPolicy
        : IPeerSecretAccessPolicy
    {
        private const string MainServiceName =
            "DEEPAi.ServiceDirectory";

        private static readonly FileSystemRights RequiredReadWriteRights =
            FileSystemRights.Read | FileSystemRights.Write;

        private readonly SecurityIdentifier _serviceSid;
        private readonly SecurityIdentifier _systemSid;
        private readonly SecurityIdentifier _administratorsSid;

        internal PeerSecretAccessPolicy(SecurityIdentifier serviceSid)
        {
            if (serviceSid == null)
            {
                throw new ArgumentNullException(nameof(serviceSid));
            }

            _systemSid = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            _administratorsSid = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null);
            if (serviceSid.Equals(_systemSid)
                || serviceSid.Equals(_administratorsSid))
            {
                throw new ArgumentException(
                    "The main service SID must be a distinct service identity.",
                    nameof(serviceSid));
            }

            _serviceSid = serviceSid;
        }

        internal static PeerSecretAccessPolicy ForInstalledMainService()
        {
            var account = new NTAccount(
                "NT SERVICE",
                MainServiceName);
            var serviceSid = (SecurityIdentifier)account.Translate(
                typeof(SecurityIdentifier));
            return new PeerSecretAccessPolicy(serviceSid);
        }

        public void ValidateExistingFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "The peer credential path is required.",
                    nameof(path));
            }

            var file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new FileNotFoundException(
                    "The peer credential file does not exist.",
                    path);
            }

            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "The peer credential file must not be a reparse point.");
            }

            FileSecurity security = file.GetAccessControl(
                AccessControlSections.Access
                    | AccessControlSections.Owner);
            ValidateDescriptor(security);
        }

        public void ProtectExistingFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "The peer credential path is required.",
                    nameof(path));
            }

            var file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new FileNotFoundException(
                    "The peer credential file does not exist.",
                    path);
            }

            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "The peer credential file must not be a reparse point.");
            }

            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(_serviceSid);
            AddFullControl(security, _serviceSid);
            AddFullControl(security, _systemSid);
            AddFullControl(security, _administratorsSid);
            file.SetAccessControl(security);
            ValidateExistingFile(path);
        }

        internal void ValidateDescriptor(FileSecurity security)
        {
            if (security == null)
            {
                throw new ArgumentNullException(nameof(security));
            }

            if (!security.AreAccessRulesProtected)
            {
                throw new UnauthorizedAccessException(
                    "The peer credential DACL must be protected from inheritance.");
            }

            var owner = security.GetOwner(
                typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null
                || (!owner.Equals(_systemSid)
                    && !owner.Equals(_administratorsSid)
                    && !owner.Equals(_serviceSid)))
            {
                throw new UnauthorizedAccessException(
                    "The peer credential owner is not a trusted local principal.");
            }

            FileSystemRights serviceRights = 0;
            FileSystemRights systemRights = 0;
            FileSystemRights administratorRights = 0;
            AuthorizationRuleCollection rules = security.GetAccessRules(
                true,
                true,
                typeof(SecurityIdentifier));
            foreach (AuthorizationRule authorizationRule in rules)
            {
                var rule = authorizationRule as FileSystemAccessRule;
                var sid = authorizationRule.IdentityReference
                    as SecurityIdentifier;
                if (rule == null
                    || sid == null
                    || rule.IsInherited
                    || rule.AccessControlType != AccessControlType.Allow
                    || rule.InheritanceFlags != InheritanceFlags.None
                    || rule.PropagationFlags != PropagationFlags.None)
                {
                    throw new UnauthorizedAccessException(
                        "The peer credential DACL contains an unsupported access rule.");
                }

                if (sid.Equals(_serviceSid))
                {
                    serviceRights |= rule.FileSystemRights;
                }
                else if (sid.Equals(_systemSid))
                {
                    systemRights |= rule.FileSystemRights;
                }
                else if (sid.Equals(_administratorsSid))
                {
                    administratorRights |= rule.FileSystemRights;
                }
                else
                {
                    throw new UnauthorizedAccessException(
                        "The peer credential DACL grants access to an unapproved principal.");
                }
            }

            RequireReadWrite(serviceRights, "main service SID");
            RequireReadWrite(systemRights, "SYSTEM");
            RequireReadWrite(
                administratorRights,
                "local Administrators");
        }

        private static void RequireReadWrite(
            FileSystemRights actual,
            string principalName)
        {
            if ((actual & RequiredReadWriteRights)
                != RequiredReadWriteRights)
            {
                throw new UnauthorizedAccessException(
                    "The peer credential DACL does not grant required read/write access to "
                    + principalName
                    + ".");
            }
        }

        private static void AddFullControl(
            FileSecurity security,
            SecurityIdentifier sid)
        {
            security.AddAccessRule(
                new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Allow));
        }
    }
}
