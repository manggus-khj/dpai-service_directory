using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal enum PeerSessionHeaderRequirement
    {
        Forbidden = 1,
        Required = 2
    }

    // The transport copies every wire value for each X-DPAI header into this
    // value. Keeping the lists lets the strict codec reject duplicate headers
    // instead of accepting the first value selected by a host implementation.
    internal sealed class PeerAuthenticationHeaderValues
    {
        private readonly IReadOnlyList<string> _instanceIdValues;
        private readonly IReadOnlyList<string> _keyEpochValues;
        private readonly IReadOnlyList<string> _sessionIdValues;
        private readonly IReadOnlyList<string> _timestampValues;
        private readonly IReadOnlyList<string> _nonceValues;
        private readonly IReadOnlyList<string> _signatureValues;

        public PeerAuthenticationHeaderValues(
            IEnumerable<string> instanceIdValues,
            IEnumerable<string> keyEpochValues,
            IEnumerable<string> sessionIdValues,
            IEnumerable<string> timestampValues,
            IEnumerable<string> nonceValues,
            IEnumerable<string> signatureValues)
        {
            _instanceIdValues = Copy(instanceIdValues);
            _keyEpochValues = Copy(keyEpochValues);
            _sessionIdValues = Copy(sessionIdValues);
            _timestampValues = Copy(timestampValues);
            _nonceValues = Copy(nonceValues);
            _signatureValues = Copy(signatureValues);
        }

        internal IReadOnlyList<string> InstanceIdValues =>
            _instanceIdValues;

        internal IReadOnlyList<string> KeyEpochValues => _keyEpochValues;

        internal IReadOnlyList<string> SessionIdValues => _sessionIdValues;

        internal IReadOnlyList<string> TimestampValues => _timestampValues;

        internal IReadOnlyList<string> NonceValues => _nonceValues;

        internal IReadOnlyList<string> SignatureValues => _signatureValues;

        private static IReadOnlyList<string> Copy(
            IEnumerable<string> values)
        {
            var copy = new List<string>();
            if (values != null)
            {
                foreach (string value in values)
                {
                    copy.Add(value);
                }
            }

            return new ReadOnlyCollection<string>(copy);
        }
    }

    internal sealed class PeerParsedAuthenticationHeaders : IDisposable
    {
        private readonly byte[] _sessionId;
        private readonly byte[] _nonce;
        private readonly byte[] _signature;
        private bool _disposed;

        internal PeerParsedAuthenticationHeaders(
            Guid senderInstanceId,
            ulong keyEpoch,
            byte[] sessionId,
            DateTimeOffset timestamp,
            byte[] nonce,
            byte[] signature)
        {
            PeerRequestAuthenticationData.ValidateOptionalSessionId(
                sessionId,
                nameof(sessionId));
            PeerAuthenticationContract.ValidateExactLength(
                nonce,
                nameof(nonce),
                PeerAuthenticationContract.NonceLength);
            PeerAuthenticationContract.ValidateExactLength(
                signature,
                nameof(signature),
                PeerAuthenticationContract.SignatureLength);

            SenderInstanceId = senderInstanceId;
            KeyEpoch = keyEpoch;
            _sessionId = sessionId == null
                ? null
                : (byte[])sessionId.Clone();
            Timestamp = timestamp.ToUniversalTime();
            _nonce = (byte[])nonce.Clone();
            _signature = (byte[])signature.Clone();
        }

        public Guid SenderInstanceId { get; }

        public ulong KeyEpoch { get; }

        public bool HasSession => _sessionId != null;

        public DateTimeOffset Timestamp { get; }

        internal byte[] CopySessionId()
        {
            ThrowIfDisposed();
            return _sessionId == null
                ? null
                : (byte[])_sessionId.Clone();
        }

        internal byte[] CopyNonce()
        {
            ThrowIfDisposed();
            return (byte[])_nonce.Clone();
        }

        internal byte[] CopySignature()
        {
            ThrowIfDisposed();
            return (byte[])_signature.Clone();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Clear(_sessionId);
            Clear(_nonce);
            Clear(_signature);
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(PeerParsedAuthenticationHeaders));
            }
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }
    }

    internal static class PeerAuthenticationHeaderCodec
    {
        internal static bool TryParseRequest(
            PeerAuthenticationHeaderValues values,
            PeerSessionHeaderRequirement sessionRequirement,
            out PeerParsedAuthenticationHeaders parsed)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            if (!Enum.IsDefined(
                typeof(PeerSessionHeaderRequirement),
                sessionRequirement))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sessionRequirement));
            }

            parsed = null;
            byte[] sessionId = null;
            byte[] nonce = null;
            byte[] signature = null;
            try
            {
                string instanceIdValue;
                string keyEpochValue;
                string timestampValue;
                string nonceValue;
                string signatureValue;
                if (!TryGetExactlyOne(
                        values.InstanceIdValues,
                        out instanceIdValue)
                    || !TryGetExactlyOne(
                        values.KeyEpochValues,
                        out keyEpochValue)
                    || !TryGetExactlyOne(
                        values.TimestampValues,
                        out timestampValue)
                    || !TryGetExactlyOne(
                        values.NonceValues,
                        out nonceValue)
                    || !TryGetExactlyOne(
                        values.SignatureValues,
                        out signatureValue))
                {
                    return false;
                }

                if (sessionRequirement
                        == PeerSessionHeaderRequirement.Forbidden)
                {
                    if (values.SessionIdValues.Count != 0)
                    {
                        return false;
                    }
                }
                else
                {
                    string sessionIdValue;
                    if (!TryGetExactlyOne(
                            values.SessionIdValues,
                            out sessionIdValue)
                        || !PeerAuthenticationContract
                            .TryParseCanonicalSessionId(
                                sessionIdValue,
                                out sessionId))
                    {
                        return false;
                    }
                }

                Guid senderInstanceId;
                ulong keyEpoch;
                DateTimeOffset timestamp;
                if (!PeerAuthenticationContract.TryParseCanonicalInstanceId(
                        instanceIdValue,
                        out senderInstanceId)
                    || !PeerAuthenticationContract.TryParseCanonicalKeyEpoch(
                        keyEpochValue,
                        out keyEpoch)
                    || !PeerAuthenticationContract.TryParseCanonicalTimestamp(
                        timestampValue,
                        out timestamp)
                    || !PeerAuthenticationContract.TryParseCanonicalNonce(
                        nonceValue,
                        out nonce)
                    || !PeerAuthenticationContract.TryParseCanonicalSignature(
                        signatureValue,
                        out signature))
                {
                    return false;
                }

                parsed = new PeerParsedAuthenticationHeaders(
                    senderInstanceId,
                    keyEpoch,
                    sessionId,
                    timestamp,
                    nonce,
                    signature);
                return true;
            }
            finally
            {
                Clear(sessionId);
                Clear(nonce);
                Clear(signature);
            }
        }

        private static bool TryGetExactlyOne(
            IReadOnlyList<string> values,
            out string value)
        {
            value = null;
            if (values == null || values.Count != 1 || values[0] == null)
            {
                return false;
            }

            value = values[0];
            return true;
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }
    }
}
