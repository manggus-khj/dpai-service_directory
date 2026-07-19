using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal static class ExternalQueryStringParser
    {
        internal const int MaximumRawQueryBytes =
            ExternalApiContract.MaximumRawQueryBytes;
        internal const int MaximumFieldCount =
            ExternalApiContract.MaximumQueryFieldCount;

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        public static bool TryParse(
            string rawQuery,
            out IReadOnlyList<ExternalApiQueryParameter> parameters)
        {
            parameters = null;
            if (string.IsNullOrEmpty(rawQuery) || rawQuery == "?")
            {
                parameters = new ReadOnlyCollection<ExternalApiQueryParameter>(
                    new List<ExternalApiQueryParameter>());
                return true;
            }

            // HttpListener supplies the raw request-target query as ASCII.
            // The optional leading '?' is included in this wire-size limit.
            if (rawQuery.Length > MaximumRawQueryBytes)
            {
                return false;
            }

            string query = rawQuery[0] == '?'
                ? rawQuery.Substring(1)
                : rawQuery;
            if (query.Length == 0)
            {
                parameters = new ReadOnlyCollection<ExternalApiQueryParameter>(
                    new List<ExternalApiQueryParameter>());
                return true;
            }

            int fieldCount = 1;
            for (int index = 0; index < query.Length; index++)
            {
                char current = query[index];
                if (current > 0x7f)
                {
                    return false;
                }

                if (current == '%')
                {
                    if (index + 2 >= query.Length
                        || HexValue(query[index + 1]) < 0
                        || HexValue(query[index + 2]) < 0)
                    {
                        return false;
                    }

                    index += 2;
                    continue;
                }

                if (!IsAllowedRawQueryCharacter(current))
                {
                    return false;
                }

                if (current == '&')
                {
                    fieldCount++;
                    if (fieldCount > MaximumFieldCount)
                    {
                        return false;
                    }
                }
            }

            var parsed = new List<ExternalApiQueryParameter>();
            string[] fields = query.Split('&');
            foreach (string field in fields)
            {
                if (field.Length == 0)
                {
                    return false;
                }

                int equalsIndex = field.IndexOf('=');
                string encodedName = equalsIndex < 0
                    ? field
                    : field.Substring(0, equalsIndex);
                string encodedValue = equalsIndex < 0
                    ? null
                    : field.Substring(equalsIndex + 1);

                string name;
                string value = null;
                if (!TryPercentDecode(encodedName, out name)
                    || (encodedValue != null
                        && !TryPercentDecode(encodedValue, out value)))
                {
                    return false;
                }

                if (encodedValue == null)
                {
                    value = null;
                }

                // Each field remains a separate entry. The core contract then
                // rejects duplicates instead of accepting a collapsed value.
                parsed.Add(new ExternalApiQueryParameter(name, value));
            }

            parameters = new ReadOnlyCollection<ExternalApiQueryParameter>(
                parsed);
            return true;
        }

        private static bool IsAllowedRawQueryCharacter(char value)
        {
            bool alphaNumeric = (value >= 'A' && value <= 'Z')
                || (value >= 'a' && value <= 'z')
                || (value >= '0' && value <= '9');
            if (alphaNumeric)
            {
                return true;
            }

            switch (value)
            {
                // RFC 3986 query = *( pchar / "/" / "?" ). Percent
                // triplets are validated separately before this method.
                case '-':
                case '.':
                case '_':
                case '~':
                case '!':
                case '$':
                case '&':
                case '\'':
                case '(':
                case ')':
                case '*':
                case '+':
                case ',':
                case ';':
                case '=':
                case ':':
                case '@':
                case '/':
                case '?':
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryPercentDecode(
            string encoded,
            out string decoded)
        {
            decoded = null;
            if (encoded == null)
            {
                return false;
            }

            try
            {
                using (var bytes = new MemoryStream(encoded.Length))
                {
                    for (int index = 0; index < encoded.Length; index++)
                    {
                        char current = encoded[index];
                        if (current == '%')
                        {
                            if (index + 2 >= encoded.Length)
                            {
                                return false;
                            }

                            int high = HexValue(encoded[index + 1]);
                            int low = HexValue(encoded[index + 2]);
                            if (high < 0 || low < 0)
                            {
                                return false;
                            }

                            bytes.WriteByte((byte)((high << 4) | low));
                            index += 2;
                        }
                        else
                        {
                            // RawQuery is an ASCII wire value. '+' remains a
                            // literal plus; only percent triplets are decoded.
                            if (current > 0x7f)
                            {
                                return false;
                            }

                            bytes.WriteByte((byte)current);
                        }
                    }

                    decoded = StrictUtf8.GetString(bytes.ToArray());
                    return true;
                }
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static int HexValue(char value)
        {
            if (value >= '0' && value <= '9')
            {
                return value - '0';
            }

            if (value >= 'A' && value <= 'F')
            {
                return value - 'A' + 10;
            }

            if (value >= 'a' && value <= 'f')
            {
                return value - 'a' + 10;
            }

            return -1;
        }
    }
}
