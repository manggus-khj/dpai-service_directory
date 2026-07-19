using System.Collections.Generic;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DEEPAi.ServiceDirectory.Watchdog;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Watchdog
{
    [TestClass]
    public sealed class WatchdogPipeClientAuthorizationTests
    {
        [TestMethod]
        public void PipeDaclIsProtectedAndContainsOnlyApprovedPrincipals()
        {
            var operators = new SecurityIdentifier(
                "S-1-5-21-1-2-3-1001");
            var watchdogService = new SecurityIdentifier(
                "S-1-5-80-1-2-3-4-5");
            var authorization = new WatchdogPipeClientAuthorization(
                operators,
                watchdogService,
                "TEST-NODE");

            PipeSecurity security = authorization.CreatePipeSecurity();
            Assert.IsTrue(security.AreAccessRulesProtected);

            var expected = new Dictionary<string, PipeAccessRights>
            {
                {
                    watchdogService.Value,
                    PipeAccessRights.ReadWrite
                        | PipeAccessRights.CreateNewInstance
                        | PipeAccessRights.Synchronize
                },
                {
                    operators.Value,
                    PipeAccessRights.ReadWrite
                        | PipeAccessRights.Synchronize
                },
                {
                    new SecurityIdentifier(
                        WellKnownSidType.LocalSystemSid,
                        null).Value,
                    PipeAccessRights.ReadWrite
                        | PipeAccessRights.Synchronize
                },
                {
                    new SecurityIdentifier(
                        WellKnownSidType.BuiltinAdministratorsSid,
                        null).Value,
                    PipeAccessRights.ReadWrite
                        | PipeAccessRights.Synchronize
                }
            };

            AuthorizationRuleCollection rules = security.GetAccessRules(
                true,
                false,
                typeof(SecurityIdentifier));
            Assert.AreEqual(expected.Count, rules.Count);
            foreach (PipeAccessRule rule in rules)
            {
                Assert.AreEqual(
                    AccessControlType.Allow,
                    rule.AccessControlType);
                var sid = (SecurityIdentifier)rule.IdentityReference;
                Assert.IsTrue(expected.ContainsKey(sid.Value));
                Assert.AreEqual(
                    expected[sid.Value],
                    rule.PipeAccessRights);
                expected.Remove(sid.Value);
            }

            Assert.AreEqual(0, expected.Count);
        }
    }
}
