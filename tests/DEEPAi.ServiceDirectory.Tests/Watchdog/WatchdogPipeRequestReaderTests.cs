using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.Infrastructure.WatchdogProtocol;
using DEEPAi.ServiceDirectory.Watchdog;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Watchdog
{
    [TestClass]
    public sealed class WatchdogPipeRequestReaderTests
    {
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        [TestMethod]
        public async Task OneLineMaySpanMultiplePipeMessages()
        {
            byte[] result = await ReadAsync("STA", "TUS\r\n");

            CollectionAssert.AreEqual(
                StrictUtf8.GetBytes("STATUS\r\n"),
                result);
            Assert.IsTrue(WatchdogPipeCodec.ParseRequest(result).IsSuccess);
        }

        [TestMethod]
        public async Task BytesAfterLfInTheSameMessageReachStrictCodec()
        {
            byte[] result = await ReadAsync("STATUS\nSTOP\n");

            WatchdogRequestParseResult parsed =
                WatchdogPipeCodec.ParseRequest(result);
            Assert.IsFalse(parsed.IsSuccess);
            Assert.AreEqual(
                WatchdogPipeParseFailureCode.InvalidLineFraming,
                parsed.FailureCode);
        }

        private static async Task<byte[]> ReadAsync(params string[] messages)
        {
            string pipeName = "SvcDirWatchdog-Test-"
                + Guid.NewGuid().ToString("N");
            using (var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous))
            using (var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous))
            using (var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(3)))
            {
                Task connected = server.WaitForConnectionAsync(
                    timeout.Token);
                await client.ConnectAsync(3000, timeout.Token);
                await connected;

                Task<byte[]> read = WatchdogPipeServer
                    .ReadSingleRequestAsync(server, timeout.Token);
                foreach (string message in messages)
                {
                    byte[] bytes = StrictUtf8.GetBytes(message);
                    await client.WriteAsync(
                        bytes,
                        0,
                        bytes.Length,
                        timeout.Token);
                    await client.FlushAsync(timeout.Token);
                }

                return await read;
            }
        }
    }
}
