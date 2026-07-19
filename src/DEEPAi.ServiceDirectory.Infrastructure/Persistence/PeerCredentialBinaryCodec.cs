using System;
using System.Globalization;
using System.IO;
using System.Text;
using DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol;

namespace DEEPAi.ServiceDirectory.Infrastructure.Persistence
{
    internal sealed class PeerCredentialBinaryCodec
    {
        internal const int MaximumPlaintextBytes = 64 * 1024;

        private const ushort SchemaVersion = 1;
        private const string UtcTimestampFormat =
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
        private const int MaximumTextFieldBytes = 128;

        private static readonly byte[] Magic =
            Encoding.ASCII.GetBytes("DPAISDPC");
        private static readonly Encoding StrictAscii =
            Encoding.GetEncoding(
                Encoding.ASCII.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);

        // The returned plaintext contains pairRoot. Its owner must clear it
        // immediately after DPAPI protection or any failed persistence step.
        internal byte[] Serialize(PairedPeerCredential credential)
        {
            if (credential == null)
            {
                throw new ArgumentNullException(nameof(credential));
            }

            byte[] transcriptHash = null;
            byte[] pairRoot = null;
            byte[] localEvidence = null;
            byte[] remoteEvidence = null;
            try
            {
                transcriptHash = credential.CopyTranscriptHash();
                pairRoot = credential.CopyPairRoot();
                localEvidence = SerializeEvidence(
                    credential.LocalCommitEvidence);
                remoteEvidence = SerializeEvidence(
                    credential.RemoteCommitEvidence);

                using (var stream = new MemoryStream())
                {
                    stream.Write(Magic, 0, Magic.Length);
                    WriteUInt16BigEndian(stream, SchemaVersion);
                    WriteAsciiField(
                        stream,
                        FormatState(credential.State));
                    WriteAsciiField(
                        stream,
                        FormatRole(credential.LocalRole));
                    WriteAsciiField(
                        stream,
                        credential.PairingId.ToString("D"));
                    WriteAsciiField(
                        stream,
                        credential.LocalInstanceId.ToString("D"));
                    WriteAsciiField(
                        stream,
                        credential.PeerInstanceId.ToString("D"));
                    WriteAsciiField(stream, credential.LocalEndpoint);
                    WriteAsciiField(stream, credential.PeerEndpoint);
                    WriteAsciiField(
                        stream,
                        credential.KeyEpoch.ToString(
                            CultureInfo.InvariantCulture));
                    WriteField(stream, transcriptHash);
                    WriteField(stream, pairRoot);
                    WriteAsciiField(
                        stream,
                        credential.CommitExpiresUtc.ToString(
                            UtcTimestampFormat,
                            CultureInfo.InvariantCulture));
                    WriteAsciiField(
                        stream,
                        credential.LocalCommitConfirmed
                            ? "true"
                            : "false");
                    WriteAsciiField(
                        stream,
                        credential.RemoteCommitConfirmed
                            ? "true"
                            : "false");
                    WriteField(stream, localEvidence);
                    WriteField(stream, remoteEvidence);

                    if (stream.Length > MaximumPlaintextBytes)
                    {
                        throw new InvalidDataException(
                            "The peer credential plaintext exceeds its fixed size limit.");
                    }

                    return stream.ToArray();
                }
            }
            finally
            {
                Clear(transcriptHash);
                Clear(pairRoot);
                Clear(localEvidence);
                Clear(remoteEvidence);
            }
        }

        internal PairedPeerCredential Deserialize(byte[] plaintext)
        {
            if (plaintext == null)
            {
                throw new ArgumentNullException(nameof(plaintext));
            }

            if (plaintext.Length == 0
                || plaintext.Length > MaximumPlaintextBytes)
            {
                throw new InvalidDataException(
                    "The peer credential plaintext has an invalid size.");
            }

            byte[] transcriptHash = null;
            byte[] pairRoot = null;
            byte[] localEvidenceBytes = null;
            byte[] remoteEvidenceBytes = null;
            PairingCommitEvidence localEvidence = null;
            PairingCommitEvidence remoteEvidence = null;
            try
            {
                var reader = new BoundedReader(plaintext);
                reader.RequireMagic(Magic);
                ushort version = reader.ReadUInt16BigEndian();
                if (version != SchemaVersion)
                {
                    throw new InvalidDataException(
                        "The peer credential schema version is not supported.");
                }

                DurablePeerCredentialState state = ParseState(
                    reader.ReadAsciiField(MaximumTextFieldBytes));
                PairingRole localRole = ParseRole(
                    reader.ReadAsciiField(MaximumTextFieldBytes));
                Guid pairingId = ParseGuid(
                    reader.ReadAsciiField(MaximumTextFieldBytes),
                    "PairingId");
                Guid localInstanceId = ParseGuid(
                    reader.ReadAsciiField(MaximumTextFieldBytes),
                    "LocalInstanceId");
                Guid peerInstanceId = ParseGuid(
                    reader.ReadAsciiField(MaximumTextFieldBytes),
                    "PeerInstanceId");
                string localEndpoint = reader.ReadAsciiField(
                    MaximumTextFieldBytes);
                string peerEndpoint = reader.ReadAsciiField(
                    MaximumTextFieldBytes);
                ulong keyEpoch = ParseUInt64(
                    reader.ReadAsciiField(MaximumTextFieldBytes),
                    "KeyEpoch");
                transcriptHash = reader.ReadField(32, 32, false);
                pairRoot = reader.ReadField(32, 32, false);
                DateTime commitExpiresUtc = ParseUtc(
                    reader.ReadAsciiField(MaximumTextFieldBytes),
                    "CommitExpiresUtc");
                bool localCommitConfirmed = ParseBoolean(
                    reader.ReadAsciiField(MaximumTextFieldBytes),
                    "LocalCommitConfirmed");
                bool remoteCommitConfirmed = ParseBoolean(
                    reader.ReadAsciiField(MaximumTextFieldBytes),
                    "RemoteCommitConfirmed");
                localEvidenceBytes = reader.ReadField(
                    0,
                    PairingCommitEvidence.MaximumResponseBodyBytes + 128,
                    true);
                remoteEvidenceBytes = reader.ReadField(
                    0,
                    PairingCommitEvidence.MaximumResponseBodyBytes + 128,
                    true);
                reader.RequireEnd();

                localEvidence = DeserializeEvidence(
                    localEvidenceBytes);
                remoteEvidence = DeserializeEvidence(
                    remoteEvidenceBytes);
                return new PairedPeerCredential(
                    state,
                    localRole,
                    pairingId,
                    localInstanceId,
                    peerInstanceId,
                    localEndpoint,
                    peerEndpoint,
                    keyEpoch,
                    transcriptHash,
                    pairRoot,
                    commitExpiresUtc,
                    localCommitConfirmed,
                    remoteCommitConfirmed,
                    localEvidence,
                    remoteEvidence);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is ArgumentException
                    || exception is FormatException
                    || exception is OverflowException)
            {
                throw new InvalidDataException(
                    "The peer credential violates a durable state invariant.",
                    exception);
            }
            finally
            {
                Clear(transcriptHash);
                Clear(pairRoot);
                Clear(localEvidenceBytes);
                Clear(remoteEvidenceBytes);
                if (localEvidence != null)
                {
                    localEvidence.Dispose();
                }

                if (remoteEvidence != null)
                {
                    remoteEvidence.Dispose();
                }
            }
        }

        private static byte[] SerializeEvidence(
            PairingCommitEvidence evidence)
        {
            if (evidence == null)
            {
                return Array.Empty<byte>();
            }

            byte[] requestMac = null;
            byte[] responseBody = null;
            byte[] responseMac = null;
            try
            {
                requestMac = evidence.CopyRequestMac();
                responseBody = evidence.CopyResponseBody();
                responseMac = evidence.CopyResponseMac();
                using (var stream = new MemoryStream())
                {
                    WriteField(stream, requestMac);
                    WriteAsciiField(
                        stream,
                        evidence.ResponseStatusCode.ToString(
                            CultureInfo.InvariantCulture));
                    WriteField(stream, responseBody);
                    WriteField(stream, responseMac);
                    return stream.ToArray();
                }
            }
            finally
            {
                Clear(requestMac);
                Clear(responseBody);
                Clear(responseMac);
            }
        }

        private static PairingCommitEvidence DeserializeEvidence(
            byte[] encoded)
        {
            if (encoded == null || encoded.Length == 0)
            {
                return null;
            }

            byte[] requestMac = null;
            byte[] responseBody = null;
            byte[] responseMac = null;
            try
            {
                var reader = new BoundedReader(encoded);
                requestMac = reader.ReadField(32, 32, false);
                int responseStatusCode = ParseInt32(
                    reader.ReadAsciiField(3),
                    "CommitResponseStatusCode");
                responseBody = reader.ReadField(
                    1,
                    PairingCommitEvidence.MaximumResponseBodyBytes,
                    false);
                responseMac = reader.ReadField(32, 32, false);
                reader.RequireEnd();
                return new PairingCommitEvidence(
                    requestMac,
                    responseStatusCode,
                    responseBody,
                    responseMac);
            }
            finally
            {
                Clear(requestMac);
                Clear(responseBody);
                Clear(responseMac);
            }
        }

        private static void WriteAsciiField(Stream stream, string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            byte[] encoded = null;
            try
            {
                encoded = StrictAscii.GetBytes(value);
                if (encoded.Length == 0
                    || encoded.Length > MaximumTextFieldBytes)
                {
                    throw new InvalidDataException(
                        "A peer credential text field has an invalid length.");
                }

                WriteField(stream, encoded);
            }
            finally
            {
                Clear(encoded);
            }
        }

        private static void WriteField(Stream stream, byte[] value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            WriteUInt32BigEndian(stream, checked((uint)value.Length));
            stream.Write(value, 0, value.Length);
        }

        private static void WriteUInt16BigEndian(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteUInt32BigEndian(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static DurablePeerCredentialState ParseState(string value)
        {
            switch (value)
            {
                case "PairedPendingCommit":
                    return DurablePeerCredentialState.PairedPendingCommit;
                case "PairedDisabled":
                    return DurablePeerCredentialState.PairedDisabled;
                case "Enabled":
                    return DurablePeerCredentialState.Enabled;
                default:
                    throw new InvalidDataException(
                        "The durable peer credential state is invalid.");
            }
        }

        private static string FormatState(DurablePeerCredentialState value)
        {
            switch (value)
            {
                case DurablePeerCredentialState.PairedPendingCommit:
                    return "PairedPendingCommit";
                case DurablePeerCredentialState.PairedDisabled:
                    return "PairedDisabled";
                case DurablePeerCredentialState.Enabled:
                    return "Enabled";
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        private static PairingRole ParseRole(string value)
        {
            switch (value)
            {
                case "initiator":
                    return PairingRole.Initiator;
                case "responder":
                    return PairingRole.Responder;
                default:
                    throw new InvalidDataException(
                        "The durable pairing role is invalid.");
            }
        }

        private static string FormatRole(PairingRole value)
        {
            switch (value)
            {
                case PairingRole.Initiator:
                    return "initiator";
                case PairingRole.Responder:
                    return "responder";
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        private static Guid ParseGuid(string value, string fieldName)
        {
            Guid parsed;
            if (!Guid.TryParseExact(value, "D", out parsed)
                || parsed == Guid.Empty
                || !StringComparer.Ordinal.Equals(
                    value,
                    parsed.ToString("D")))
            {
                throw new InvalidDataException(
                    fieldName + " is not a canonical non-empty GUID.");
            }

            return parsed;
        }

        private static ulong ParseUInt64(string value, string fieldName)
        {
            ulong parsed;
            if (!ulong.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed)
                || parsed == 0
                || !StringComparer.Ordinal.Equals(
                    value,
                    parsed.ToString(CultureInfo.InvariantCulture)))
            {
                throw new InvalidDataException(
                    fieldName + " is not a canonical positive unsigned integer.");
            }

            return parsed;
        }

        private static int ParseInt32(string value, string fieldName)
        {
            int parsed;
            if (!int.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed)
                || !StringComparer.Ordinal.Equals(
                    value,
                    parsed.ToString(CultureInfo.InvariantCulture)))
            {
                throw new InvalidDataException(
                    fieldName + " is not a canonical integer.");
            }

            return parsed;
        }

        private static bool ParseBoolean(string value, string fieldName)
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
                fieldName + " is not a canonical boolean.");
        }

        private static DateTime ParseUtc(string value, string fieldName)
        {
            DateTime parsed;
            if (!DateTime.TryParseExact(
                    value,
                    UtcTimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal
                        | DateTimeStyles.AdjustToUniversal,
                    out parsed)
                || parsed.Kind != DateTimeKind.Utc
                || !StringComparer.Ordinal.Equals(
                    value,
                    parsed.ToString(
                        UtcTimestampFormat,
                        CultureInfo.InvariantCulture)))
            {
                throw new InvalidDataException(
                    fieldName + " is not a canonical UTC timestamp.");
            }

            return parsed;
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }

        private sealed class BoundedReader
        {
            private readonly byte[] _contents;
            private int _offset;

            internal BoundedReader(byte[] contents)
            {
                _contents = contents;
            }

            internal void RequireMagic(byte[] expected)
            {
                RequireRemaining(expected.Length);
                for (int index = 0; index < expected.Length; index++)
                {
                    if (_contents[_offset + index] != expected[index])
                    {
                        throw new InvalidDataException(
                            "The peer credential magic value is invalid.");
                    }
                }

                _offset += expected.Length;
            }

            internal ushort ReadUInt16BigEndian()
            {
                RequireRemaining(2);
                ushort value = (ushort)((_contents[_offset] << 8)
                    | _contents[_offset + 1]);
                _offset += 2;
                return value;
            }

            internal string ReadAsciiField(int maximumBytes)
            {
                byte[] encoded = null;
                try
                {
                    encoded = ReadField(1, maximumBytes, false);
                    return StrictAscii.GetString(encoded);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException(
                        "A peer credential text field is not strict ASCII.",
                        exception);
                }
                finally
                {
                    Clear(encoded);
                }
            }

            internal byte[] ReadField(
                int minimumBytes,
                int maximumBytes,
                bool allowEmpty)
            {
                uint encodedLength = ReadUInt32BigEndian();
                if (encodedLength > int.MaxValue)
                {
                    throw new InvalidDataException(
                        "A peer credential field length is too large.");
                }

                int length = (int)encodedLength;
                if ((!allowEmpty && length == 0)
                    || length < minimumBytes
                    || length > maximumBytes)
                {
                    throw new InvalidDataException(
                        "A peer credential field length is outside its allowed bounds.");
                }

                RequireRemaining(length);
                var result = new byte[length];
                Buffer.BlockCopy(
                    _contents,
                    _offset,
                    result,
                    0,
                    length);
                _offset += length;
                return result;
            }

            internal void RequireEnd()
            {
                if (_offset != _contents.Length)
                {
                    throw new InvalidDataException(
                        "The peer credential contains trailing data.");
                }
            }

            private uint ReadUInt32BigEndian()
            {
                RequireRemaining(4);
                uint value = ((uint)_contents[_offset] << 24)
                    | ((uint)_contents[_offset + 1] << 16)
                    | ((uint)_contents[_offset + 2] << 8)
                    | _contents[_offset + 3];
                _offset += 4;
                return value;
            }

            private void RequireRemaining(int count)
            {
                if (count < 0
                    || _offset > _contents.Length - count)
                {
                    throw new InvalidDataException(
                        "The peer credential ended inside a framed field.");
                }
            }
        }
    }
}
