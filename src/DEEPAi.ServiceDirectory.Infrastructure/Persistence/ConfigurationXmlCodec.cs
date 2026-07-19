using System;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using DEEPAi.ServiceDirectory.Infrastructure.Configuration;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence.SerializationModel;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal sealed partial class StateXmlCodec
    {
        private static readonly XmlSerializer ConfigurationSerializer =
            new XmlSerializer(typeof(ConfigurationDocument));

        internal byte[] SerializeConfiguration(
            ServiceDirectoryConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            var synchronization = configuration.Synchronization;
            var document = new ConfigurationDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                ListenAddress = configuration.ListenAddress,
                InstanceId = configuration.InstanceId.ToString("D"),
                LastPeerKeyEpoch = FormatUInt64(
                    configuration.LastPeerKeyEpoch),
                LogRetentionDays = configuration.LogRetentionDays.ToString(
                    CultureInfo.InvariantCulture),
                Sync = new SynchronizationConfigurationDocument
                {
                    State = FormatSynchronizationState(
                        synchronization.State),
                    PeerEndpoint = synchronization.PeerEndpoint,
                    PeerInstanceId = synchronization.PeerInstanceId.HasValue
                        ? synchronization.PeerInstanceId.Value.ToString("D")
                        : null,
                    KeyEpoch = synchronization.KeyEpoch.HasValue
                        ? FormatUInt64(synchronization.KeyEpoch.Value)
                        : null,
                    PairingId = synchronization.PairingId.HasValue
                        ? synchronization.PairingId.Value.ToString("D")
                        : null,
                    CommitExpiresUtc = synchronization.CommitExpiresUtc.HasValue
                        ? FormatUtc(synchronization.CommitExpiresUtc.Value)
                        : null,
                    LocalCommitConfirmed = FormatOptionalBoolean(
                        synchronization.LocalCommitConfirmed),
                    RemoteCommitConfirmed = FormatOptionalBoolean(
                        synchronization.RemoteCommitConfirmed),
                    LastResult = synchronization.LastSynchronization.Result,
                    LastSyncUtc = synchronization.LastSynchronization.LastSyncUtc.HasValue
                        ? FormatUtc(
                            synchronization.LastSynchronization.LastSyncUtc.Value)
                        : null,
                    ClockSkewSeconds =
                        synchronization.LastSynchronization.ClockSkewSeconds.HasValue
                            ? FormatInt64(
                                synchronization.LastSynchronization.ClockSkewSeconds.Value)
                            : null,
                    LastPeerNotificationOperation = FormatNotificationOperation(
                        synchronization.LastPeerNotification.Operation),
                    LastPeerNotificationResult = FormatNotificationResult(
                        synchronization.LastPeerNotification.Result),
                    LastPeerNotificationUtc =
                        synchronization.LastPeerNotification.NotificationUtc.HasValue
                            ? FormatUtc(
                                synchronization.LastPeerNotification.NotificationUtc.Value)
                            : null
                }
            };

            return SerializeDocument(
                document,
                ConfigurationSerializer,
                "config.xml");
        }

        internal ServiceDirectoryConfiguration DeserializeConfiguration(
            byte[] contents)
        {
            if (contents == null)
            {
                throw new ArgumentNullException(nameof(contents));
            }

            try
            {
                string xml = DecodeStrictUtf8(contents, "config.xml");
                ValidateConfigurationShape(xml);
                ConfigurationDocument document =
                    DeserializeDocument<ConfigurationDocument>(
                        xml,
                        ConfigurationSerializer,
                        "config.xml");
                ServiceDirectoryConfiguration configuration =
                    ToConfiguration(document);
                RequireCanonicalDocument(
                    contents,
                    SerializeConfiguration(configuration),
                    "config.xml");
                return configuration;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (ArgumentException exception)
            {
                throw InvalidStateXml(
                    "config.xml violates a configuration invariant.",
                    exception);
            }
            catch (InvalidOperationException exception)
            {
                throw InvalidStateXml(
                    "config.xml could not be deserialized.",
                    exception);
            }
        }

        private static ServiceDirectoryConfiguration ToConfiguration(
            ConfigurationDocument document)
        {
            if (document == null)
            {
                throw InvalidStateXml("config.xml is missing its root.");
            }

            RequireSchemaVersion(document.SchemaVersion, "config.xml");
            if (document.Sync == null)
            {
                throw InvalidStateXml("config.xml is missing Sync.");
            }

            Guid instanceId = ParseCanonicalGuid(
                document.InstanceId,
                "Config.InstanceId");
            ulong lastPeerKeyEpoch = ParseCanonicalUInt64(
                document.LastPeerKeyEpoch,
                "Config.LastPeerKeyEpoch");
            int logRetentionDays = ParseCanonicalInt32(
                document.LogRetentionDays,
                "Config.LogRetentionDays");
            SynchronizationConfiguration synchronization =
                ToSynchronizationConfiguration(document.Sync);

            return new ServiceDirectoryConfiguration(
                document.ListenAddress,
                instanceId,
                lastPeerKeyEpoch,
                logRetentionDays,
                synchronization);
        }

        private static SynchronizationConfiguration
            ToSynchronizationConfiguration(
                SynchronizationConfigurationDocument document)
        {
            DurableSynchronizationState state =
                ParseSynchronizationState(document.State);
            var lastSynchronization = new LastSynchronizationStatus(
                document.LastResult,
                ParseOptionalUtc(
                    document.LastSyncUtc,
                    "Config.Sync.LastSyncUtc"),
                ParseOptionalInt64(
                    document.ClockSkewSeconds,
                    "Config.Sync.ClockSkewSeconds"));
            var notification = new PeerNotificationStatus(
                ParseNotificationOperation(
                    document.LastPeerNotificationOperation),
                ParseNotificationResult(
                    document.LastPeerNotificationResult),
                ParseOptionalUtc(
                    document.LastPeerNotificationUtc,
                    "Config.Sync.LastPeerNotificationUtc"));

            switch (state)
            {
                case DurableSynchronizationState.Unpaired:
                    RequirePeerFieldsAbsent(document);
                    RequireCommitFieldsAbsent(document);
                    return SynchronizationConfiguration.Unpaired(
                        lastSynchronization,
                        notification);

                case DurableSynchronizationState.PairedPendingCommit:
                    return SynchronizationConfiguration.PairedPendingCommit(
                        RequireValue(
                            document.PeerEndpoint,
                            "Config.Sync.PeerEndpoint"),
                        ParseCanonicalGuid(
                            document.PeerInstanceId,
                            "Config.Sync.PeerInstanceId"),
                        ParseCanonicalUInt64(
                            document.KeyEpoch,
                            "Config.Sync.KeyEpoch"),
                        ParseCanonicalGuid(
                            document.PairingId,
                            "Config.Sync.PairingId"),
                        ParseCanonicalUtc(
                            document.CommitExpiresUtc,
                            "Config.Sync.CommitExpiresUtc"),
                        ParseRequiredBoolean(
                            document.LocalCommitConfirmed,
                            "Config.Sync.LocalCommitConfirmed"),
                        ParseRequiredBoolean(
                            document.RemoteCommitConfirmed,
                            "Config.Sync.RemoteCommitConfirmed"),
                        lastSynchronization,
                        notification);

                case DurableSynchronizationState.PairedDisabled:
                case DurableSynchronizationState.Enabled:
                    RequireCommitFieldsAbsent(document);
                    string endpoint = RequireValue(
                        document.PeerEndpoint,
                        "Config.Sync.PeerEndpoint");
                    Guid peerInstanceId = ParseCanonicalGuid(
                        document.PeerInstanceId,
                        "Config.Sync.PeerInstanceId");
                    ulong keyEpoch = ParseCanonicalUInt64(
                        document.KeyEpoch,
                        "Config.Sync.KeyEpoch");
                    return state == DurableSynchronizationState.Enabled
                        ? SynchronizationConfiguration.Enabled(
                            endpoint,
                            peerInstanceId,
                            keyEpoch,
                            lastSynchronization,
                            notification)
                        : SynchronizationConfiguration.PairedDisabled(
                            endpoint,
                            peerInstanceId,
                            keyEpoch,
                            lastSynchronization,
                            notification);

                default:
                    throw InvalidStateXml(
                        "Config.Sync.State is not a durable state.");
            }
        }

        private static void ValidateConfigurationShape(string xml)
        {
            var settings = CreateReaderSettings(xml.Length);
            try
            {
                using (var textReader = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(textReader, settings))
                {
                    bool rootSeen = false;
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.XmlDeclaration)
                        {
                            ValidateConfigurationDeclaration(reader);
                            continue;
                        }

                        if (reader.NodeType == XmlNodeType.Comment
                            || reader.NodeType == XmlNodeType.ProcessingInstruction)
                        {
                            throw InvalidStateXml(
                                "config.xml cannot contain comments or processing instructions.");
                        }

                        if (reader.NodeType != XmlNodeType.Element)
                        {
                            continue;
                        }

                        if (reader.Depth >= MaximumXmlDepth)
                        {
                            throw InvalidStateXml(
                                "config.xml exceeds the maximum XML depth.");
                        }

                        if (reader.Prefix.Length != 0
                            || reader.NamespaceURI.Length != 0)
                        {
                            throw InvalidStateXml(
                                "config.xml must not use XML namespaces or prefixes.");
                        }

                        if (reader.Depth == 0)
                        {
                            if (rootSeen
                                || !StringComparer.Ordinal.Equals(
                                    reader.LocalName,
                                    "Config"))
                            {
                                throw InvalidStateXml(
                                    "config.xml must have the Config root.");
                            }

                            rootSeen = true;
                            ValidateConfigurationRootAttributes(reader);
                        }
                        else if (reader.AttributeCount != 0)
                        {
                            throw InvalidStateXml(
                                "config.xml child elements cannot have attributes.");
                        }
                    }

                    if (!rootSeen)
                    {
                        throw InvalidStateXml("config.xml is empty.");
                    }
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (XmlException exception)
            {
                throw InvalidStateXml(
                    "config.xml is not valid XML.",
                    exception);
            }
        }

        private static void ValidateConfigurationDeclaration(XmlReader reader)
        {
            if (reader.AttributeCount != 2
                || !StringComparer.Ordinal.Equals(
                    reader.GetAttribute("version"),
                    "1.0")
                || !StringComparer.Ordinal.Equals(
                    reader.GetAttribute("encoding"),
                    "utf-8")
                || reader.GetAttribute("standalone") != null)
            {
                throw InvalidStateXml(
                    "config.xml must declare XML 1.0 UTF-8 without standalone.");
            }
        }

        private static void ValidateConfigurationRootAttributes(XmlReader reader)
        {
            if (reader.AttributeCount != 1
                || !StringComparer.Ordinal.Equals(
                    reader.GetAttribute("SchemaVersion"),
                    CurrentSchemaVersion))
            {
                throw InvalidStateXml(
                    "config.xml must use exact SchemaVersion=\"1\".");
            }

            reader.MoveToFirstAttribute();
            if (reader.Prefix.Length != 0
                || reader.NamespaceURI.Length != 0
                || !StringComparer.Ordinal.Equals(
                    reader.LocalName,
                    "SchemaVersion"))
            {
                throw InvalidStateXml(
                    "config.xml has an invalid root attribute.");
            }

            reader.MoveToElement();
        }

        private static string RequireValue(string value, string fieldName)
        {
            if (value == null)
            {
                throw InvalidStateXml(fieldName + " is required.");
            }

            return value;
        }

        private static void RequirePeerFieldsAbsent(
            SynchronizationConfigurationDocument document)
        {
            if (document.PeerEndpoint != null
                || document.PeerInstanceId != null
                || document.KeyEpoch != null)
            {
                throw InvalidStateXml(
                    "Unpaired config.xml cannot contain peer fields.");
            }
        }

        private static void RequireCommitFieldsAbsent(
            SynchronizationConfigurationDocument document)
        {
            if (document.PairingId != null
                || document.CommitExpiresUtc != null
                || document.LocalCommitConfirmed != null
                || document.RemoteCommitConfirmed != null)
            {
                throw InvalidStateXml(
                    "The durable synchronization state cannot contain commit fields.");
            }
        }

        private static DateTime? ParseOptionalUtc(
            string value,
            string fieldName)
        {
            return value == null
                ? (DateTime?)null
                : ParseCanonicalUtc(value, fieldName);
        }

        private static long? ParseOptionalInt64(
            string value,
            string fieldName)
        {
            if (value == null)
            {
                return null;
            }

            long parsed;
            if (!long.TryParse(
                    value,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out parsed)
                || !StringComparer.Ordinal.Equals(value, FormatInt64(parsed)))
            {
                throw InvalidStateXml(
                    fieldName + " is not a canonical signed integer.");
            }

            return parsed;
        }

        private static bool ParseRequiredBoolean(
            string value,
            string fieldName)
        {
            return ParseCanonicalBoolean(
                RequireValue(value, fieldName),
                fieldName);
        }

        private static DurableSynchronizationState ParseSynchronizationState(
            string value)
        {
            switch (value)
            {
                case "Unpaired":
                    return DurableSynchronizationState.Unpaired;
                case "PairedPendingCommit":
                    return DurableSynchronizationState.PairedPendingCommit;
                case "PairedDisabled":
                    return DurableSynchronizationState.PairedDisabled;
                case "Enabled":
                    return DurableSynchronizationState.Enabled;
                default:
                    throw InvalidStateXml(
                        "Config.Sync.State is not a supported durable state.");
            }
        }

        private static PeerNotificationOperation ParseNotificationOperation(
            string value)
        {
            switch (value)
            {
                case "NONE":
                    return PeerNotificationOperation.None;
                case "RELEASE":
                    return PeerNotificationOperation.Release;
                case "REVOKE":
                    return PeerNotificationOperation.Revoke;
                default:
                    throw InvalidStateXml(
                        "Config.Sync.LastPeerNotificationOperation is invalid.");
            }
        }

        private static PeerNotificationResult ParseNotificationResult(
            string value)
        {
            switch (value)
            {
                case "NOT_RUN":
                    return PeerNotificationResult.NotRun;
                case "CONFIRMED":
                    return PeerNotificationResult.Confirmed;
                case "UNCONFIRMED":
                    return PeerNotificationResult.Unconfirmed;
                case "NOT_REQUIRED":
                    return PeerNotificationResult.NotRequired;
                default:
                    throw InvalidStateXml(
                        "Config.Sync.LastPeerNotificationResult is invalid.");
            }
        }

        private static string FormatSynchronizationState(
            DurableSynchronizationState value)
        {
            switch (value)
            {
                case DurableSynchronizationState.Unpaired:
                    return "Unpaired";
                case DurableSynchronizationState.PairedPendingCommit:
                    return "PairedPendingCommit";
                case DurableSynchronizationState.PairedDisabled:
                    return "PairedDisabled";
                case DurableSynchronizationState.Enabled:
                    return "Enabled";
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        private static string FormatNotificationOperation(
            PeerNotificationOperation value)
        {
            switch (value)
            {
                case PeerNotificationOperation.None:
                    return "NONE";
                case PeerNotificationOperation.Release:
                    return "RELEASE";
                case PeerNotificationOperation.Revoke:
                    return "REVOKE";
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        private static string FormatNotificationResult(
            PeerNotificationResult value)
        {
            switch (value)
            {
                case PeerNotificationResult.NotRun:
                    return "NOT_RUN";
                case PeerNotificationResult.Confirmed:
                    return "CONFIRMED";
                case PeerNotificationResult.Unconfirmed:
                    return "UNCONFIRMED";
                case PeerNotificationResult.NotRequired:
                    return "NOT_REQUIRED";
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        private static string FormatOptionalBoolean(bool? value)
        {
            if (!value.HasValue)
            {
                return null;
            }

            return value.Value ? "true" : "false";
        }

        private static string FormatInt64(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
