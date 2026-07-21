using System;
using System.IO;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    public sealed partial class ConfigurationPersistenceTests
    {
        [TestMethod]
        public void CodecRejectsPartialMismatchedOrIpv6DirectoryIdentity()
        {
            var codec = new StateXmlCodec();
            string xml = StrictUtf8.GetString(
                codec.SerializeConfiguration(CreateInitialConfiguration()));
            byte[] missingHostName = StrictUtf8.GetBytes(
                xml.Replace(
                    "  <DirectoryHostName>management.internal</DirectoryHostName>\r\n",
                    string.Empty));
            byte[] mismatchedAddress = StrictUtf8.GetBytes(
                xml.Replace(
                    "<DirectoryIpv4Address>10.20.30.40</DirectoryIpv4Address>",
                    "<DirectoryIpv4Address>10.20.30.41</DirectoryIpv4Address>"));
            byte[] ipv6Address = StrictUtf8.GetBytes(
                xml.Replace(
                    "<DirectoryIpv4Address>10.20.30.40</DirectoryIpv4Address>",
                    "<DirectoryIpv4Address>2001:db8::1</DirectoryIpv4Address>"));

            Assert.ThrowsExactly<InvalidDataException>(() =>
                codec.DeserializeConfiguration(missingHostName));
            Assert.ThrowsExactly<InvalidDataException>(() =>
                codec.DeserializeConfiguration(mismatchedAddress));
            Assert.ThrowsExactly<InvalidDataException>(() =>
                codec.DeserializeConfiguration(ipv6Address));
        }

        [TestMethod]
        public void ConfigurationRejectsHttpAndIpv6PeerEndpoints()
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                SynchronizationConfiguration.Enabled(
                    "http://10.20.30.41:21000",
                    PeerInstanceId,
                    1,
                    LastSynchronizationStatus.NotRun(),
                    PeerNotificationStatus.NotRun()));
            Assert.ThrowsExactly<ArgumentException>(() =>
                SynchronizationConfiguration.Enabled(
                    "https://[2001:db8::1]:21000",
                    PeerInstanceId,
                    1,
                    LastSynchronizationStatus.NotRun(),
                    PeerNotificationStatus.NotRun()));
        }

        [TestMethod]
        public void NormalCommitRejectsDirectoryIdentityChange()
        {
            string stateDirectory = CreateInitializedStateDirectory();
            try
            {
                var store = new XmlServiceDirectoryConfigurationStore(
                    stateDirectory);
                ConfigurationLoadResult loaded = store.Load();
                ServiceDirectoryConfiguration next =
                    loaded.Configuration.WithDirectoryIdentityForRepair(
                        CreateDirectoryIdentity(
                            "management-repaired.internal",
                            "10.20.30.42"));

                Assert.ThrowsExactly<ArgumentException>(
                    () => store.Commit(loaded.Configuration, next));
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }
    }
}
