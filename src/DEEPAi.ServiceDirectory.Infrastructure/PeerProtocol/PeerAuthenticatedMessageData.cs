using System;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal sealed class PeerRequestAuthenticationData
    {
        private readonly byte[] _sessionId;
        private readonly byte[] _body;
        private readonly byte[] _nonce;

        public PeerRequestAuthenticationData(
            Guid senderInstanceId,
            Guid receiverInstanceId,
            ulong keyEpoch,
            byte[] sessionId,
            string method,
            PeerCanonicalRequestTarget requestTarget,
            string contentType,
            byte[] body,
            DateTimeOffset timestamp,
            byte[] nonce)
        {
            ValidatePeerBinding(senderInstanceId, receiverInstanceId, keyEpoch);
            ValidateOptionalSessionId(sessionId, nameof(sessionId));
            if (requestTarget == null)
            {
                throw new ArgumentNullException(nameof(requestTarget));
            }

            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            PeerAuthenticationContract.ValidateExactLength(
                nonce,
                nameof(nonce),
                PeerAuthenticationContract.NonceLength);

            SenderInstanceId = senderInstanceId;
            ReceiverInstanceId = receiverInstanceId;
            KeyEpoch = keyEpoch;
            _sessionId = sessionId == null
                ? new byte[0]
                : (byte[])sessionId.Clone();
            Method = PeerAuthenticationContract.NormalizeHttpMethod(method);
            RequestTarget = requestTarget;
            ContentType = PeerAuthenticationContract.NormalizeContentType(
                contentType);
            _body = (byte[])body.Clone();
            Timestamp = timestamp.ToUniversalTime();
            _nonce = (byte[])nonce.Clone();
        }

        public Guid SenderInstanceId { get; }

        public Guid ReceiverInstanceId { get; }

        public ulong KeyEpoch { get; }

        public bool HasSession => _sessionId.Length != 0;

        public string Method { get; }

        public PeerCanonicalRequestTarget RequestTarget { get; }

        public string ContentType { get; }

        public DateTimeOffset Timestamp { get; }

        public byte[] CopySessionId()
        {
            return (byte[])_sessionId.Clone();
        }

        public byte[] CopyBody()
        {
            return (byte[])_body.Clone();
        }

        public byte[] CopyNonce()
        {
            return (byte[])_nonce.Clone();
        }

        internal static void ValidatePeerBinding(
            Guid senderInstanceId,
            Guid receiverInstanceId,
            ulong keyEpoch)
        {
            if (senderInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The sender instance ID must not be empty.",
                    nameof(senderInstanceId));
            }

            if (receiverInstanceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The receiver instance ID must not be empty.",
                    nameof(receiverInstanceId));
            }

            if (senderInstanceId == receiverInstanceId)
            {
                throw new ArgumentException(
                    "The sender and receiver instance IDs must be different.",
                    nameof(receiverInstanceId));
            }

            if (keyEpoch == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keyEpoch),
                    keyEpoch,
                    "The key epoch must be positive.");
            }
        }

        internal static void ValidateOptionalSessionId(
            byte[] sessionId,
            string parameterName)
        {
            if (sessionId != null
                && sessionId.Length
                    != PeerAuthenticationContract.SessionIdLength)
            {
                throw new ArgumentException(
                    "A session ID must contain exactly 16 bytes when present.",
                    parameterName);
            }
        }
    }

    internal sealed class PeerResponseAuthenticationData
    {
        private readonly byte[] _sessionId;
        private readonly byte[] _body;
        private readonly byte[] _responseNonce;
        private readonly byte[] _requestNonce;

        public PeerResponseAuthenticationData(
            Guid senderInstanceId,
            Guid receiverInstanceId,
            ulong keyEpoch,
            byte[] sessionId,
            string requestMethod,
            PeerCanonicalRequestTarget requestTarget,
            int httpStatus,
            string contentType,
            byte[] body,
            DateTimeOffset timestamp,
            byte[] responseNonce,
            byte[] requestNonce)
        {
            PeerRequestAuthenticationData.ValidatePeerBinding(
                senderInstanceId,
                receiverInstanceId,
                keyEpoch);
            PeerRequestAuthenticationData.ValidateOptionalSessionId(
                sessionId,
                nameof(sessionId));
            if (requestTarget == null)
            {
                throw new ArgumentNullException(nameof(requestTarget));
            }

            if (httpStatus < 100 || httpStatus > 599)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(httpStatus),
                    httpStatus,
                    "The HTTP status must be between 100 and 599.");
            }

            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            PeerAuthenticationContract.ValidateExactLength(
                responseNonce,
                nameof(responseNonce),
                PeerAuthenticationContract.NonceLength);
            PeerAuthenticationContract.ValidateExactLength(
                requestNonce,
                nameof(requestNonce),
                PeerAuthenticationContract.NonceLength);

            SenderInstanceId = senderInstanceId;
            ReceiverInstanceId = receiverInstanceId;
            KeyEpoch = keyEpoch;
            _sessionId = sessionId == null
                ? new byte[0]
                : (byte[])sessionId.Clone();
            RequestMethod = PeerAuthenticationContract.NormalizeHttpMethod(
                requestMethod);
            RequestTarget = requestTarget;
            HttpStatus = httpStatus;
            ContentType = PeerAuthenticationContract.NormalizeContentType(
                contentType);
            _body = (byte[])body.Clone();
            Timestamp = timestamp.ToUniversalTime();
            _responseNonce = (byte[])responseNonce.Clone();
            _requestNonce = (byte[])requestNonce.Clone();
        }

        public Guid SenderInstanceId { get; }

        public Guid ReceiverInstanceId { get; }

        public ulong KeyEpoch { get; }

        public bool HasSession => _sessionId.Length != 0;

        public string RequestMethod { get; }

        public PeerCanonicalRequestTarget RequestTarget { get; }

        public int HttpStatus { get; }

        public string ContentType { get; }

        public DateTimeOffset Timestamp { get; }

        public byte[] CopySessionId()
        {
            return (byte[])_sessionId.Clone();
        }

        public byte[] CopyBody()
        {
            return (byte[])_body.Clone();
        }

        public byte[] CopyResponseNonce()
        {
            return (byte[])_responseNonce.Clone();
        }

        public byte[] CopyRequestNonce()
        {
            return (byte[])_requestNonce.Clone();
        }
    }
}
