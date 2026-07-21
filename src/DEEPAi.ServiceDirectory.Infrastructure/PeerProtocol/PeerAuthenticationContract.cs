using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal static class PeerAuthenticationContract
    {
        internal const string InstanceIdHeaderName = "X-DPAI-Instance-Id";
        internal const string KeyEpochHeaderName = "X-DPAI-Key-Epoch";
        internal const string SessionIdHeaderName = "X-DPAI-Session-Id";
        internal const string TimestampHeaderName = "X-DPAI-Timestamp";
        internal const string NonceHeaderName = "X-DPAI-Nonce";
        internal const string SignatureHeaderName = "X-DPAI-Signature";
        internal const string HandshakePath = "/api/sync/handshake";
        internal const string ExchangePath = "/api/sync/exchange";
        internal const string PkiStatePath = PeerSyncContract.PkiStatePath;
        internal const string ReleasePath = "/api/sync/release";
        internal const string RevokePath = "/api/sync/revoke";

        internal const string TimestampFormat =
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        internal const int AuthenticationKeyLength = 32;
        internal const int SignatureLength = 32;
        internal const int NonceLength = 16;
        internal const int SessionIdLength = 16;
        internal const int FreshnessSeconds = 60;
        internal const int NonSessionReplayRetentionMinutes = 10;
        internal const int SessionLifetimeMinutes = 10;
        internal const int MaximumReplayCacheEntries = 1024;

        internal static string FormatTimestamp(DateTimeOffset timestamp)
        {
            return timestamp.ToUniversalTime().ToString(
                TimestampFormat,
                CultureInfo.InvariantCulture);
        }

        internal static bool TryParseCanonicalTimestamp(
            string value,
            out DateTimeOffset timestamp)
        {
            timestamp = default(DateTimeOffset);
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            DateTimeOffset parsed;
            if (!DateTimeOffset.TryParseExact(
                value,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal
                    | DateTimeStyles.AdjustToUniversal,
                out parsed)
                || parsed.Offset != TimeSpan.Zero
                || !StringComparer.Ordinal.Equals(
                    value,
                    FormatTimestamp(parsed)))
            {
                return false;
            }

            timestamp = parsed;
            return true;
        }

        internal static bool TryParseCanonicalInstanceId(
            string value,
            out Guid instanceId)
        {
            instanceId = Guid.Empty;
            Guid parsed;
            if (string.IsNullOrEmpty(value)
                || !Guid.TryParseExact(value, "D", out parsed)
                || parsed == Guid.Empty
                || !StringComparer.Ordinal.Equals(
                    value,
                    parsed.ToString("D").ToLowerInvariant()))
            {
                return false;
            }

            instanceId = parsed;
            return true;
        }

        internal static bool TryParseCanonicalKeyEpoch(
            string value,
            out ulong keyEpoch)
        {
            keyEpoch = 0;
            ulong parsed;
            if (string.IsNullOrEmpty(value)
                || !ulong.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed)
                || parsed == 0
                || !StringComparer.Ordinal.Equals(
                    value,
                    parsed.ToString(CultureInfo.InvariantCulture)))
            {
                return false;
            }

            keyEpoch = parsed;
            return true;
        }

        internal static bool TryParseCanonicalNonce(
            string value,
            out byte[] nonce)
        {
            return TryDecodeCanonicalBase64(
                value,
                NonceLength,
                out nonce);
        }

        internal static bool TryParseCanonicalSessionId(
            string value,
            out byte[] sessionId)
        {
            return TryDecodeCanonicalBase64(
                value,
                SessionIdLength,
                out sessionId);
        }

        internal static bool TryParseCanonicalSignature(
            string value,
            out byte[] signature)
        {
            return TryDecodeCanonicalBase64(
                value,
                SignatureLength,
                out signature);
        }

        internal static bool IsTimestampFresh(
            DateTimeOffset timestamp,
            DateTimeOffset receivedAt)
        {
            long differenceTicks = receivedAt.ToUniversalTime().Ticks
                - timestamp.ToUniversalTime().Ticks;
            if (differenceTicks < 0)
            {
                differenceTicks = -differenceTicks;
            }

            return differenceTicks
                <= TimeSpan.FromSeconds(FreshnessSeconds).Ticks;
        }

        internal static string NormalizeHttpMethod(string method)
        {
            if (string.IsNullOrEmpty(method)
                || !StringComparer.Ordinal.Equals(method, method.Trim()))
            {
                throw new ArgumentException(
                    "The HTTP method must not be empty or padded.",
                    nameof(method));
            }

            for (int index = 0; index < method.Length; index++)
            {
                char character = method[index];
                bool isUpperAscii = character >= 'A' && character <= 'Z';
                bool isLowerAscii = character >= 'a' && character <= 'z';
                if (!isUpperAscii && !isLowerAscii)
                {
                    throw new ArgumentException(
                        "The HTTP method must contain only ASCII letters.",
                        nameof(method));
                }
            }

            return method.ToUpperInvariant();
        }

        internal static string NormalizeContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)
                || !StringComparer.Ordinal.Equals(
                    contentType,
                    contentType.Trim()))
            {
                throw new ArgumentException(
                    "The content type must not be empty or padded.",
                    nameof(contentType));
            }

            for (int index = 0; index < contentType.Length; index++)
            {
                char character = contentType[index];
                if (character < 0x20 || character > 0x7e)
                {
                    throw new ArgumentException(
                        "The content type must contain printable ASCII only.",
                        nameof(contentType));
                }
            }

            return contentType.ToLowerInvariant();
        }

        internal static void ValidateExactLength(
            byte[] value,
            string parameterName,
            int expectedLength)
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (value.Length != expectedLength)
            {
                throw new ArgumentException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "The value must contain exactly {0} bytes.",
                        expectedLength),
                    parameterName);
            }
        }

        [MethodImpl(
            MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal static bool FixedTimeEquals16(byte[] left, byte[] right)
        {
            ValidateExactLength(left, nameof(left), NonceLength);
            ValidateExactLength(right, nameof(right), NonceLength);

            int difference = 0;
            for (int index = 0; index < NonceLength; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        private static bool TryDecodeCanonicalBase64(
            string value,
            int expectedLength,
            out byte[] decoded)
        {
            decoded = null;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            int expectedEncodedLength = checked(
                ((expectedLength + 2) / 3) * 4);
            if (value.Length != expectedEncodedLength)
            {
                return false;
            }

            byte[] candidate;
            try
            {
                candidate = Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return false;
            }

            if (candidate.Length != expectedLength
                || !StringComparer.Ordinal.Equals(
                    value,
                    Convert.ToBase64String(candidate)))
            {
                Array.Clear(candidate, 0, candidate.Length);
                return false;
            }

            decoded = candidate;
            return true;
        }
    }
}
