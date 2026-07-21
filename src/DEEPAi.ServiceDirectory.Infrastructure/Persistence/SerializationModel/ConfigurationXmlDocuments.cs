using System.Xml.Serialization;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence.SerializationModel
{
    [XmlRoot("Config", Namespace = "", IsNullable = false)]
    public sealed class ConfigurationDocument
    {
        [XmlAttribute("SchemaVersion")]
        public string SchemaVersion { get; set; }

        [XmlElement("ListenAddress", Order = 0)]
        public string ListenAddress { get; set; }

        [XmlElement("DirectoryHostName", Order = 1)]
        public string DirectoryHostName { get; set; }

        [XmlElement("DirectoryIpv4Address", Order = 2)]
        public string DirectoryIpv4Address { get; set; }

        [XmlElement("InstanceId", Order = 3)]
        public string InstanceId { get; set; }

        [XmlElement("LastPeerKeyEpoch", Order = 4)]
        public string LastPeerKeyEpoch { get; set; }

        [XmlElement("LogRetentionDays", Order = 5)]
        public string LogRetentionDays { get; set; }

        [XmlElement("Sync", Order = 6)]
        public SynchronizationConfigurationDocument Sync { get; set; }
    }

    public sealed class SynchronizationConfigurationDocument
    {
        [XmlElement("State", Order = 0)]
        public string State { get; set; }

        [XmlElement("PeerEndpoint", Order = 1)]
        public string PeerEndpoint { get; set; }

        [XmlElement("PeerInstanceId", Order = 2)]
        public string PeerInstanceId { get; set; }

        [XmlElement("KeyEpoch", Order = 3)]
        public string KeyEpoch { get; set; }

        [XmlElement("PairingId", Order = 4)]
        public string PairingId { get; set; }

        [XmlElement("CommitExpiresUtc", Order = 5)]
        public string CommitExpiresUtc { get; set; }

        [XmlElement("LocalCommitConfirmed", Order = 6)]
        public string LocalCommitConfirmed { get; set; }

        [XmlElement("RemoteCommitConfirmed", Order = 7)]
        public string RemoteCommitConfirmed { get; set; }

        [XmlElement("LastResult", Order = 8)]
        public string LastResult { get; set; }

        [XmlElement("LastSyncUtc", Order = 9)]
        public string LastSyncUtc { get; set; }

        [XmlElement("ClockSkewSeconds", Order = 10)]
        public string ClockSkewSeconds { get; set; }

        [XmlElement("LastPeerNotificationOperation", Order = 11)]
        public string LastPeerNotificationOperation { get; set; }

        [XmlElement("LastPeerNotificationResult", Order = 12)]
        public string LastPeerNotificationResult { get; set; }

        [XmlElement("LastPeerNotificationUtc", Order = 13)]
        public string LastPeerNotificationUtc { get; set; }
    }
}
