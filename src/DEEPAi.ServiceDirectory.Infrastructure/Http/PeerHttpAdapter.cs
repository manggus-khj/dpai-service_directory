using System;
using System.IO;
using System.Net;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;
using DEEPAi.ServiceDirectory.Infrastructure.Protocol;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    // Applies the unauthenticated transport boundary before handing the exact
    // raw body and headers to the Peer wire handler. Remote peer identity/IP
    // binding is deliberately checked by the handler after HMAC validation so
    // an authenticated mismatch can receive the contract's signed 403.
    public sealed class PeerHttpAdapter
    {
        private const string PairingHelloPath =
            "/api/sync/pairing/hello";
        private const string PairingKeyConfirmPath =
            "/api/sync/pairing/key-confirm";
        private const string PairingDecisionPath =
            "/api/sync/pairing/decision";
        private const string PairingCommitPath =
            "/api/sync/pairing/commit";
        private const string HandshakePath = "/api/sync/handshake";
        private const string ExchangePath = "/api/sync/exchange";
        private const string PkiStatePath = PeerSyncContract.PkiStatePath;
        private const string ReleasePath = "/api/sync/release";
        private const string RevokePath = "/api/sync/revoke";

        private readonly IPeerHttpRequestHandler _handler;
        private readonly ServiceDirectoryListenerAddress _configuredAddress;
        private readonly SecurityAuditEventLogger _securityAuditLogger;
        private readonly BoundedRequestBodyReader _bodyReader;
        private readonly Func<Guid> _requestIdProvider;
        private readonly Func<DateTimeOffset> _utcNowProvider;

        public PeerHttpAdapter(
            IPeerHttpRequestHandler handler,
            ServiceDirectoryListenerAddress configuredAddress,
            SecurityAuditEventLogger securityAuditLogger)
            : this(
                handler,
                configuredAddress,
                securityAuditLogger,
                new BoundedRequestBodyReader(),
                Guid.NewGuid,
                () => DateTimeOffset.UtcNow)
        {
        }

        internal PeerHttpAdapter(
            IPeerHttpRequestHandler handler,
            ServiceDirectoryListenerAddress configuredAddress,
            SecurityAuditEventLogger securityAuditLogger,
            BoundedRequestBodyReader bodyReader,
            Func<Guid> requestIdProvider,
            Func<DateTimeOffset> utcNowProvider)
        {
            _handler = handler ?? throw new ArgumentNullException(
                nameof(handler));
            _configuredAddress = configuredAddress
                ?? throw new ArgumentNullException(
                    nameof(configuredAddress));
            _securityAuditLogger = securityAuditLogger
                ?? throw new ArgumentNullException(
                    nameof(securityAuditLogger));
            _bodyReader = bodyReader ?? throw new ArgumentNullException(
                nameof(bodyReader));
            _requestIdProvider = requestIdProvider
                ?? throw new ArgumentNullException(
                    nameof(requestIdProvider));
            _utcNowProvider = utcNowProvider
                ?? throw new ArgumentNullException(
                    nameof(utcNowProvider));
        }

        public PeerHttpResponseData Process(PeerHttpRequestData request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            try
            {
                return ProcessCore(request);
            }
            catch (SecurityAuditSourceUnavailableException)
            {
                throw;
            }
            catch (SecurityAuditWriteException)
            {
                throw;
            }
            catch (Exception)
            {
                return PeerHttpResponseData.Bodyless(500);
            }
        }

        private PeerHttpResponseData ProcessCore(PeerHttpRequestData request)
        {
            SecurityAuditOperation operation;
            bool exchange;
            if (!TryResolveOperation(
                    request.Method,
                    request.AbsolutePath,
                    request.RawQuery,
                    out operation,
                    out exchange))
            {
                return PeerHttpResponseData.Bodyless(404);
            }

            Guid requestId = _requestIdProvider();
            if (requestId == Guid.Empty)
            {
                return PeerHttpResponseData.Bodyless(500);
            }

            IPEndPoint localEndpoint = request.LocalEndpoint;
            if (localEndpoint == null || localEndpoint.Address == null)
            {
                WriteNetworkBoundaryFailure(
                    requestId,
                    operation,
                    SecurityAuditReason.LocalEndpointUnavailable,
                    request.RemoteEndpoint);
                return PeerHttpResponseData.Bodyless(403);
            }

            if (!HttpRequestEndpointGuard
                .IsConfiguredLocalEndpointAllowed(
                    localEndpoint,
                    _configuredAddress))
            {
                WriteNetworkBoundaryFailure(
                    requestId,
                    operation,
                    SecurityAuditReason.LocalEndpointMismatch,
                    request.RemoteEndpoint);
                return PeerHttpResponseData.Bodyless(403);
            }

            if (!string.IsNullOrEmpty(
                    request.ContentEncodingHeaderValue)
                || !IsSupportedXmlContentType(request.ContentType))
            {
                return PeerHttpResponseData.Bodyless(415);
            }

            byte[] body;
            PeerHttpResponseData bodyFailure;
            if (!TryReadBody(
                    request,
                    exchange,
                    out body,
                    out bodyFailure))
            {
                return bodyFailure;
            }

            var handlerRequest = new PeerHttpHandlerRequest(
                request,
                body,
                requestId,
                _utcNowProvider());
            Array.Clear(body, 0, body.Length);
            return _handler.Process(handlerRequest)
                ?? PeerHttpResponseData.Bodyless(500);
        }

        private bool TryReadBody(
            PeerHttpRequestData request,
            bool exchange,
            out byte[] body,
            out PeerHttpResponseData failure)
        {
            body = null;
            failure = null;
            int maximumBytes = exchange
                ? PeerSyncContract.MaximumExchangeBodyBytes
                : PeerSyncContract.MaximumControlBodyBytes;
            if (request.DeclaredContentLength > maximumBytes)
            {
                failure = PeerHttpResponseData.Bodyless(413);
                return false;
            }

            if (request.BodyStream == null
                || !request.BodyStream.CanRead)
            {
                failure = PeerHttpResponseData.Bodyless(400);
                return false;
            }

            BoundedBodyReadResult read = exchange
                ? _bodyReader.ReadSyncExchange(
                    request.BodyStream,
                    request.DeclaredContentLength)
                : _bodyReader.ReadStandard(
                    request.BodyStream,
                    request.DeclaredContentLength);
            if (!read.IsSuccess)
            {
                switch (read.FailureCode)
                {
                    case BoundedBodyReadFailureCode
                        .DeclaredLengthTooLarge:
                    case BoundedBodyReadFailureCode.ActualLengthTooLarge:
                        failure = PeerHttpResponseData.Bodyless(413);
                        break;
                    case BoundedBodyReadFailureCode.DeclaredLengthMismatch:
                        failure = PeerHttpResponseData.Bodyless(400);
                        break;
                    case BoundedBodyReadFailureCode.IoFailure:
                    case BoundedBodyReadFailureCode.None:
                    default:
                        failure = PeerHttpResponseData.Bodyless(500);
                        break;
                }

                return false;
            }

            body = CopyBody(read.Body);
            return true;
        }

        private static byte[] CopyBody(BoundedRequestBody body)
        {
            var copy = new byte[body.Length];
            using (Stream source = body.OpenRead())
            {
                int offset = 0;
                while (offset < copy.Length)
                {
                    int read = source.Read(
                        copy,
                        offset,
                        copy.Length - offset);
                    if (read == 0)
                    {
                        throw new InvalidOperationException(
                            "The bounded Peer body ended unexpectedly.");
                    }

                    offset += read;
                }
            }

            return copy;
        }

        private void WriteNetworkBoundaryFailure(
            Guid requestId,
            SecurityAuditOperation operation,
            SecurityAuditReason reason,
            IPEndPoint remoteEndpoint)
        {
            IPAddress remoteAddress = remoteEndpoint == null
                ? null
                : remoteEndpoint.Address;
            _securityAuditLogger.WriteFailure(
                SecurityAuditEventId.NetworkBoundaryRejected,
                SecurityAuditBoundary.Peer,
                operation,
                reason,
                requestId,
                null,
                remoteAddress);
        }

        private static bool TryResolveOperation(
            string method,
            string path,
            string rawQuery,
            out SecurityAuditOperation operation,
            out bool exchange)
        {
            operation = SecurityAuditOperation.PeerExchange;
            exchange = false;
            if (!StringComparer.Ordinal.Equals(method, "POST")
                || !string.IsNullOrEmpty(rawQuery))
            {
                return false;
            }

            switch (path)
            {
                case PairingHelloPath:
                    operation = SecurityAuditOperation.PeerPairingHello;
                    return true;
                case PairingKeyConfirmPath:
                    operation =
                        SecurityAuditOperation.PeerPairingKeyConfirm;
                    return true;
                case PairingDecisionPath:
                    operation =
                        SecurityAuditOperation.PeerPairingDecision;
                    return true;
                case PairingCommitPath:
                    operation = SecurityAuditOperation.PeerPairingCommit;
                    return true;
                case HandshakePath:
                    operation = SecurityAuditOperation.PeerHandshake;
                    return true;
                case ExchangePath:
                    operation = SecurityAuditOperation.PeerExchange;
                    exchange = true;
                    return true;
                case PkiStatePath:
                    operation = SecurityAuditOperation.PeerExchange;
                    exchange = true;
                    return true;
                case ReleasePath:
                    operation = SecurityAuditOperation.PeerRelease;
                    return true;
                case RevokePath:
                    operation = SecurityAuditOperation.PeerRevoke;
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsSupportedXmlContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType))
            {
                return false;
            }

            string[] segments = contentType.Split(';');
            if (segments.Length != 2
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    TrimOptionalWhitespace(segments[0]),
                    "application/xml"))
            {
                return false;
            }

            string parameter = TrimOptionalWhitespace(segments[1]);
            int equalsIndex = parameter.IndexOf('=');
            if (equalsIndex <= 0
                || equalsIndex != parameter.LastIndexOf('='))
            {
                return false;
            }

            return StringComparer.OrdinalIgnoreCase.Equals(
                    TrimOptionalWhitespace(
                        parameter.Substring(0, equalsIndex)),
                    "charset")
                && StringComparer.OrdinalIgnoreCase.Equals(
                    TrimOptionalWhitespace(
                        parameter.Substring(equalsIndex + 1)),
                    "utf-8");
        }

        private static string TrimOptionalWhitespace(string value)
        {
            return value == null ? null : value.Trim(' ', '\t');
        }
    }
}
