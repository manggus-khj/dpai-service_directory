using System;
using System.IO;
using System.Text;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class ConfigurationPersistenceTests
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly Guid InstanceId = new Guid(
            "11111111-1111-1111-1111-111111111111");
        private static readonly Guid PeerInstanceId = new Guid(
            "22222222-2222-2222-2222-222222222222");
        private static readonly Guid PairingId = new Guid(
            "33333333-3333-3333-3333-333333333333");

        [TestMethod]
        public void InitialConfigurationUsesThirtyDayUnpairedDefaults()
        {
            ServiceDirectoryConfiguration configuration =
                CreateInitialConfiguration();

            Assert.AreEqual("10.20.30.40", configuration.ListenAddress);
            Assert.AreEqual(InstanceId, configuration.InstanceId);
            Assert.AreEqual(0UL, configuration.LastPeerKeyEpoch);
            Assert.AreEqual(30, configuration.LogRetentionDays);
            Assert.AreEqual(
                DurableSynchronizationState.Unpaired,
                configuration.Synchronization.State);
            Assert.AreEqual(
                "NOT_RUN",
                configuration.Synchronization.LastSynchronization.Result);
            Assert.AreEqual(
                PeerNotificationOperation.None,
                configuration.Synchronization.LastPeerNotification.Operation);
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1096)]
        public void ConfigurationRejectsRetentionOutsideSupportedRange(
            int retentionDays)
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new ServiceDirectoryConfiguration(
                    "10.20.30.40",
                    InstanceId,
                    0UL,
                    retentionDays,
                    SynchronizationConfiguration.Unpaired(
                        LastSynchronizationStatus.NotRun(),
                        PeerNotificationStatus.NotRun())));
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(1095)]
        public void ConfigurationAcceptsRetentionBoundaryValues(
            int retentionDays)
        {
            ServiceDirectoryConfiguration configuration =
                CreateInitialConfiguration().WithLogRetentionDays(
                    retentionDays);

            Assert.AreEqual(retentionDays, configuration.LogRetentionDays);
        }

        [TestMethod]
        public void ConfigurationRequiresCurrentPeerEpochToEqualHighWater()
        {
            SynchronizationConfiguration synchronization =
                SynchronizationConfiguration.Enabled(
                    "http://10.20.30.41:21000",
                    PeerInstanceId,
                    7UL,
                    LastSynchronizationStatus.NotRun(),
                    PeerNotificationStatus.NotRun());

            Assert.ThrowsExactly<ArgumentException>(
                () => new ServiceDirectoryConfiguration(
                    "10.20.30.40",
                    InstanceId,
                    8UL,
                    30,
                    synchronization));
        }

        [TestMethod]
        public void DurableHistoryRejectsInconsistentConditionalFields()
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => new LastSynchronizationStatus(
                    LastSynchronizationStatus.NotRunResult,
                    Utc(1),
                    null));
            Assert.ThrowsExactly<ArgumentException>(
                () => new LastSynchronizationStatus(
                    "OK",
                    null,
                    null));
            Assert.ThrowsExactly<ArgumentException>(
                () => new PeerNotificationStatus(
                    PeerNotificationOperation.None,
                    PeerNotificationResult.Confirmed,
                    Utc(1)));
        }

        [TestMethod]
        public void CodecRoundTripsMaximumPeerEpochHighWater()
        {
            var codec = new StateXmlCodec();
            ServiceDirectoryConfiguration expected =
                CreateUnpairedConfigurationWithEpoch(ulong.MaxValue);

            ServiceDirectoryConfiguration actual =
                codec.DeserializeConfiguration(
                    codec.SerializeConfiguration(expected));

            Assert.AreEqual(ulong.MaxValue, actual.LastPeerKeyEpoch);
            Assert.IsTrue(ConfigurationValueComparer.Equals(expected, actual));
        }

        [TestMethod]
        public void CodecRoundTripsCanonicalInitialConfiguration()
        {
            var codec = new StateXmlCodec();
            ServiceDirectoryConfiguration expected =
                CreateInitialConfiguration();

            byte[] contents = codec.SerializeConfiguration(expected);
            ServiceDirectoryConfiguration actual =
                codec.DeserializeConfiguration(contents);
            string xml = StrictUtf8.GetString(contents);

            Assert.IsTrue(ConfigurationValueComparer.Equals(expected, actual));
            Assert.IsFalse(
                contents.Length >= 3
                && contents[0] == 0xEF
                && contents[1] == 0xBB
                && contents[2] == 0xBF);
            StringAssert.Contains(xml, "<Config SchemaVersion=\"1\">");
            StringAssert.Contains(xml, "<LogRetentionDays>30</LogRetentionDays>");
            StringAssert.Contains(xml, "<State>Unpaired</State>");
            Assert.DoesNotContain("PairingWindowOpen", xml);
            Assert.DoesNotContain("Sas", xml);
        }

        [TestMethod]
        public void CodecWritesExactCanonicalInitialBytes()
        {
            var codec = new StateXmlCodec();

            string xml = StrictUtf8.GetString(
                codec.SerializeConfiguration(CreateInitialConfiguration()));

            const string expected =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
                + "<Config SchemaVersion=\"1\">\r\n"
                + "  <ListenAddress>10.20.30.40</ListenAddress>\r\n"
                + "  <InstanceId>11111111-1111-1111-1111-111111111111</InstanceId>\r\n"
                + "  <LastPeerKeyEpoch>0</LastPeerKeyEpoch>\r\n"
                + "  <LogRetentionDays>30</LogRetentionDays>\r\n"
                + "  <Sync>\r\n"
                + "    <State>Unpaired</State>\r\n"
                + "    <LastResult>NOT_RUN</LastResult>\r\n"
                + "    <LastPeerNotificationOperation>NONE</LastPeerNotificationOperation>\r\n"
                + "    <LastPeerNotificationResult>NOT_RUN</LastPeerNotificationResult>\r\n"
                + "  </Sync>\r\n"
                + "</Config>";
            Assert.AreEqual(expected, xml);
        }

        [TestMethod]
        public void CodecRoundTripsEnabledStateAndDurableHistory()
        {
            var codec = new StateXmlCodec();
            ServiceDirectoryConfiguration expected =
                CreateEnabledConfiguration();

            ServiceDirectoryConfiguration actual =
                codec.DeserializeConfiguration(
                    codec.SerializeConfiguration(expected));

            Assert.IsTrue(ConfigurationValueComparer.Equals(expected, actual));
            Assert.AreEqual(
                DurableSynchronizationState.Enabled,
                actual.Synchronization.State);
            Assert.AreEqual(
                "http://10.20.30.41:21000",
                actual.Synchronization.PeerEndpoint);
            Assert.AreEqual(-2L, actual.Synchronization
                .LastSynchronization.ClockSkewSeconds.Value);
            Assert.AreEqual(
                PeerNotificationResult.Confirmed,
                actual.Synchronization.LastPeerNotification.Result);
        }

        [TestMethod]
        public void CodecRoundTripsPairedPendingCommitState()
        {
            var lastSync = LastSynchronizationStatus.NotRun();
            var notification = PeerNotificationStatus.NotRun();
            SynchronizationConfiguration synchronization =
                SynchronizationConfiguration.PairedPendingCommit(
                    "http://[2001:db8::2]:21000",
                    PeerInstanceId,
                    9UL,
                    PairingId,
                    Utc(23),
                    true,
                    false,
                    lastSync,
                    notification);
            var expected = new ServiceDirectoryConfiguration(
                "2001:db8::1",
                InstanceId,
                9UL,
                30,
                synchronization);
            var codec = new StateXmlCodec();

            ServiceDirectoryConfiguration actual =
                codec.DeserializeConfiguration(
                    codec.SerializeConfiguration(expected));

            Assert.IsTrue(ConfigurationValueComparer.Equals(expected, actual));
            Assert.AreEqual(
                PairingId,
                actual.Synchronization.PairingId.Value);
            Assert.IsTrue(
                actual.Synchronization.LocalCommitConfirmed.Value);
            Assert.IsFalse(
                actual.Synchronization.RemoteCommitConfirmed.Value);
        }

        [TestMethod]
        public void CodecRejectsNoncanonicalDurableFieldRepresentations()
        {
            var codec = new StateXmlCodec();
            string enabled = StrictUtf8.GetString(
                codec.SerializeConfiguration(CreateEnabledConfiguration()));
            byte[] noncanonicalInstanceId = StrictUtf8.GetBytes(
                enabled.Replace(
                    "<InstanceId>11111111-1111-1111-1111-111111111111</InstanceId>",
                    "<InstanceId>{11111111-1111-1111-1111-111111111111}</InstanceId>"));
            byte[] noncanonicalEpoch = StrictUtf8.GetBytes(
                enabled.Replace(
                    "<LastPeerKeyEpoch>7</LastPeerKeyEpoch>",
                    "<LastPeerKeyEpoch>07</LastPeerKeyEpoch>"));
            byte[] noncanonicalClockSkew = StrictUtf8.GetBytes(
                enabled.Replace(
                    "<ClockSkewSeconds>-2</ClockSkewSeconds>",
                    "<ClockSkewSeconds>-02</ClockSkewSeconds>"));
            byte[] noncanonicalUtc = StrictUtf8.GetBytes(
                enabled.Replace(
                    "2026-07-18T02:00:00.0000000Z",
                    "2026-07-18T02:00:00Z"));

            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeConfiguration(
                    noncanonicalInstanceId));
            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeConfiguration(noncanonicalEpoch));
            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeConfiguration(
                    noncanonicalClockSkew));
            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeConfiguration(noncanonicalUtc));
        }

        [TestMethod]
        public void CodecRejectsNoncanonicalBooleanAndConditionalPeerFields()
        {
            var codec = new StateXmlCodec();
            var pending = new ServiceDirectoryConfiguration(
                "10.20.30.40",
                InstanceId,
                9UL,
                30,
                SynchronizationConfiguration.PairedPendingCommit(
                    "http://10.20.30.41:21000",
                    PeerInstanceId,
                    9UL,
                    PairingId,
                    Utc(23),
                    true,
                    false,
                    LastSynchronizationStatus.NotRun(),
                    PeerNotificationStatus.NotRun()));
            string pendingXml = StrictUtf8.GetString(
                codec.SerializeConfiguration(pending));
            byte[] noncanonicalBoolean = StrictUtf8.GetBytes(
                pendingXml.Replace(
                    "<LocalCommitConfirmed>true</LocalCommitConfirmed>",
                    "<LocalCommitConfirmed>True</LocalCommitConfirmed>"));
            string enabledXml = StrictUtf8.GetString(
                codec.SerializeConfiguration(CreateEnabledConfiguration()));
            byte[] missingPeerEndpoint = StrictUtf8.GetBytes(
                enabledXml.Replace(
                    "    <PeerEndpoint>http://10.20.30.41:21000</PeerEndpoint>\r\n",
                    string.Empty));

            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeConfiguration(noncanonicalBoolean));
            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeConfiguration(missingPeerEndpoint));
        }

        [TestMethod]
        [DataRow("0")]
        [DataRow("1096")]
        [DataRow("030")]
        [DataRow("not-an-integer")]
        [DataRow("999999999999999999999999")]
        public void CodecRejectsInvalidRetentionValue(string value)
        {
            var codec = new StateXmlCodec();
            string xml = StrictUtf8.GetString(
                codec.SerializeConfiguration(CreateInitialConfiguration()));
            byte[] invalid = StrictUtf8.GetBytes(
                xml.Replace(
                    "<LogRetentionDays>30</LogRetentionDays>",
                    "<LogRetentionDays>" + value + "</LogRetentionDays>"));

            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeConfiguration(invalid));
        }

        [TestMethod]
        public void CodecRejectsTransientPairingState()
        {
            var codec = new StateXmlCodec();
            string xml = StrictUtf8.GetString(
                codec.SerializeConfiguration(CreateInitialConfiguration()));
            byte[] invalid = StrictUtf8.GetBytes(
                xml.Replace(
                    "<State>Unpaired</State>",
                    "<State>SasPending</State>"));

            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeConfiguration(invalid));
        }

        [TestMethod]
        [DataRow("SchemaVersion=\"2\"")]
        [DataRow("SchemaVersion=\"01\"")]
        [DataRow("")]
        public void CodecRejectsMissingOrUnsupportedSchemaVersion(
            string replacement)
        {
            var codec = new StateXmlCodec();
            string xml = StrictUtf8.GetString(
                codec.SerializeConfiguration(CreateInitialConfiguration()));
            byte[] invalid = StrictUtf8.GetBytes(
                xml.Replace("SchemaVersion=\"1\"", replacement));

            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeConfiguration(invalid));
        }

        [TestMethod]
        public void CodecRejectsUnknownElementAndAttribute()
        {
            var codec = new StateXmlCodec();
            string xml = StrictUtf8.GetString(
                codec.SerializeConfiguration(CreateInitialConfiguration()));
            byte[] unknownElement = StrictUtf8.GetBytes(
                xml.Replace(
                    "</Config>",
                    "  <Unknown>value</Unknown>\r\n</Config>"));
            byte[] unknownAttribute = StrictUtf8.GetBytes(
                xml.Replace(
                    "<State>Unpaired</State>",
                    "<State Unexpected=\"true\">Unpaired</State>"));

            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeConfiguration(unknownElement));
            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeConfiguration(unknownAttribute));
        }

        [TestMethod]
        public void CodecRejectsDtdBomAndNoncanonicalListenAddress()
        {
            var codec = new StateXmlCodec();
            byte[] canonical = codec.SerializeConfiguration(
                CreateInitialConfiguration());
            string xml = StrictUtf8.GetString(canonical);
            byte[] withDtd = StrictUtf8.GetBytes(
                xml.Replace(
                    "?>\r\n",
                    "?>\r\n<!DOCTYPE Config [<!ENTITY x \"value\">]>\r\n"));
            var withBom = new byte[canonical.Length + 3];
            withBom[0] = 0xEF;
            withBom[1] = 0xBB;
            withBom[2] = 0xBF;
            Buffer.BlockCopy(
                canonical,
                0,
                withBom,
                3,
                canonical.Length);
            byte[] noncanonicalAddress = StrictUtf8.GetBytes(
                xml.Replace("10.20.30.40", "010.20.30.40"));

            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeConfiguration(withDtd));
            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeConfiguration(withBom));
            Assert.ThrowsExactly<InvalidDataException>(
                () => codec.DeserializeConfiguration(noncanonicalAddress));
        }

        [TestMethod]
        public void StoreRequiresLoadBeforeInitialization()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                var store = new XmlServiceDirectoryConfigurationStore(
                    stateDirectory);

                ConfigurationCommitResult result = store.Initialize(
                    CreateInitialConfiguration());

                Assert.IsFalse(result.IsSuccess);
                Assert.AreEqual(
                    ConfigurationCommitFailureCode.RecoveryRequired,
                    result.FailureCode);
                Assert.IsFalse(File.Exists(
                    Path.Combine(stateDirectory, "config.xml")));
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void StoreInitializationRequiresExactInstallationDefaults()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                var store = new XmlServiceDirectoryConfigurationStore(
                    stateDirectory);
                Assert.AreEqual(
                    ConfigurationLoadFailureCode.Missing,
                    store.Load().FailureCode);
                ServiceDirectoryConfiguration nondefault =
                    CreateInitialConfiguration().WithLogRetentionDays(31);

                Assert.ThrowsExactly<ArgumentException>(
                    () => store.Initialize(nondefault));
                Assert.IsFalse(File.Exists(
                    Path.Combine(stateDirectory, "config.xml")));
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void StoreInitializesAndReloadsConfiguration()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                ServiceDirectoryConfiguration expected =
                    CreateInitialConfiguration();
                var store = new XmlServiceDirectoryConfigurationStore(
                    stateDirectory);

                ConfigurationLoadResult missing = store.Load();
                ConfigurationCommitResult initialized =
                    store.Initialize(expected);
                ConfigurationLoadResult reloaded =
                    new XmlServiceDirectoryConfigurationStore(
                        stateDirectory).Load();

                Assert.IsFalse(missing.IsSuccess);
                Assert.AreEqual(
                    ConfigurationLoadFailureCode.Missing,
                    missing.FailureCode);
                Assert.IsTrue(initialized.IsSuccess);
                Assert.IsTrue(reloaded.IsSuccess);
                Assert.IsTrue(ConfigurationValueComparer.Equals(
                    expected,
                    reloaded.Configuration));
                AssertJournalIsEmpty(stateDirectory);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void StorePersistsRetentionAndRestrictsAddressToRepair()
        {
            string stateDirectory = CreateInitializedStateDirectory();
            try
            {
                var store = new XmlServiceDirectoryConfigurationStore(
                    stateDirectory);
                ConfigurationLoadResult loaded = store.Load();
                ServiceDirectoryConfiguration retentionChanged =
                    loaded.Configuration.WithLogRetentionDays(1095);

                ConfigurationCommitResult saved = store.Commit(
                    loaded.Configuration,
                    retentionChanged);
                ConfigurationCommitResult repaired =
                    store.CommitListenAddressForRepair(
                        retentionChanged,
                        "10.20.30.42");
                ConfigurationLoadResult reloaded =
                    new XmlServiceDirectoryConfigurationStore(
                        stateDirectory).Load();

                Assert.IsTrue(saved.IsSuccess);
                Assert.IsTrue(repaired.IsSuccess);
                Assert.IsTrue(reloaded.IsSuccess);
                Assert.AreEqual(1095, reloaded.Configuration.LogRetentionDays);
                Assert.AreEqual(
                    "10.20.30.42",
                    reloaded.Configuration.ListenAddress);
                Assert.AreEqual(InstanceId, reloaded.Configuration.InstanceId);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void NormalCommitRejectsListenAddressChange()
        {
            string stateDirectory = CreateInitializedStateDirectory();
            try
            {
                var store = new XmlServiceDirectoryConfigurationStore(
                    stateDirectory);
                ConfigurationLoadResult loaded = store.Load();
                ServiceDirectoryConfiguration next =
                    loaded.Configuration.WithListenAddressForRepair(
                        "10.20.30.42");

                Assert.ThrowsExactly<ArgumentException>(
                    () => store.Commit(loaded.Configuration, next));
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void NormalCommitRejectsInstanceIdChange()
        {
            string stateDirectory = CreateInitializedStateDirectory();
            try
            {
                var store = new XmlServiceDirectoryConfigurationStore(
                    stateDirectory);
                ConfigurationLoadResult loaded = store.Load();
                var next = new ServiceDirectoryConfiguration(
                    loaded.Configuration.ListenAddress,
                    new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    loaded.Configuration.LastPeerKeyEpoch,
                    loaded.Configuration.LogRetentionDays,
                    loaded.Configuration.Synchronization);

                Assert.ThrowsExactly<ArgumentException>(
                    () => store.Commit(loaded.Configuration, next));
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void StoreRejectsLastPeerKeyEpochDecrease()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                var store = new XmlServiceDirectoryConfigurationStore(
                    stateDirectory);
                ServiceDirectoryConfiguration initial =
                    CreateInitialConfiguration();
                ServiceDirectoryConfiguration expected =
                    CreateUnpairedConfigurationWithEpoch(4UL);
                Assert.AreEqual(
                    ConfigurationLoadFailureCode.Missing,
                    store.Load().FailureCode);
                Assert.IsTrue(store.Initialize(initial).IsSuccess);
                Assert.IsTrue(store.Commit(initial, expected).IsSuccess);
                var next = CreateUnpairedConfigurationWithEpoch(3UL);

                Assert.ThrowsExactly<ArgumentException>(
                    () => store.Commit(expected, next));
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void PreparedInitializationRollsBackToMissingConfiguration()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                var injector = new ThrowOnceFaultInjector(
                    RecoveryJournalFaultPoint.TargetApplied,
                    StateFileTarget.Config);
                var store = new XmlServiceDirectoryConfigurationStore(
                    stateDirectory,
                    injector);
                Assert.AreEqual(
                    ConfigurationLoadFailureCode.Missing,
                    store.Load().FailureCode);

                ConfigurationCommitResult initialized = store.Initialize(
                    CreateInitialConfiguration());
                ConfigurationLoadResult recovered =
                    new XmlServiceDirectoryConfigurationStore(
                        stateDirectory).Load();

                Assert.IsFalse(initialized.IsSuccess);
                Assert.AreEqual(
                    ConfigurationCommitFailureCode.RecoveryRequired,
                    initialized.FailureCode);
                Assert.IsFalse(recovered.IsSuccess);
                Assert.AreEqual(
                    ConfigurationLoadFailureCode.Missing,
                    recovered.FailureCode);
                Assert.IsFalse(File.Exists(
                    Path.Combine(stateDirectory, "config.xml")));
                Assert.IsFalse(File.Exists(
                    Path.Combine(stateDirectory, "config.xml.bak")));
                AssertJournalIsEmpty(stateDirectory);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void CommittedInitializationRollsForwardOnNextLoad()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                ServiceDirectoryConfiguration expected =
                    CreateInitialConfiguration();
                var injector = new ThrowOnceFaultInjector(
                    RecoveryJournalFaultPoint.CommittedFlushed,
                    null);
                var store = new XmlServiceDirectoryConfigurationStore(
                    stateDirectory,
                    injector);
                Assert.AreEqual(
                    ConfigurationLoadFailureCode.Missing,
                    store.Load().FailureCode);

                ConfigurationCommitResult initialized =
                    store.Initialize(expected);
                ConfigurationLoadResult recovered =
                    new XmlServiceDirectoryConfigurationStore(
                        stateDirectory).Load();

                Assert.IsFalse(initialized.IsSuccess);
                Assert.AreEqual(
                    ConfigurationCommitFailureCode.RecoveryRequired,
                    initialized.FailureCode);
                Assert.IsTrue(recovered.IsSuccess);
                Assert.IsTrue(ConfigurationValueComparer.Equals(
                    expected,
                    recovered.Configuration));
                AssertJournalIsEmpty(stateDirectory);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void PreparedUpdateRestoresConfigurationAndItsBackup()
        {
            string stateDirectory = CreateInitializedStateDirectory();
            try
            {
                var injector = new ThrowOnceFaultInjector(
                    RecoveryJournalFaultPoint.TargetApplied,
                    StateFileTarget.Config);
                var store = new XmlServiceDirectoryConfigurationStore(
                    stateDirectory,
                    injector);
                ConfigurationLoadResult loaded = store.Load();

                ConfigurationCommitResult commit = store.Commit(
                    loaded.Configuration,
                    loaded.Configuration.WithLogRetentionDays(31));
                ConfigurationLoadResult recovered =
                    new XmlServiceDirectoryConfigurationStore(
                        stateDirectory).Load();
                ServiceDirectoryConfiguration backup =
                    new StateXmlCodec().DeserializeConfiguration(
                        File.ReadAllBytes(
                            Path.Combine(
                                stateDirectory,
                                "config.xml.bak")));

                Assert.IsFalse(commit.IsSuccess);
                Assert.AreEqual(
                    ConfigurationCommitFailureCode.RecoveryRequired,
                    commit.FailureCode);
                Assert.IsTrue(recovered.IsSuccess);
                Assert.AreEqual(
                    30,
                    recovered.Configuration.LogRetentionDays);
                Assert.IsTrue(ConfigurationValueComparer.Equals(
                    recovered.Configuration,
                    backup));
                AssertJournalIsEmpty(stateDirectory);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void StoreFailsClosedForBackupWithoutPrimary()
        {
            string stateDirectory = CreateStateDirectory();
            try
            {
                byte[] contents = new StateXmlCodec().SerializeConfiguration(
                    CreateInitialConfiguration());
                File.WriteAllBytes(
                    Path.Combine(stateDirectory, "config.xml.bak"),
                    contents);

                ConfigurationLoadResult loaded =
                    new XmlServiceDirectoryConfigurationStore(
                        stateDirectory).Load();

                Assert.IsFalse(loaded.IsSuccess);
                Assert.AreEqual(
                    ConfigurationLoadFailureCode.RecoveryFailed,
                    loaded.FailureCode);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void StoreFailsClosedWhenBackupPathIsNotARegularFile()
        {
            string stateDirectory = CreateInitializedStateDirectory();
            try
            {
                Directory.CreateDirectory(
                    Path.Combine(stateDirectory, "config.xml.bak"));

                ConfigurationLoadResult loaded =
                    new XmlServiceDirectoryConfigurationStore(
                        stateDirectory).Load();

                Assert.IsFalse(loaded.IsSuccess);
                Assert.AreEqual(
                    ConfigurationLoadFailureCode.IoFailure,
                    loaded.FailureCode);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void StoreFailsClosedForCorruptBackupBesideValidPrimary()
        {
            string stateDirectory = CreateInitializedStateDirectory();
            try
            {
                var store = new XmlServiceDirectoryConfigurationStore(
                    stateDirectory);
                ConfigurationLoadResult initial = store.Load();
                Assert.IsTrue(initial.IsSuccess);
                Assert.IsTrue(store.Commit(
                    initial.Configuration,
                    initial.Configuration.WithLogRetentionDays(31)).IsSuccess);

                File.WriteAllBytes(
                    Path.Combine(stateDirectory, "config.xml.bak"),
                    StrictUtf8.GetBytes("not canonical XML"));

                ConfigurationLoadResult loaded =
                    new XmlServiceDirectoryConfigurationStore(
                        stateDirectory).Load();

                Assert.IsFalse(loaded.IsSuccess);
                Assert.AreEqual(
                    ConfigurationLoadFailureCode.InvalidData,
                    loaded.FailureCode);
                ServiceDirectoryConfiguration primary =
                    new StateXmlCodec().DeserializeConfiguration(
                        File.ReadAllBytes(
                            Path.Combine(stateDirectory, "config.xml")));
                Assert.AreEqual(31, primary.LogRetentionDays);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        [TestMethod]
        public void StoreDetectsExternalConfigurationMutation()
        {
            string stateDirectory = CreateInitializedStateDirectory();
            try
            {
                var store = new XmlServiceDirectoryConfigurationStore(
                    stateDirectory);
                ConfigurationLoadResult loaded = store.Load();
                ServiceDirectoryConfiguration external =
                    loaded.Configuration.WithLogRetentionDays(31);
                File.WriteAllBytes(
                    Path.Combine(stateDirectory, "config.xml"),
                    new StateXmlCodec().SerializeConfiguration(external));

                ConfigurationCommitResult commit = store.Commit(
                    loaded.Configuration,
                    loaded.Configuration.WithLogRetentionDays(32));

                Assert.IsFalse(commit.IsSuccess);
                Assert.AreEqual(
                    ConfigurationCommitFailureCode.RecoveryRequired,
                    commit.FailureCode);
                Assert.IsTrue(commit.RequiresReload);
            }
            finally
            {
                DeleteStateDirectory(stateDirectory);
            }
        }

        private static ServiceDirectoryConfiguration
            CreateInitialConfiguration()
        {
            return ServiceDirectoryConfiguration.CreateInitial(
                "10.20.30.40",
                InstanceId);
        }

        private static ServiceDirectoryConfiguration
            CreateUnpairedConfigurationWithEpoch(ulong epoch)
        {
            return new ServiceDirectoryConfiguration(
                "10.20.30.40",
                InstanceId,
                epoch,
                30,
                SynchronizationConfiguration.Unpaired(
                    LastSynchronizationStatus.NotRun(),
                    PeerNotificationStatus.NotRun()));
        }

        private static ServiceDirectoryConfiguration
            CreateEnabledConfiguration()
        {
            var lastSynchronization = new LastSynchronizationStatus(
                "OK",
                Utc(2),
                -2L);
            var notification = new PeerNotificationStatus(
                PeerNotificationOperation.Release,
                PeerNotificationResult.Confirmed,
                Utc(1));
            SynchronizationConfiguration synchronization =
                SynchronizationConfiguration.Enabled(
                    "http://10.20.30.41:21000",
                    PeerInstanceId,
                    7UL,
                    lastSynchronization,
                    notification);
            return new ServiceDirectoryConfiguration(
                "10.20.30.40",
                InstanceId,
                7UL,
                30,
                synchronization);
        }

        private static DateTime Utc(int hour)
        {
            return new DateTime(
                2026,
                7,
                18,
                hour,
                0,
                0,
                DateTimeKind.Utc);
        }

        private static string CreateInitializedStateDirectory()
        {
            string path = CreateStateDirectory();
            var store = new XmlServiceDirectoryConfigurationStore(path);
            Assert.AreEqual(
                ConfigurationLoadFailureCode.Missing,
                store.Load().FailureCode);
            Assert.IsTrue(store.Initialize(
                CreateInitialConfiguration()).IsSuccess);
            return path;
        }

        private static string CreateStateDirectory()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "dpai-sd-config-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteStateDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        private static void AssertJournalIsEmpty(string stateDirectory)
        {
            string journalPath = Path.Combine(stateDirectory, "journal");
            Assert.IsTrue(Directory.Exists(journalPath));
            Assert.AreEqual(
                0,
                Directory.GetFileSystemEntries(journalPath).Length);
        }

        private sealed class ThrowOnceFaultInjector
            : IRecoveryJournalFaultInjector
        {
            private readonly RecoveryJournalFaultPoint _faultPoint;
            private readonly StateFileTarget? _target;
            private bool _thrown;

            internal ThrowOnceFaultInjector(
                RecoveryJournalFaultPoint faultPoint,
                StateFileTarget? target)
            {
                _faultPoint = faultPoint;
                _target = target;
            }

            public void OnFault(
                RecoveryJournalFaultPoint faultPoint,
                StateFileTarget? target)
            {
                if (!_thrown
                    && faultPoint == _faultPoint
                    && target == _target)
                {
                    _thrown = true;
                    throw new IOException(
                        "Injected configuration persistence interruption.");
                }
            }
        }
    }
}
