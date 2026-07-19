using System.Xml.Serialization;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence.SerializationModel
{
    [XmlRoot("RecoveryJournal", Namespace = "")]
    public sealed class RecoveryJournalDocument
    {
        [XmlAttribute("SchemaVersion")]
        public string SchemaVersion { get; set; }

        [XmlAttribute("TransactionId")]
        public string TransactionId { get; set; }

        [XmlAttribute("Phase")]
        public string Phase { get; set; }

        [XmlElement("Entry", Order = 0)]
        public RecoveryJournalEntryDocument[] Entries { get; set; }
    }

    public sealed class RecoveryJournalEntryDocument
    {
        [XmlAttribute("Target")]
        public string Target { get; set; }

        [XmlAttribute("BeforeExists")]
        public string BeforeExists { get; set; }

        [XmlAttribute("AfterExists")]
        public string AfterExists { get; set; }

        [XmlAttribute("BeforeSha256")]
        public string BeforeSha256 { get; set; }

        [XmlAttribute("AfterSha256")]
        public string AfterSha256 { get; set; }
    }
}
