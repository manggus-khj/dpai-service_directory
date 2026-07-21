using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using DEEPAi.ServiceDirectory.Infrastructure.Persistence.SerializationModel;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal enum RecoveryJournalPhase
    {
        Prepared = 0,
        Committed = 1
    }

    internal sealed class RecoveryJournalEntry
    {
        internal RecoveryJournalEntry(
            StateFileTarget target,
            bool beforeExists,
            bool afterExists,
            string beforeSha256,
            string afterSha256)
        {
            Target = target;
            BeforeExists = beforeExists;
            AfterExists = afterExists;
            BeforeSha256 = beforeSha256;
            AfterSha256 = afterSha256;
        }

        internal StateFileTarget Target { get; }

        internal bool BeforeExists { get; }

        internal bool AfterExists { get; }

        internal string BeforeSha256 { get; }

        internal string AfterSha256 { get; }
    }

    internal sealed class RecoveryJournalState
    {
        internal RecoveryJournalState(
            Guid transactionId,
            RecoveryJournalPhase phase,
            IReadOnlyList<RecoveryJournalEntry> entries)
        {
            TransactionId = transactionId;
            Phase = phase;
            Entries = entries;
        }

        internal Guid TransactionId { get; }

        internal RecoveryJournalPhase Phase { get; }

        internal IReadOnlyList<RecoveryJournalEntry> Entries { get; }
    }

    internal sealed class RecoveryJournalCodec
    {
        internal const int MaximumJournalBytes = 16 * 1024;
        private const string SchemaVersion = "1";
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private static readonly XmlSerializer Serializer =
            new XmlSerializer(typeof(RecoveryJournalDocument));

        internal byte[] Serialize(RecoveryJournalState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            ValidateState(state);
            var document = new RecoveryJournalDocument
            {
                SchemaVersion = SchemaVersion,
                TransactionId = state.TransactionId.ToString("D"),
                Phase = state.Phase == RecoveryJournalPhase.Prepared
                    ? "PREPARED"
                    : "COMMITTED",
                Entries = CreateEntryDocuments(state.Entries)
            };

            var settings = new XmlWriterSettings
            {
                Encoding = StrictUtf8,
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\r\n",
                NewLineHandling = NewLineHandling.None,
                OmitXmlDeclaration = false,
                CloseOutput = false
            };
            var namespaces = new XmlSerializerNamespaces();
            namespaces.Add(string.Empty, string.Empty);
            using (var stream = new MemoryStream())
            {
                using (XmlWriter writer = XmlWriter.Create(stream, settings))
                {
                    Serializer.Serialize(writer, document, namespaces);
                }

                if (stream.Length > MaximumJournalBytes)
                {
                    throw new InvalidDataException(
                        "The recovery journal exceeds its size limit.");
                }

                return EnsureFinalCrLf(stream.ToArray());
            }
        }

        internal RecoveryJournalState Deserialize(
            byte[] contents,
            Guid expectedTransactionId)
        {
            if (contents == null)
            {
                throw new ArgumentNullException(nameof(contents));
            }

            if (expectedTransactionId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Expected transaction ID must not be empty.",
                    nameof(expectedTransactionId));
            }

            if (contents.Length == 0 || contents.Length > MaximumJournalBytes)
            {
                throw new InvalidDataException(
                    "The recovery journal size is invalid.");
            }

            if (contents.Length >= 3
                && contents[0] == 0xef
                && contents[1] == 0xbb
                && contents[2] == 0xbf)
            {
                throw new InvalidDataException(
                    "The recovery journal must not contain a UTF-8 BOM.");
            }

            string xml;
            try
            {
                xml = StrictUtf8.GetString(contents);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    "The recovery journal is not strict UTF-8.",
                    exception);
            }

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                IgnoreWhitespace = false,
                CloseInput = true
            };
            RecoveryJournalDocument document;
            try
            {
                using (var textReader = new StringReader(xml))
                using (XmlReader reader = XmlReader.Create(textReader, settings))
                {
                    var events = new XmlDeserializationEvents();
                    events.OnUnknownAttribute += RejectUnknownAttribute;
                    events.OnUnknownElement += RejectUnknownElement;
                    events.OnUnknownNode += RejectUnknownNode;
                    document = (RecoveryJournalDocument)Serializer.Deserialize(
                        reader,
                        events);
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is InvalidOperationException
                    || exception is XmlException)
            {
                throw new InvalidDataException(
                    "The recovery journal XML is invalid.",
                    exception);
            }

            RecoveryJournalState state = ConvertDocument(
                document,
                expectedTransactionId);
            RequireCanonical(contents, Serialize(state));
            return state;
        }

        private static byte[] EnsureFinalCrLf(byte[] contents)
        {
            if (contents.Length >= 2
                && contents[contents.Length - 2] == (byte)'\r'
                && contents[contents.Length - 1] == (byte)'\n')
            {
                return contents;
            }

            var canonical = new byte[contents.Length + 2];
            Buffer.BlockCopy(contents, 0, canonical, 0, contents.Length);
            canonical[canonical.Length - 2] = (byte)'\r';
            canonical[canonical.Length - 1] = (byte)'\n';
            return canonical;
        }

        private static void RequireCanonical(
            byte[] supplied,
            byte[] canonical)
        {
            if (supplied.Length != canonical.Length)
            {
                throw new InvalidDataException(
                    "The recovery journal is not in canonical v1 form.");
            }

            for (int index = 0; index < supplied.Length; index++)
            {
                if (supplied[index] != canonical[index])
                {
                    throw new InvalidDataException(
                        "The recovery journal is not in canonical v1 form.");
                }
            }
        }

        private static RecoveryJournalState ConvertDocument(
            RecoveryJournalDocument document,
            Guid expectedTransactionId)
        {
            if (document == null
                || !StringComparer.Ordinal.Equals(
                    document.SchemaVersion,
                    SchemaVersion))
            {
                throw new InvalidDataException(
                    "The recovery journal schema version is invalid.");
            }

            Guid transactionId;
            if (!TryParseCanonicalGuid(document.TransactionId, out transactionId)
                || transactionId != expectedTransactionId)
            {
                throw new InvalidDataException(
                    "The recovery journal transaction ID is invalid.");
            }

            RecoveryJournalPhase phase;
            if (StringComparer.Ordinal.Equals(document.Phase, "PREPARED"))
            {
                phase = RecoveryJournalPhase.Prepared;
            }
            else if (StringComparer.Ordinal.Equals(document.Phase, "COMMITTED"))
            {
                phase = RecoveryJournalPhase.Committed;
            }
            else
            {
                throw new InvalidDataException(
                    "The recovery journal phase is invalid.");
            }

            RecoveryJournalEntryDocument[] entryDocuments = document.Entries;
            if (entryDocuments == null
                || entryDocuments.Length == 0
                || entryDocuments.Length > StateFileTargets.All.Count)
            {
                throw new InvalidDataException(
                    "The recovery journal entry count is invalid.");
            }

            var entries = new List<RecoveryJournalEntry>(entryDocuments.Length);
            int previousOrder = -1;
            foreach (RecoveryJournalEntryDocument entryDocument in entryDocuments)
            {
                if (entryDocument == null)
                {
                    throw new InvalidDataException(
                        "The recovery journal contains an empty entry.");
                }

                StateFileTarget target;
                if (!StateFileTargets.TryParseJournalName(
                        entryDocument.Target,
                        out target)
                    || (int)target <= previousOrder)
                {
                    throw new InvalidDataException(
                        "Recovery journal targets must be unique and canonically ordered.");
                }

                previousOrder = (int)target;
                bool beforeExists = ParseCanonicalBoolean(
                    entryDocument.BeforeExists,
                    "BeforeExists");
                bool afterExists = ParseCanonicalBoolean(
                    entryDocument.AfterExists,
                    "AfterExists");
                ValidateImageTransition(beforeExists, afterExists);
                ValidateHashPresence(
                    beforeExists,
                    entryDocument.BeforeSha256,
                    "BeforeSha256");
                ValidateHashPresence(
                    afterExists,
                    entryDocument.AfterSha256,
                    "AfterSha256");
                entries.Add(new RecoveryJournalEntry(
                    target,
                    beforeExists,
                    afterExists,
                    entryDocument.BeforeSha256,
                    entryDocument.AfterSha256));
            }

            return new RecoveryJournalState(
                transactionId,
                phase,
                entries.AsReadOnly());
        }

        private static RecoveryJournalEntryDocument[] CreateEntryDocuments(
            IReadOnlyList<RecoveryJournalEntry> entries)
        {
            var documents = new RecoveryJournalEntryDocument[entries.Count];
            for (int index = 0; index < entries.Count; index++)
            {
                RecoveryJournalEntry entry = entries[index];
                documents[index] = new RecoveryJournalEntryDocument
                {
                    Target = StateFileTargets.Get(entry.Target).JournalName,
                    BeforeExists = entry.BeforeExists ? "true" : "false",
                    AfterExists = entry.AfterExists ? "true" : "false",
                    BeforeSha256 = entry.BeforeSha256,
                    AfterSha256 = entry.AfterSha256
                };
            }

            return documents;
        }

        private static void ValidateState(RecoveryJournalState state)
        {
            if (state.TransactionId == Guid.Empty)
            {
                throw new InvalidDataException(
                    "Recovery journal transaction ID must not be empty.");
            }

            if (!Enum.IsDefined(typeof(RecoveryJournalPhase), state.Phase))
            {
                throw new InvalidDataException(
                    "Recovery journal phase is invalid.");
            }

            if (state.Entries == null
                || state.Entries.Count == 0
                || state.Entries.Count > StateFileTargets.All.Count)
            {
                throw new InvalidDataException(
                    "Recovery journal entry count is invalid.");
            }

            int previousOrder = -1;
            foreach (RecoveryJournalEntry entry in state.Entries)
            {
                if (entry == null
                    || !Enum.IsDefined(
                        typeof(StateFileTarget),
                        entry.Target)
                    || (int)entry.Target <= previousOrder)
                {
                    throw new InvalidDataException(
                        "Recovery journal targets must be unique and canonically ordered.");
                }

                previousOrder = (int)entry.Target;
                ValidateImageTransition(
                    entry.BeforeExists,
                    entry.AfterExists);
                ValidateHashPresence(
                    entry.BeforeExists,
                    entry.BeforeSha256,
                    "BeforeSha256");
                ValidateHashPresence(
                    entry.AfterExists,
                    entry.AfterSha256,
                    "AfterSha256");
            }
        }

        private static void ValidateImageTransition(
            bool beforeExists,
            bool afterExists)
        {
            if (!beforeExists && !afterExists)
            {
                throw new InvalidDataException(
                    "A recovery journal entry must contain a before or after image.");
            }
        }

        private static bool ParseCanonicalBoolean(string value, string fieldName)
        {
            if (StringComparer.Ordinal.Equals(value, "true"))
            {
                return true;
            }

            if (StringComparer.Ordinal.Equals(value, "false"))
            {
                return false;
            }

            throw new InvalidDataException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Recovery journal {0} is not a canonical boolean.",
                    fieldName));
        }

        private static void ValidateHashPresence(
            bool exists,
            string value,
            string fieldName)
        {
            if (!exists)
            {
                if (value != null)
                {
                    throw new InvalidDataException(
                        fieldName + " must be absent when its image does not exist.");
                }

                return;
            }

            if (value == null || value.Length != 64)
            {
                throw new InvalidDataException(
                    fieldName + " must be a 64-character lowercase SHA-256 value.");
            }

            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                bool valid = (current >= '0' && current <= '9')
                    || (current >= 'a' && current <= 'f');
                if (!valid)
                {
                    throw new InvalidDataException(
                        fieldName + " must be lowercase hexadecimal.");
                }
            }
        }

        private static bool TryParseCanonicalGuid(string value, out Guid guid)
        {
            return Guid.TryParseExact(value, "D", out guid)
                && StringComparer.Ordinal.Equals(value, guid.ToString("D"));
        }

        private static void RejectUnknownAttribute(
            object sender,
            XmlAttributeEventArgs eventArgs)
        {
            throw new InvalidDataException(
                "The recovery journal contains an unknown attribute.");
        }

        private static void RejectUnknownElement(
            object sender,
            XmlElementEventArgs eventArgs)
        {
            throw new InvalidDataException(
                "The recovery journal contains an unknown element.");
        }

        private static void RejectUnknownNode(
            object sender,
            XmlNodeEventArgs eventArgs)
        {
            if (eventArgs.NodeType == XmlNodeType.Whitespace
                || eventArgs.NodeType == XmlNodeType.SignificantWhitespace)
            {
                return;
            }

            throw new InvalidDataException(
                "The recovery journal contains an unknown XML node.");
        }
    }
}
