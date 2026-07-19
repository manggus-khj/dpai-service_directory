using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace DEEPAi.ServiceDirectory.Infrastructure.Http
{
    internal interface IHttpListenerServer : IDisposable
    {
        void Configure(
            IEnumerable<string> prefixes,
            Func<IHttpServerRequest, AuthenticationSchemes>
                authenticationSelector,
            bool unsafeConnectionNtlmAuthentication);

        void Start();

        Task<IHttpServerContext> AcceptAsync();

        void Stop();
    }

    internal interface IHttpServerRequest
    {
        string RawUrl { get; }

        string HttpMethod { get; }

        string ContentType { get; }

        long ContentLength64 { get; }

        Stream InputStream { get; }

        IPEndPoint LocalEndPoint { get; }

        IPEndPoint RemoteEndPoint { get; }

        IReadOnlyList<string> GetHeaderValues(string name);

        void SetBodyReadTimeout(TimeSpan timeout);
    }

    internal interface IHttpServerContext : IDisposable
    {
        IHttpServerRequest Request { get; }

        IPrincipal Principal { get; }

        Task WriteResponseAsync(
            HttpTransportResponseData response,
            CancellationToken cancellationToken);

        void Abort();
    }

    internal sealed class HttpListenerTransportException : IOException
    {
        internal HttpListenerTransportException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class SystemHttpListenerServer : IHttpListenerServer
    {
        private readonly HttpListener _listener;
        private bool _configured;
        private bool _disposed;

        internal SystemHttpListenerServer()
        {
            _listener = new HttpListener();
        }

        public void Configure(
            IEnumerable<string> prefixes,
            Func<IHttpServerRequest, AuthenticationSchemes>
                authenticationSelector,
            bool unsafeConnectionNtlmAuthentication)
        {
            if (prefixes == null)
            {
                throw new ArgumentNullException(nameof(prefixes));
            }

            if (authenticationSelector == null)
            {
                throw new ArgumentNullException(
                    nameof(authenticationSelector));
            }

            if (unsafeConnectionNtlmAuthentication)
            {
                throw new ArgumentException(
                    "Connection-level NTLM credential reuse is forbidden.",
                    nameof(unsafeConnectionNtlmAuthentication));
            }

            ThrowIfDisposed();
            if (_configured || _listener.IsListening)
            {
                throw new InvalidOperationException(
                    "The HTTP listener is already configured.");
            }

            int prefixCount = 0;
            foreach (string prefix in prefixes)
            {
                if (string.IsNullOrEmpty(prefix))
                {
                    throw new ArgumentException(
                        "Listener prefixes must not be empty.",
                        nameof(prefixes));
                }

                _listener.Prefixes.Add(prefix);
                prefixCount++;
            }

            if (prefixCount == 0)
            {
                throw new ArgumentException(
                    "At least one listener prefix is required.",
                    nameof(prefixes));
            }

            _listener.UnsafeConnectionNtlmAuthentication =
                unsafeConnectionNtlmAuthentication;
            _listener.AuthenticationSchemeSelectorDelegate = request =>
                authenticationSelector(
                    new SystemHttpServerRequest(request));
            _configured = true;
        }

        public void Start()
        {
            ThrowIfDisposed();
            if (!_configured)
            {
                throw new InvalidOperationException(
                    "The HTTP listener must be configured before start.");
            }

            _listener.Start();
        }

        public async Task<IHttpServerContext> AcceptAsync()
        {
            ThrowIfDisposed();
            HttpListenerContext context = await _listener
                .GetContextAsync()
                .ConfigureAwait(false);
            return new SystemHttpServerContext(context);
        }

        public void Stop()
        {
            if (_disposed)
            {
                return;
            }

            _listener.Stop();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _listener.Close();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(SystemHttpListenerServer));
            }
        }
    }

    internal sealed class SystemHttpServerRequest : IHttpServerRequest
    {
        private readonly HttpListenerRequest _request;

        internal SystemHttpServerRequest(HttpListenerRequest request)
        {
            _request = request
                ?? throw new ArgumentNullException(nameof(request));
        }

        public string RawUrl => _request.RawUrl;

        public string HttpMethod => _request.HttpMethod;

        public string ContentType => _request.ContentType;

        public long ContentLength64 => _request.ContentLength64;

        public Stream InputStream => _request.InputStream;

        public IPEndPoint LocalEndPoint => _request.LocalEndPoint;

        public IPEndPoint RemoteEndPoint => _request.RemoteEndPoint;

        public IReadOnlyList<string> GetHeaderValues(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(
                    "A header name is required.",
                    nameof(name));
            }

            string[] values = _request.Headers.GetValues(name);
            return values == null
                ? (IReadOnlyList<string>)new string[0]
                : Array.AsReadOnly((string[])values.Clone());
        }

        public void SetBodyReadTimeout(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            Stream stream = _request.InputStream;
            if (stream == null || !stream.CanTimeout)
            {
                return;
            }

            double milliseconds = Math.Ceiling(timeout.TotalMilliseconds);
            stream.ReadTimeout = milliseconds >= int.MaxValue
                ? int.MaxValue
                : Math.Max(1, (int)milliseconds);
        }
    }

    internal sealed class SystemHttpServerContext : IHttpServerContext
    {
        private const int Open = 0;
        private const int Writing = 1;
        private const int Finished = 2;

        private readonly HttpListenerContext _context;
        private readonly SystemHttpServerRequest _request;
        private int _state;

        internal SystemHttpServerContext(HttpListenerContext context)
        {
            _context = context
                ?? throw new ArgumentNullException(nameof(context));
            _request = new SystemHttpServerRequest(context.Request);
        }

        public IHttpServerRequest Request => _request;

        public IPrincipal Principal => _context.User;

        public async Task WriteResponseAsync(
            HttpTransportResponseData response,
            CancellationToken cancellationToken)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            if (Interlocked.CompareExchange(
                    ref _state,
                    Writing,
                    Open) != Open)
            {
                throw new InvalidOperationException(
                    "The HTTP context is already complete.");
            }

            HttpListenerResponse target = _context.Response;
            byte[] body = response.GetBody();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                target.StatusCode = response.StatusCode;
                target.SendChunked = false;
                target.ContentLength64 = body.LongLength;
                if (response.ContentType != null)
                {
                    target.ContentType = response.ContentType;
                }

                foreach (KeyValuePair<string, string> header
                    in response.Headers)
                {
                    target.AddHeader(header.Key, header.Value);
                }

                if (response.RetryAfterSeconds.HasValue)
                {
                    target.AddHeader(
                        "Retry-After",
                        response.RetryAfterSeconds.Value.ToString(
                            CultureInfo.InvariantCulture));
                }

                if (body.Length != 0)
                {
                    await target.OutputStream.WriteAsync(
                            body,
                            0,
                            body.Length,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await target.OutputStream.FlushAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                target.Close();
                Interlocked.Exchange(ref _state, Finished);
            }
            catch (OperationCanceledException)
            {
                Abort();
                throw;
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is HttpListenerException
                || exception is ObjectDisposedException
                || exception is InvalidOperationException)
            {
                Abort();
                throw new HttpListenerTransportException(
                    "The HTTP response could not be completed.",
                    exception);
            }
        }

        public void Abort()
        {
            if (Interlocked.Exchange(ref _state, Finished) == Finished)
            {
                return;
            }

            TryCloseRequestStream();
            try
            {
                _context.Response.Abort();
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is HttpListenerException
                || exception is ObjectDisposedException
                || exception is InvalidOperationException)
            {
                // The context is already unusable, which is the abort goal.
            }
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _state) != Finished)
            {
                Abort();
                return;
            }

            TryCloseRequestStream();
        }

        private void TryCloseRequestStream()
        {
            try
            {
                Stream stream = _context.Request.InputStream;
                if (stream != null)
                {
                    stream.Close();
                }
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is ObjectDisposedException
                || exception is InvalidOperationException)
            {
                // Closing an already closed or disconnected request is safe.
            }
        }
    }
}
