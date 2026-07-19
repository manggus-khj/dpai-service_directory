using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal static class PeerMessageCanonicalizer
    {
        private static readonly Encoding Ascii = Encoding.ASCII;
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        public static byte[] CreateRequest(
            PeerRequestAuthenticationData request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            byte[][] fields = null;
            try
            {
                fields = new[]
                {
                    Ascii.GetBytes("request"),
                    GetCanonicalGuid(request.SenderInstanceId),
                    GetCanonicalGuid(request.ReceiverInstanceId),
                    GetCanonicalUnsignedDecimal(request.KeyEpoch),
                    GetCanonicalSessionId(request.CopySessionId()),
                    Ascii.GetBytes(request.Method),
                    StrictUtf8.GetBytes(request.RequestTarget.Value),
                    Ascii.GetBytes(request.ContentType),
                    ComputeBodyHash(request.CopyBody()),
                    Ascii.GetBytes(
                        PeerAuthenticationContract.FormatTimestamp(
                            request.Timestamp)),
                    request.CopyNonce()
                };
                return JoinLengthPrefixed(fields);
            }
            finally
            {
                Clear(fields);
            }
        }

        public static byte[] CreateResponse(
            PeerResponseAuthenticationData response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            byte[][] fields = null;
            try
            {
                fields = new[]
                {
                    Ascii.GetBytes("response"),
                    GetCanonicalGuid(response.SenderInstanceId),
                    GetCanonicalGuid(response.ReceiverInstanceId),
                    GetCanonicalUnsignedDecimal(response.KeyEpoch),
                    GetCanonicalSessionId(response.CopySessionId()),
                    Ascii.GetBytes(response.RequestMethod),
                    StrictUtf8.GetBytes(response.RequestTarget.Value),
                    GetCanonicalUnsignedDecimal((ulong)response.HttpStatus),
                    Ascii.GetBytes(response.ContentType),
                    ComputeBodyHash(response.CopyBody()),
                    Ascii.GetBytes(
                        PeerAuthenticationContract.FormatTimestamp(
                            response.Timestamp)),
                    response.CopyResponseNonce(),
                    response.CopyRequestNonce()
                };
                return JoinLengthPrefixed(fields);
            }
            finally
            {
                Clear(fields);
            }
        }

        private static byte[] ComputeBodyHash(byte[] body)
        {
            try
            {
                using (SHA256 sha256 = SHA256.Create())
                {
                    return sha256.ComputeHash(body);
                }
            }
            finally
            {
                Array.Clear(body, 0, body.Length);
            }
        }

        private static byte[] GetCanonicalGuid(Guid value)
        {
            return Ascii.GetBytes(value.ToString("D").ToLowerInvariant());
        }

        private static byte[] GetCanonicalSessionId(byte[] sessionId)
        {
            try
            {
                return sessionId.Length == 0
                    ? new byte[0]
                    : Ascii.GetBytes(Convert.ToBase64String(sessionId));
            }
            finally
            {
                Array.Clear(sessionId, 0, sessionId.Length);
            }
        }

        private static byte[] GetCanonicalUnsignedDecimal(ulong value)
        {
            return Ascii.GetBytes(
                value.ToString(CultureInfo.InvariantCulture));
        }

        private static byte[] JoinLengthPrefixed(byte[][] fields)
        {
            int totalLength = 0;
            checked
            {
                for (int index = 0; index < fields.Length; index++)
                {
                    if (fields[index] == null)
                    {
                        throw new ArgumentException(
                            "Canonical fields must not be null.",
                            nameof(fields));
                    }

                    totalLength += sizeof(uint) + fields[index].Length;
                }
            }

            var result = new byte[totalLength];
            int offset = 0;
            for (int index = 0; index < fields.Length; index++)
            {
                WriteUInt32BigEndian(
                    result,
                    offset,
                    (uint)fields[index].Length);
                offset += sizeof(uint);
                Buffer.BlockCopy(
                    fields[index],
                    0,
                    result,
                    offset,
                    fields[index].Length);
                offset += fields[index].Length;
            }

            return result;
        }

        private static void WriteUInt32BigEndian(
            byte[] destination,
            int offset,
            uint value)
        {
            destination[offset] = (byte)(value >> 24);
            destination[offset + 1] = (byte)(value >> 16);
            destination[offset + 2] = (byte)(value >> 8);
            destination[offset + 3] = (byte)value;
        }

        private static void Clear(byte[][] values)
        {
            if (values == null)
            {
                return;
            }

            for (int index = 0; index < values.Length; index++)
            {
                if (values[index] != null)
                {
                    Array.Clear(values[index], 0, values[index].Length);
                }
            }
        }
    }
}
