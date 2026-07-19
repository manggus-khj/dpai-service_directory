using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Cache;
using DEEPAi.ServiceDirectory.Domain;
using DEEPAi.ServiceDirectory.ExternalProtocol.Authentication;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;

namespace DEEPAi.ServiceDirectory.Watchdog
{
    internal interface IWatchdogHealthProbe
    {
        bool Check();
    }

    internal sealed class WatchdogHealthProbe : IWatchdogHealthProbe
    {
        internal const string HealthUrl =
            "http://127.0.0.1:21000/api/health";
        internal static readonly TimeSpan RequestTimeout =
            TimeSpan.FromSeconds(3);

        private static readonly ProductCode WatchdogProductCode =
            CreateWatchdogProductCode();

        public bool Check()
        {
            try
            {
                Stopwatch deadline = Stopwatch.StartNew();
                var request = (HttpWebRequest)WebRequest.Create(HealthUrl);
                request.Method = "GET";
                request.Proxy = null;
                request.AllowAutoRedirect = false;
                request.AutomaticDecompression = DecompressionMethods.None;
                request.KeepAlive = false;
                request.Pipelined = false;
                request.CachePolicy = new RequestCachePolicy(
                    RequestCacheLevel.NoCacheNoStore);
                request.Headers[ExternalApiContract.ApiKeyHeaderName] =
                    DailyApiKeyCodec.Create(WatchdogProductCode);

                int remainingMilliseconds =
                    GetRemainingTimeoutMilliseconds(deadline);
                request.Timeout = remainingMilliseconds;
                request.ReadWriteTimeout = remainingMilliseconds;

                bool isValid;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    EnsureDeadlineHasNotExpired(deadline);
                    if (response.StatusCode != HttpStatusCode.OK
                        || !StringComparer.OrdinalIgnoreCase.Equals(
                            response.ContentType,
                            ExternalApiContract.XmlContentType)
                        || response.ContentLength
                            > ExternalApiContract.MaximumBodyBytes)
                    {
                        return false;
                    }

                    byte[] body = ReadBoundedBody(response, deadline);
                    isValid = WatchdogHealthResponseValidator.IsValid(body);
                }

                EnsureDeadlineHasNotExpired(deadline);
                return isValid;
            }
            catch (WebException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static byte[] ReadBoundedBody(
            HttpWebResponse response,
            Stopwatch deadline)
        {
            if (deadline == null)
            {
                throw new ArgumentNullException(nameof(deadline));
            }

            using (Stream input = response.GetResponseStream())
            using (var output = new MemoryStream())
            {
                if (input == null)
                {
                    throw new InvalidDataException(
                        "The health response has no body stream.");
                }

                if (!input.CanTimeout)
                {
                    throw new InvalidDataException(
                        "The health response body stream cannot enforce the deadline.");
                }

                var buffer = new byte[4096];
                while (true)
                {
                    int remainingMilliseconds =
                        GetRemainingTimeoutMilliseconds(deadline);
                    input.ReadTimeout = remainingMilliseconds;

                    int read = input.Read(buffer, 0, buffer.Length);
                    EnsureDeadlineHasNotExpired(deadline);
                    if (read == 0)
                    {
                        return output.ToArray();
                    }

                    if (output.Length + read
                        > ExternalApiContract.MaximumBodyBytes)
                    {
                        throw new InvalidDataException(
                            "The health response exceeds the body limit.");
                    }

                    output.Write(buffer, 0, read);
                }
            }
        }

        private static int GetRemainingTimeoutMilliseconds(
            Stopwatch deadline)
        {
            if (deadline == null)
            {
                throw new ArgumentNullException(nameof(deadline));
            }

            double remainingMilliseconds =
                (RequestTimeout - deadline.Elapsed).TotalMilliseconds;
            int boundedMilliseconds = remainingMilliseconds >= int.MaxValue
                ? int.MaxValue
                : checked((int)Math.Floor(remainingMilliseconds));
            if (boundedMilliseconds <= 0)
            {
                throw new IOException(
                    "The health response deadline expired.");
            }

            return boundedMilliseconds;
        }

        private static void EnsureDeadlineHasNotExpired(
            Stopwatch deadline)
        {
            if (deadline == null)
            {
                throw new ArgumentNullException(nameof(deadline));
            }

            if (deadline.Elapsed >= RequestTimeout)
            {
                throw new IOException(
                    "The health response deadline expired.");
            }
        }

        private static ProductCode CreateWatchdogProductCode()
        {
            ProductCode productCode;
            if (!ProductCode.TryCreate("WDOG", out productCode))
            {
                throw new InvalidOperationException(
                    "The fixed watchdog ProductCode is invalid.");
            }

            return productCode;
        }
    }
}
