using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using DEEPAi.ServiceDirectory.Infrastructure.Pki;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using DEEPAi.ServiceDirectory.InternalProtocol.Peer;

namespace DEEPAi.ServiceDirectory.Infrastructure.PeerProtocol
{
    internal sealed class PeerOutboundHttpRequest
    {
        private readonly byte[] _body;
        private readonly IReadOnlyDictionary<string, string> _headers;

        internal PeerOutboundHttpRequest(
            string peerEndpoint,
            string path,
            byte[] body,
            IDictionary<string, string> headers,
            TimeSpan timeout)
        {
            string canonicalPeerEndpoint;
            if (!AdminPeerEndpoint.TryNormalize(
                    peerEndpoint,
                    out canonicalPeerEndpoint)
                || !StringComparer.Ordinal.Equals(
                    peerEndpoint,
                    canonicalPeerEndpoint))
            {
                throw new ArgumentException(
                    "The Peer endpoint must be canonical HTTPS IPv4 on port 21000.",
                    nameof(peerEndpoint));
            }

            if (string.IsNullOrEmpty(path)
                || path[0] != '/'
                || path.IndexOf('?') >= 0
                || path.IndexOf('#') >= 0)
            {
                throw new ArgumentException(
                    "The Peer request path must be an absolute path without query or fragment.",
                    nameof(path));
            }

            if (body == null || body.Length == 0)
            {
                throw new ArgumentException(
                    "The Peer request body is required.",
                    nameof(body));
            }

            if (timeout <= TimeSpan.Zero
                || timeout.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            PeerEndpoint = peerEndpoint;
            Path = path;
            Timeout = timeout;
            _body = (byte[])body.Clone();
            _headers = CopyHeaders(headers);
        }

        internal string PeerEndpoint { get; }

        internal string Path { get; }

        internal TimeSpan Timeout { get; }

        internal byte[] CopyBody()
        {
            return (byte[])_body.Clone();
        }

        internal IReadOnlyDictionary<string, string> Headers => _headers;

        private static IReadOnlyDictionary<string, string> CopyHeaders(
            IDictionary<string, string> headers)
        {
            var copy = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            if (headers != null)
            {
                foreach (KeyValuePair<string, string> header in headers)
                {
                    if (string.IsNullOrEmpty(header.Key)
                        || string.IsNullOrEmpty(header.Value)
                        || header.Value.IndexOf('\r') >= 0
                        || header.Value.IndexOf('\n') >= 0)
                    {
                        throw new ArgumentException(
                            "A Peer request contains an invalid header.",
                            nameof(headers));
                    }

                    copy.Add(header.Key, header.Value);
                }
            }

            return new ReadOnlyDictionary<string, string>(copy);
        }
    }

    internal sealed class PeerInboundHttpResponse
    {
        private readonly byte[] _body;
        private readonly IReadOnlyDictionary<string,
            IReadOnlyList<string>> _headers;

        internal PeerInboundHttpResponse(
            int statusCode,
            string contentType,
            string contentEncoding,
            byte[] body,
            IDictionary<string, IReadOnlyList<string>> headers)
        {
            if (statusCode < 100 || statusCode > 599)
            {
                throw new ArgumentOutOfRangeException(nameof(statusCode));
            }

            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            StatusCode = statusCode;
            ContentType = contentType;
            ContentEncoding = contentEncoding;
            _body = (byte[])body.Clone();
            _headers = CopyHeaders(headers);
        }

        internal int StatusCode { get; }

        internal string ContentType { get; }

        internal string ContentEncoding { get; }

        internal byte[] CopyBody()
        {
            return (byte[])_body.Clone();
        }

        internal IReadOnlyList<string> GetHeaderValues(string name)
        {
            IReadOnlyList<string> values;
            return _headers.TryGetValue(name, out values)
                ? values
                : EmptyValues;
        }

        private static readonly IReadOnlyList<string> EmptyValues =
            Array.AsReadOnly(new string[0]);

        private static IReadOnlyDictionary<string, IReadOnlyList<string>>
            CopyHeaders(
            IDictionary<string, IReadOnlyList<string>> headers)
        {
            var copy = new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase);
            if (headers != null)
            {
                foreach (KeyValuePair<string, IReadOnlyList<string>> header
                    in headers)
                {
                    var values = new string[
                        header.Value == null ? 0 : header.Value.Count];
                    if (header.Value != null)
                    {
                        for (int index = 0;
                            index < header.Value.Count;
                            index++)
                        {
                            values[index] = header.Value[index];
                        }
                    }

                    copy.Add(header.Key, Array.AsReadOnly(values));
                }
            }

            return new ReadOnlyDictionary<string, IReadOnlyList<string>>(
                copy);
        }
    }

    internal sealed class PeerHttpTransportResult
    {
        private PeerHttpTransportResult(
            PeerInboundHttpResponse response,
            bool timedOut)
        {
            Response = response;
            TimedOut = timedOut;
        }

        internal bool IsSuccess => Response != null;

        internal bool TimedOut { get; }

        internal PeerInboundHttpResponse Response { get; }

        internal static PeerHttpTransportResult Success(
            PeerInboundHttpResponse response)
        {
            return new PeerHttpTransportResult(
                response ?? throw new ArgumentNullException(
                    nameof(response)),
                false);
        }

        internal static PeerHttpTransportResult Failure(bool timedOut)
        {
            return new PeerHttpTransportResult(null, timedOut);
        }
    }

    internal interface IPeerHttpTransport
    {
        PeerHttpTransportResult Send(PeerOutboundHttpRequest request);

        void CancelPendingRequests();
    }

    internal sealed class SystemPeerHttpTransport : IPeerHttpTransport
    {
        private readonly object _gate = new object();
        private readonly IPeerTlsTrustProvider _tlsTrustProvider;
        private readonly HashSet<HttpWebRequest> _activeRequests =
            new HashSet<HttpWebRequest>();
        private bool _cancellationRequested;

        internal SystemPeerHttpTransport()
        {
        }

        internal SystemPeerHttpTransport(
            IPeerTlsTrustProvider tlsTrustProvider)
        {
            _tlsTrustProvider = tlsTrustProvider
                ?? throw new ArgumentNullException(
                    nameof(tlsTrustProvider));
        }

        private static readonly string[] ResponseHeaderNames =
        {
            PeerAuthenticationContract.InstanceIdHeaderName,
            PeerAuthenticationContract.KeyEpochHeaderName,
            PeerAuthenticationContract.SessionIdHeaderName,
            PeerAuthenticationContract.TimestampHeaderName,
            PeerAuthenticationContract.NonceHeaderName,
            PeerAuthenticationContract.SignatureHeaderName,
            "X-DPAI-Pairing-MAC"
        };

        public PeerHttpTransportResult Send(PeerOutboundHttpRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            HttpWebRequest webRequest = null;
            byte[] body = null;
            PeerTlsTrustSnapshot tlsTrust = null;
            try
            {
                if (_tlsTrustProvider == null)
                {
                    return PeerHttpTransportResult.Failure(false);
                }

                tlsTrust = _tlsTrustProvider.CapturePeerTlsTrust(
                    request.PeerEndpoint,
                    DateTime.UtcNow);
                var uri = new Uri(
                    request.PeerEndpoint + request.Path,
                    UriKind.Absolute);
                webRequest = (HttpWebRequest)WebRequest.Create(uri);
                webRequest.ServerCertificateValidationCallback =
                    (sender, certificate, chain, errors) =>
                        tlsTrust.Validate(
                            certificate,
                            errors,
                            DateTime.UtcNow);
                lock (_gate)
                {
                    if (_cancellationRequested)
                    {
                        return PeerHttpTransportResult.Failure(false);
                    }

                    _activeRequests.Add(webRequest);
                }

                int timeoutMilliseconds = checked(
                    (int)Math.Ceiling(
                        request.Timeout.TotalMilliseconds));
                webRequest.Method = "POST";
                webRequest.ContentType = PeerSyncContract.XmlContentType;
                webRequest.AllowAutoRedirect = false;
                webRequest.AutomaticDecompression =
                    DecompressionMethods.None;
                webRequest.KeepAlive = false;
                webRequest.Proxy = null;
                webRequest.Timeout = timeoutMilliseconds;
                webRequest.ReadWriteTimeout = timeoutMilliseconds;

                foreach (KeyValuePair<string, string> header
                    in request.Headers)
                {
                    webRequest.Headers.Add(header.Key, header.Value);
                }

                body = request.CopyBody();
                webRequest.ContentLength = body.LongLength;
                using (Stream stream = webRequest.GetRequestStream())
                {
                    stream.Write(body, 0, body.Length);
                    stream.Flush();
                }

                using (var response =
                    (HttpWebResponse)webRequest.GetResponse())
                {
                    return ReadResponse(response, request.Path);
                }
            }
            catch (WebException exception)
            {
                var response = exception.Response as HttpWebResponse;
                if (response != null)
                {
                    using (response)
                    {
                        try
                        {
                            return ReadResponse(response, request.Path);
                        }
                        catch (IOException)
                        {
                            return PeerHttpTransportResult.Failure(false);
                        }
                        catch (InvalidDataException)
                        {
                            return PeerHttpTransportResult.Failure(false);
                        }
                    }
                }

                return PeerHttpTransportResult.Failure(
                    exception.Status == WebExceptionStatus.Timeout);
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is InvalidDataException
                || exception is UriFormatException
                || exception is CryptographicException
                || exception is UnauthorizedAccessException
                || exception is SecurityException)
            {
                return PeerHttpTransportResult.Failure(false);
            }
            finally
            {
                Clear(body);
                if (tlsTrust != null)
                {
                    tlsTrust.Dispose();
                }

                if (webRequest != null)
                {
                    lock (_gate)
                    {
                        _activeRequests.Remove(webRequest);
                    }

                    webRequest.Abort();
                }
            }
        }

        public void CancelPendingRequests()
        {
            HttpWebRequest[] requests;
            lock (_gate)
            {
                _cancellationRequested = true;
                requests = new HttpWebRequest[_activeRequests.Count];
                _activeRequests.CopyTo(requests);
            }

            foreach (HttpWebRequest request in requests)
            {
                try
                {
                    request.Abort();
                }
                catch (ObjectDisposedException)
                {
                    // A concurrently completing request has already released
                    // its transport resources.
                }
            }
        }

        private static PeerHttpTransportResult ReadResponse(
            HttpWebResponse response,
            string path)
        {
            int maximumBytes = StringComparer.Ordinal.Equals(
                path,
                PeerAuthenticationContract.ExchangePath)
                || StringComparer.Ordinal.Equals(
                    path,
                    PeerAuthenticationContract.PkiStatePath)
                ? PeerSyncContract.MaximumExchangeBodyBytes
                : PeerSyncContract.MaximumControlBodyBytes;
            if (response.ContentLength > maximumBytes)
            {
                throw new InvalidDataException(
                    "The Peer response exceeds its byte limit.");
            }

            byte[] body;
            Stream responseStream = response.GetResponseStream();
            if (responseStream == null)
            {
                body = new byte[0];
            }
            else
            {
                using (Stream input = responseStream)
                using (var collected = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    int total = 0;
                    while (true)
                    {
                        int read = input.Read(buffer, 0, buffer.Length);
                        if (read == 0)
                        {
                            break;
                        }

                        total = checked(total + read);
                        if (total > maximumBytes)
                        {
                            throw new InvalidDataException(
                                "The Peer response exceeds its byte limit.");
                        }

                        collected.Write(buffer, 0, read);
                    }

                    body = collected.ToArray();
                }
            }

            var headers = new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string name in ResponseHeaderNames)
            {
                string[] values = response.Headers.GetValues(name);
                headers.Add(
                    name,
                    Array.AsReadOnly(values ?? new string[0]));
            }

            return PeerHttpTransportResult.Success(
                new PeerInboundHttpResponse(
                    (int)response.StatusCode,
                    response.ContentType,
                    response.ContentEncoding,
                    body,
                    headers));
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
