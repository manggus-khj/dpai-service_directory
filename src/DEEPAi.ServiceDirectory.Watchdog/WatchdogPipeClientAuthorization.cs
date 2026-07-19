using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using Microsoft.Win32.SafeHandles;

namespace DEEPAi.ServiceDirectory.Watchdog
{
    internal sealed class WatchdogPipeClientAuthorization
    {
        internal const string LocalOperatorsGroupName =
            "DEEPAi-ServiceDirectory-Operators";

        private const int ComputerNameBufferCharacters = 256;

        private readonly SecurityIdentifier _operatorsGroupSid;
        private readonly SecurityIdentifier _watchdogServiceSid;
        private readonly SecurityIdentifier _systemSid;
        private readonly SecurityIdentifier _administratorsSid;
        private readonly string _localComputerName;

        internal WatchdogPipeClientAuthorization(
            SecurityIdentifier operatorsGroupSid,
            SecurityIdentifier watchdogServiceSid,
            string localComputerName)
        {
            _operatorsGroupSid = operatorsGroupSid
                ?? throw new ArgumentNullException(nameof(operatorsGroupSid));
            _watchdogServiceSid = watchdogServiceSid
                ?? throw new ArgumentNullException(nameof(watchdogServiceSid));
            if (string.IsNullOrWhiteSpace(localComputerName))
            {
                throw new ArgumentException(
                    "The local computer name is required.",
                    nameof(localComputerName));
            }

            _localComputerName = localComputerName;
            _systemSid = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            _administratorsSid = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null);
        }

        internal static WatchdogPipeClientAuthorization Create(
            string watchdogServiceName)
        {
            if (string.IsNullOrWhiteSpace(watchdogServiceName))
            {
                throw new ArgumentException(
                    "The watchdog Windows Service name is required.",
                    nameof(watchdogServiceName));
            }

            return new WatchdogPipeClientAuthorization(
                ResolveAccountSid(
                    Environment.MachineName,
                    LocalOperatorsGroupName),
                ResolveAccountSid("NT SERVICE", watchdogServiceName),
                Environment.MachineName);
        }

        internal PipeSecurity CreatePipeSecurity()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(
                _watchdogServiceSid,
                PipeAccessRights.ReadWrite
                    | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                _operatorsGroupSid,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                _systemSid,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                _administratorsSid,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
            return security;
        }

        internal bool TryAuthorize(
            NamedPipeServerStream pipe,
            out SecurityIdentifier actorSid,
            out SecurityAuditReason rejectionReason)
        {
            if (pipe == null)
            {
                throw new ArgumentNullException(nameof(pipe));
            }

            actorSid = null;
            rejectionReason = SecurityAuditReason.ClientTokenUnavailable;
            if (!pipe.IsConnected || !IsLocalClient(pipe.SafePipeHandle))
            {
                return false;
            }

            WindowsIdentity identity = null;
            try
            {
                pipe.RunAsClient(() =>
                {
                    identity = WindowsIdentity.GetCurrent(
                        TokenAccessLevels.Query);
                });
                if (identity == null
                    || !identity.IsAuthenticated
                    || identity.User == null)
                {
                    return false;
                }

                actorSid = identity.User;
                var principal = new WindowsPrincipal(identity);
                bool authorized = actorSid.Equals(_watchdogServiceSid)
                    || actorSid.Equals(_systemSid)
                    || principal.IsInRole(_operatorsGroupSid)
                    || principal.IsInRole(_administratorsSid);
                if (!authorized)
                {
                    rejectionReason =
                        SecurityAuditReason.ClientNotAuthorized;
                }

                return authorized;
            }
            catch (InvalidOperationException)
            {
                actorSid = null;
                rejectionReason =
                    SecurityAuditReason.ClientTokenUnavailable;
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                actorSid = null;
                rejectionReason =
                    SecurityAuditReason.ClientTokenUnavailable;
                return false;
            }
            catch (SecurityException)
            {
                actorSid = null;
                rejectionReason =
                    SecurityAuditReason.ClientTokenUnavailable;
                return false;
            }
            catch (IOException)
            {
                actorSid = null;
                rejectionReason =
                    SecurityAuditReason.ClientTokenUnavailable;
                return false;
            }
            catch (Win32Exception)
            {
                actorSid = null;
                rejectionReason =
                    SecurityAuditReason.ClientTokenUnavailable;
                return false;
            }
            finally
            {
                if (identity != null)
                {
                    identity.Dispose();
                }
            }
        }

        private bool IsLocalClient(SafePipeHandle pipeHandle)
        {
            var computerName = new StringBuilder(
                ComputerNameBufferCharacters);
            if (!GetNamedPipeClientComputerName(
                    pipeHandle,
                    computerName,
                    computerName.Capacity))
            {
                return false;
            }

            string reported = computerName.ToString().TrimStart('\\');
            return StringComparer.OrdinalIgnoreCase.Equals(
                reported,
                _localComputerName);
        }

        private static SecurityIdentifier ResolveAccountSid(
            string domainName,
            string accountName)
        {
            var account = new NTAccount(domainName, accountName);
            try
            {
                return (SecurityIdentifier)account.Translate(
                    typeof(SecurityIdentifier));
            }
            catch (IdentityNotMappedException)
            {
                throw;
            }
            catch (SystemException exception)
                when (exception.GetType() == typeof(SystemException))
            {
                throw new InvalidOperationException(
                    "A required local watchdog security principal could not be resolved.",
                    exception);
            }
        }

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetNamedPipeClientComputerNameW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientComputerName(
            SafePipeHandle pipe,
            StringBuilder clientComputerName,
            int clientComputerNameLength);
    }
}
