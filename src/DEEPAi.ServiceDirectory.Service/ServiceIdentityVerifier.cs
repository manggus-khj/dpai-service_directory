using System;
using System.Security;
using System.Security.Principal;

namespace DEEPAi.ServiceDirectory.Service
{
    internal static class ServiceIdentityVerifier
    {
        internal static void EnsureExpectedVirtualServiceAccount(
            string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                throw new ArgumentException(
                    "The Windows service name is required.",
                    nameof(serviceName));
            }

            try
            {
                var account = new NTAccount(
                    "NT SERVICE",
                    serviceName);
                var expectedSid = (SecurityIdentifier)account.Translate(
                    typeof(SecurityIdentifier));
                using (WindowsIdentity identity =
                    WindowsIdentity.GetCurrent(TokenAccessLevels.Query))
                {
                    if (identity.User == null)
                    {
                        throw new InvalidOperationException(
                            "The main service identity has no Windows SID.");
                    }

                    if (!identity.User.Equals(expectedSid))
                    {
                        throw new InvalidOperationException(
                            "The main service must run as its dedicated Windows virtual service account.");
                    }
                }
            }
            catch (IdentityNotMappedException exception)
            {
                throw new InvalidOperationException(
                    "The main Windows virtual service account could not be resolved.",
                    exception);
            }
            catch (SecurityException exception)
            {
                throw new InvalidOperationException(
                    "The main service identity could not be verified.",
                    exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new InvalidOperationException(
                    "Access to the main service identity was denied.",
                    exception);
            }
        }
    }
}
