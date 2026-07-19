using System;
using System.Collections.Generic;
using System.Text;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal sealed class PeerQueryParameter
    {
        public PeerQueryParameter(string name, string value)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            Name = name;
            Value = value;
        }

        public string Name { get; }

        public string Value { get; }
    }

    internal sealed class PeerCanonicalRequestTarget
    {
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);
        private const string HexDigits = "0123456789ABCDEF";

        private PeerCanonicalRequestTarget(string value)
        {
            Value = value;
        }

        public string Value { get; }

        // Both the path and query names/values supplied here are decoded text.
        // This method produces the single RFC 3986 percent-encoded value that is
        // included in a Peer request or response MAC.
        public static PeerCanonicalRequestTarget Create(
            string decodedPath,
            IEnumerable<PeerQueryParameter> queryParameters)
        {
            string canonicalPath = CanonicalizePath(decodedPath);
            var encodedParameters = new List<EncodedQueryParameter>();
            if (queryParameters != null)
            {
                foreach (PeerQueryParameter parameter in queryParameters)
                {
                    if (parameter == null)
                    {
                        throw new ArgumentException(
                            "Query parameters must not contain null entries.",
                            nameof(queryParameters));
                    }

                    encodedParameters.Add(
                        new EncodedQueryParameter(
                            PercentEncode(parameter.Name, false),
                            PercentEncode(parameter.Value, false)));
                }
            }

            encodedParameters.Sort(EncodedQueryParameterComparer.Instance);
            if (encodedParameters.Count == 0)
            {
                return new PeerCanonicalRequestTarget(canonicalPath);
            }

            var builder = new StringBuilder(canonicalPath);
            builder.Append('?');
            for (int index = 0; index < encodedParameters.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append('&');
                }

                builder.Append(encodedParameters[index].Name);
                builder.Append('=');
                builder.Append(encodedParameters[index].Value);
            }

            return new PeerCanonicalRequestTarget(builder.ToString());
        }

        private static string CanonicalizePath(string decodedPath)
        {
            if (string.IsNullOrEmpty(decodedPath)
                || decodedPath[0] != '/'
                || decodedPath.IndexOf('?') >= 0
                || decodedPath.IndexOf('#') >= 0
                || decodedPath.IndexOf('\\') >= 0)
            {
                throw new ArgumentException(
                    "The request path must be an absolute decoded path without a query or fragment.",
                    nameof(decodedPath));
            }

            string[] segments = decodedPath.Split('/');
            for (int index = 0; index < segments.Length; index++)
            {
                if (StringComparer.Ordinal.Equals(segments[index], ".")
                    || StringComparer.Ordinal.Equals(segments[index], ".."))
                {
                    throw new ArgumentException(
                        "The request path must not contain dot segments.",
                        nameof(decodedPath));
                }
            }

            return PercentEncode(decodedPath, true);
        }

        private static string PercentEncode(
            string value,
            bool preservePathSeparator)
        {
            byte[] encoded;
            try
            {
                encoded = StrictUtf8.GetBytes(value);
            }
            catch (EncoderFallbackException exception)
            {
                throw new ArgumentException(
                    "The value contains an invalid Unicode sequence.",
                    nameof(value),
                    exception);
            }

            var builder = new StringBuilder(encoded.Length);
            for (int index = 0; index < encoded.Length; index++)
            {
                byte current = encoded[index];
                if (IsUnreserved(current)
                    || (preservePathSeparator && current == (byte)'/'))
                {
                    builder.Append((char)current);
                    continue;
                }

                builder.Append('%');
                builder.Append(HexDigits[current >> 4]);
                builder.Append(HexDigits[current & 0x0f]);
            }

            Array.Clear(encoded, 0, encoded.Length);
            return builder.ToString();
        }

        private static bool IsUnreserved(byte value)
        {
            return (value >= (byte)'A' && value <= (byte)'Z')
                || (value >= (byte)'a' && value <= (byte)'z')
                || (value >= (byte)'0' && value <= (byte)'9')
                || value == (byte)'-'
                || value == (byte)'.'
                || value == (byte)'_'
                || value == (byte)'~';
        }

        private sealed class EncodedQueryParameter
        {
            public EncodedQueryParameter(string name, string value)
            {
                Name = name;
                Value = value;
            }

            public string Name { get; }

            public string Value { get; }
        }

        private sealed class EncodedQueryParameterComparer
            : IComparer<EncodedQueryParameter>
        {
            public static readonly EncodedQueryParameterComparer Instance =
                new EncodedQueryParameterComparer();

            public int Compare(
                EncodedQueryParameter left,
                EncodedQueryParameter right)
            {
                if (ReferenceEquals(left, right))
                {
                    return 0;
                }

                if (left == null)
                {
                    return -1;
                }

                if (right == null)
                {
                    return 1;
                }

                int nameComparison = StringComparer.Ordinal.Compare(
                    left.Name,
                    right.Name);
                return nameComparison != 0
                    ? nameComparison
                    : StringComparer.Ordinal.Compare(left.Value, right.Value);
            }
        }
    }
}
