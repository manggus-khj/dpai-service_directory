using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.Infrastructure.Logging;
using DEEPAi.ServiceDirectory.Infrastructure.WatchdogProtocol;

namespace DEEPAi.ServiceDirectory.Watchdog
{
    internal sealed class WatchdogPipeServer
    {
        private const int PipeBufferBytes = 4096;
        private const string InvalidRequestReason =
            "잘못된 요청입니다.";

        private readonly WatchdogPipeClientAuthorization _authorization;
        private readonly SecurityAuditEventLogger _securityAuditLogger;
        private readonly Func<
            WatchdogPipeCommand,
            WatchdogPipeResponseDeadline,
            byte[]> _commandHandler;
        private readonly PipeSecurity _pipeSecurity;

        internal WatchdogPipeServer(
            WatchdogPipeClientAuthorization authorization,
            SecurityAuditEventLogger securityAuditLogger,
            Func<
                WatchdogPipeCommand,
                WatchdogPipeResponseDeadline,
                byte[]> commandHandler)
        {
            _authorization = authorization
                ?? throw new ArgumentNullException(nameof(authorization));
            _securityAuditLogger = securityAuditLogger
                ?? throw new ArgumentNullException(
                    nameof(securityAuditLogger));
            _commandHandler = commandHandler
                ?? throw new ArgumentNullException(nameof(commandHandler));
            _pipeSecurity = _authorization.CreatePipeSecurity();
        }

        internal async Task RunAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (NamedPipeServerStream pipe = CreateServer())
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(cancellationToken)
                            .ConfigureAwait(false);
                        await ServeConnectedClientAsync(
                                pipe,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (IOException)
                    {
                        // A client may disconnect at any point. The bounded
                        // single-connection instance is discarded before the
                        // next client is accepted.
                    }
                }
            }
        }

        private NamedPipeServerStream CreateServer()
        {
            return new NamedPipeServerStream(
                WatchdogPipeCodec.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                PipeBufferBytes,
                PipeBufferBytes,
                _pipeSecurity);
        }

        private async Task ServeConnectedClientAsync(
            NamedPipeServerStream pipe,
            CancellationToken serviceCancellationToken)
        {
            using (var requestTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    serviceCancellationToken))
            {
                requestTimeout.CancelAfter(
                    TimeSpan.FromSeconds(
                        WatchdogPipeCodec.TimeoutSeconds));

                SecurityIdentifier actorSid;
                SecurityAuditReason rejectionReason;
                if (!_authorization.TryAuthorize(
                        pipe,
                        out actorSid,
                        out rejectionReason))
                {
                    _securityAuditLogger.WriteFailure(
                        SecurityAuditEventId.PipeAuthorizationRejected,
                        SecurityAuditBoundary.NamedPipe,
                        SecurityAuditOperation.PipeConnect,
                        rejectionReason,
                        Guid.NewGuid(),
                        actorSid,
                        null);
                    return;
                }

                byte[] requestBytes;
                try
                {
                    requestBytes = await ReadSingleRequestAsync(
                            pipe,
                            requestTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!serviceCancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var responseDeadline = new WatchdogPipeResponseDeadline(
                    TimeSpan.FromSeconds(
                        WatchdogPipeCodec.TimeoutSeconds));
                WatchdogRequestParseResult request =
                    WatchdogPipeCodec.ParseRequest(requestBytes);
                byte[] response = request.IsSuccess
                    ? _commandHandler(
                        request.Command.Value,
                        responseDeadline)
                    : WatchdogPipeCodec.EncodeErrorResponse(
                        InvalidRequestReason);
                if (response == null
                    || response.Length == 0
                    || response.Length > WatchdogPipeCodec.MaximumLineBytes)
                {
                    throw new InvalidOperationException(
                        "The watchdog command handler returned an invalid response.");
                }

                using (var responseTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        serviceCancellationToken))
                {
                    TimeSpan remaining = responseDeadline.Remaining;
                    if (remaining <= TimeSpan.Zero)
                    {
                        return;
                    }

                    responseTimeout.CancelAfter(remaining);
                    try
                    {
                        await pipe.WriteAsync(
                                response,
                                0,
                                response.Length,
                                responseTimeout.Token)
                            .ConfigureAwait(false);
                        await pipe.FlushAsync(responseTimeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (!serviceCancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }

        internal static async Task<byte[]> ReadSingleRequestAsync(
            NamedPipeServerStream pipe,
            CancellationToken cancellationToken)
        {
            if (pipe == null)
            {
                throw new ArgumentNullException(nameof(pipe));
            }

            using (var output = new MemoryStream())
            {
                var oneByte = new byte[1];
                bool lineTerminatorSeen = false;
                while (output.Length <= WatchdogPipeCodec.MaximumLineBytes)
                {
                    int read = await pipe.ReadAsync(
                            oneByte,
                            0,
                            oneByte.Length,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        return output.ToArray();
                    }

                    output.WriteByte(oneByte[0]);
                    if (oneByte[0] == (byte)'\n')
                    {
                        lineTerminatorSeen = true;
                    }

                    // The wire contract is a UTF-8 line, not a Named Pipe
                    // message. A client may split one line across writes, so
                    // a message boundary before LF is not a request boundary.
                    // Keep bytes after LF in the same message so the strict
                    // codec rejects a second line or trailing data.
                    if (lineTerminatorSeen && pipe.IsMessageComplete)
                    {
                        return output.ToArray();
                    }
                }

                return output.ToArray();
            }
        }
    }
}
