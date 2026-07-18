using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.Infrastructure.WatchdogProtocol;

namespace DEEPAi.ServiceDirectory.Tray.Clients
{
    public sealed class WatchdogCallResult
    {
        private WatchdogCallResult(
            bool isSuccess,
            WatchdogStatusSnapshot statusSnapshot,
            string errorMessage)
        {
            IsSuccess = isSuccess;
            StatusSnapshot = statusSnapshot;
            ErrorMessage = errorMessage;
        }

        public bool IsSuccess { get; }

        public WatchdogStatusSnapshot StatusSnapshot { get; }

        public WatchdogServiceStatus? ServiceStatus => StatusSnapshot == null
            ? (WatchdogServiceStatus?)null
            : StatusSnapshot.ServiceStatus;

        public string ErrorMessage { get; }

        internal static WatchdogCallResult Success(
            WatchdogStatusSnapshot statusSnapshot)
        {
            return new WatchdogCallResult(true, statusSnapshot, null);
        }

        internal static WatchdogCallResult Failure(string errorMessage)
        {
            return new WatchdogCallResult(false, null, errorMessage);
        }
    }

    public sealed class WatchdogPipeClient
    {
        public async Task<WatchdogCallResult> SendAsync(
            WatchdogPipeCommand command,
            CancellationToken cancellationToken)
        {
            bool requestTransmitted = false;
            try
            {
                using (var pipe = new NamedPipeClientStream(
                    ".",
                    WatchdogPipeCodec.PipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Identification))
                {
                    using (var connectTimeout =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken))
                    {
                        connectTimeout.CancelAfter(
                            TimeSpan.FromSeconds(
                                WatchdogPipeCodec.TimeoutSeconds));
                        await pipe.ConnectAsync(
                            WatchdogPipeCodec.TimeoutSeconds * 1000,
                            connectTimeout.Token);
                    }

                    byte[] request = WatchdogPipeCodec.EncodeRequest(command);
                    await pipe.WriteAsync(
                        request,
                        0,
                        request.Length,
                        cancellationToken);
                    await pipe.FlushAsync(cancellationToken);
                    requestTransmitted = true;

                    byte[] responseBytes;
                    using (var responseTimeout =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken))
                    {
                        responseTimeout.CancelAfter(
                            TimeSpan.FromSeconds(
                                WatchdogPipeCodec.TimeoutSeconds));
                        responseBytes = await ReadSingleLineAsync(
                            pipe,
                            responseTimeout.Token);
                    }

                    WatchdogResponseParseResult response =
                        WatchdogPipeCodec.ParseResponse(responseBytes, command);
                    if (!response.IsValid)
                    {
                        return WatchdogCallResult.Failure(
                            "와치독 응답 형식이 올바르지 않습니다.");
                    }

                    if (response.Outcome == WatchdogPipeResponseOutcome.Error)
                    {
                        return WatchdogCallResult.Failure(response.ErrorReason);
                    }

                    return WatchdogCallResult.Success(response.StatusSnapshot);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return WatchdogCallResult.Failure(
                    requestTransmitted
                        ? "와치독 응답 시간이 초과되었습니다."
                        : "와치독 연결 시간이 초과되었습니다.");
            }
            catch (TimeoutException)
            {
                return WatchdogCallResult.Failure(
                    requestTransmitted
                        ? "와치독 응답 시간이 초과되었습니다."
                        : "와치독 연결 시간이 초과되었습니다.");
            }
            catch (UnauthorizedAccessException)
            {
                return WatchdogCallResult.Failure(
                    "와치독 파이프 접근 권한이 없습니다.");
            }
            catch (IOException)
            {
                return WatchdogCallResult.Failure(
                    "와치독 서비스에 연결할 수 없습니다.");
            }
        }

        private static async Task<byte[]> ReadSingleLineAsync(
            Stream input,
            CancellationToken cancellationToken)
        {
            using (var output = new MemoryStream())
            {
                var singleByte = new byte[1];
                while (output.Length < WatchdogPipeCodec.MaximumLineBytes)
                {
                    int read = await input.ReadAsync(
                        singleByte,
                        0,
                        1,
                        cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    output.WriteByte(singleByte[0]);
                    if (singleByte[0] == (byte)'\n')
                    {
                        return output.ToArray();
                    }
                }

                if (output.Length >= WatchdogPipeCodec.MaximumLineBytes)
                {
                    throw new IOException("Watchdog response exceeded the line limit.");
                }

                return output.ToArray();
            }
        }
    }
}
