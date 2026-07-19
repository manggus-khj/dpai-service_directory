using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal static class AdminQueryStringParser
    {
        private const int DefaultPageSize = 100;

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        public static bool TryParseServices(
            string rawQuery,
            out AdminServicesQuery query)
        {
            query = null;
            Dictionary<string, string> values;
            if (!TryParse(rawQuery, out values)
                || !ContainsOnly(
                    values,
                    "includeDeleted",
                    "pageSize",
                    "cursor"))
            {
                return false;
            }

            bool includeDeleted = false;
            string includeDeletedText;
            if (values.TryGetValue(
                "includeDeleted",
                out includeDeletedText)
                && !TryParseBoolean(
                    includeDeletedText,
                    out includeDeleted))
            {
                return false;
            }

            int pageSize;
            string cursor;
            if (!TryReadPage(values, out pageSize, out cursor))
            {
                return false;
            }

            query = new AdminServicesQuery(
                includeDeleted,
                pageSize,
                cursor);
            return true;
        }

        public static bool TryParsePending(
            string rawQuery,
            out AdminPendingQuery query)
        {
            query = null;
            Dictionary<string, string> values;
            if (!TryParse(rawQuery, out values)
                || !ContainsOnly(values, "pageSize", "cursor"))
            {
                return false;
            }

            int pageSize;
            string cursor;
            if (!TryReadPage(values, out pageSize, out cursor))
            {
                return false;
            }

            query = new AdminPendingQuery(pageSize, cursor);
            return true;
        }

        public static bool TryParseCertificates(
            string rawQuery,
            out AdminCertificatesQuery query)
        {
            query = null;
            Dictionary<string, string> values;
            if (!TryParse(rawQuery, out values)
                || !ContainsOnly(values, "pageSize", "cursor"))
            {
                return false;
            }

            int pageSize;
            string cursor;
            if (!TryReadPage(values, out pageSize, out cursor))
            {
                return false;
            }

            query = new AdminCertificatesQuery(pageSize, cursor);
            return true;
        }

        public static bool IsEmpty(string rawQuery)
        {
            return string.IsNullOrEmpty(rawQuery)
                || StringComparer.Ordinal.Equals(rawQuery, "?");
        }

        private static bool TryReadPage(
            IDictionary<string, string> values,
            out int pageSize,
            out string cursor)
        {
            pageSize = DefaultPageSize;
            cursor = null;

            string pageSizeText;
            if (values.TryGetValue("pageSize", out pageSizeText)
                && (!int.TryParse(
                        pageSizeText,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out pageSize)
                    || pageSize < 1
                    || pageSize > AdminApiContract.PageSize
                    || !StringComparer.Ordinal.Equals(
                        pageSizeText,
                        pageSize.ToString(CultureInfo.InvariantCulture))))
            {
                return false;
            }

            if (values.TryGetValue("cursor", out cursor))
            {
                if (string.IsNullOrWhiteSpace(cursor)
                    || cursor.Length > 2048
                    || StrictUtf8.GetByteCount(cursor)
                        > AdminApiContract.MaximumBodyBytes)
                {
                    return false;
                }

                try
                {
                    XmlConvert.VerifyXmlChars(cursor);
                }
                catch (XmlException)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryParseBoolean(
            string text,
            out bool value)
        {
            if (StringComparer.Ordinal.Equals(text, "true"))
            {
                value = true;
                return true;
            }

            if (StringComparer.Ordinal.Equals(text, "false"))
            {
                value = false;
                return true;
            }

            value = false;
            return false;
        }

        private static bool TryParse(
            string rawQuery,
            out Dictionary<string, string> values)
        {
            values = new Dictionary<string, string>(
                StringComparer.Ordinal);
            if (IsEmpty(rawQuery))
            {
                return true;
            }

            string query = rawQuery[0] == '?'
                ? rawQuery.Substring(1)
                : rawQuery;
            if (query.Length == 0
                || query.Length > AdminApiContract.MaximumBodyBytes)
            {
                return false;
            }

            string[] pairs = query.Split('&');
            if (pairs.Length > 3)
            {
                return false;
            }

            for (int index = 0; index < pairs.Length; index++)
            {
                string pair = pairs[index];
                int separatorIndex = pair.IndexOf('=');
                if (pair.Length == 0 || separatorIndex <= 0)
                {
                    return false;
                }

                string name;
                string value;
                if (!TryPercentDecode(
                        pair.Substring(0, separatorIndex),
                        out name)
                    || !TryPercentDecode(
                        pair.Substring(separatorIndex + 1),
                        out value)
                    || values.ContainsKey(name))
                {
                    return false;
                }

                values.Add(name, value);
            }

            return true;
        }

        private static bool ContainsOnly(
            IDictionary<string, string> values,
            params string[] allowedNames)
        {
            var allowed = new HashSet<string>(
                allowedNames,
                StringComparer.Ordinal);
            foreach (string name in values.Keys)
            {
                if (!allowed.Contains(name))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryPercentDecode(
            string text,
            out string decoded)
        {
            decoded = null;
            if (text == null)
            {
                return false;
            }

            var bytes = new byte[text.Length];
            int count = 0;
            for (int index = 0; index < text.Length; index++)
            {
                char current = text[index];
                if (current == '%')
                {
                    if (index + 2 >= text.Length)
                    {
                        return false;
                    }

                    int high = HexValue(text[index + 1]);
                    int low = HexValue(text[index + 2]);
                    if (high < 0 || low < 0)
                    {
                        return false;
                    }

                    bytes[count++] = checked((byte)((high << 4) | low));
                    index += 2;
                }
                else
                {
                    if (!IsAllowedRawQueryCharacter(current))
                    {
                        return false;
                    }

                    bytes[count++] = (byte)current;
                }
            }

            try
            {
                decoded = StrictUtf8.GetString(bytes, 0, count);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
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
