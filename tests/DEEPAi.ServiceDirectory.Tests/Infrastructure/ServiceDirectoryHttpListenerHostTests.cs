using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DEEPAi.ServiceDirectory.ExternalProtocol.ExternalApi;
using DEEPAi.ServiceDirectory.Infrastructure.Http;
using DEEPAi.ServiceDirectory.Infrastructure.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DEEPAi.ServiceDirectory.Tests.Infrastructure
{
    [TestClass]
    public sealed class ServiceDirectoryHttpListenerHostTests
    {
        private static readonly IPEndPoint ConfiguredLocal =
            new IPEndPoint(IPAddress.Parse("10.20.30.40"), 21000);
        private static readonly IPEndPoint RemoteClient =
            new IPEndPoint(IPAddress.Parse("10.20.30.50"), 32000);
        private static readonly IPEndPoint LoopbackLocal =
            new IPEndPoint(IPAddress.Loopback, 21000);
        private static readonly IPEndPoint LoopbackClient =
            new IPEndPoint(IPAddress.Loopback, 32001);

        [TestMethod]
        public void ConfigurationUsesOnlyExactPrefixesAndRawAdminAuth()
        {
            var listener = new FakeHttpListenerServer();
            using (ServiceDirectoryHttpListenerHost host = CreateHost(
                listener))
            {
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "http://10.20.30.40:21000/",
                        "http://127.0.0.1:21000/"
                    },
                    listener.Prefixes);
                Assert.IsFalse(
                    listener.UnsafeConnectionNtlmAuthentication);

                Assert.AreEqual(
                    AuthenticationSchemes.Negotiate,
                    listener.SelectAuthentication(
                        Request(
                            "/admin/services?pageSize=10",
                            LoopbackLocal,
                            LoopbackClient)));
                Assert.AreEqual(
                    AuthenticationSchemes.Anonymous,
                    listener.SelectAuthentication(
                        Request(
                            "/admin%2fservices",
                            LoopbackLocal,
                            LoopbackClient)));
                Assert.AreEqual(
                    AuthenticationSchemes.Anonymous,
                    listener.SelectAuthentication(
                        Request(
                            "/api/health",
                            LoopbackLocal,
                            LoopbackClient)));
                Assert.AreEqual(
                    AuthenticationSchemes.Anonymous,
                    listener.SelectAuthentication(
                        Request(
                            "/ADMIN/services",
                            LoopbackLocal,
                            LoopbackClient)));
                Assert.AreEqual(
                    AuthenticationSchemes.Anonymous,
                    listener.SelectAuthentication(
                        Request(
                            "/admin/services",
                            ConfiguredLocal,
                            RemoteClient)));
                Assert.AreEqual(
                    AuthenticationSchemes.Anonymous,
                    listener.SelectAuthentication(
                        Request(
                            "/admin/services",
                            null,
                            null)));
            }
        }

        [TestMethod]
        public void DispatchPreservesRawTargetHeadersPrincipalAndMetadata()
        {
            var listener = new FakeHttpListenerServer();
            ExternalHttpRequestData capturedExternal = null;
            AdminHttpRequestData capturedAdmin = null;
            ExternalHttpRequestData capturedWatchdog = null;
            var principal = new GenericPrincipal(
                new GenericIdentity("operator"),
                new string[0]);

            using (var host = new ServiceDirectoryHttpListenerHost(
                listener,
                ConfiguredAddress(),
                request =>
                {
                    capturedExternal = request;
                    return ExternalHttpResponseData.Bodyless(404);
                },
                request =>
                {
                    capturedAdmin = request;
                    return AdminHttpResponseData.Bodyless(404);
                },
                request =>
                {
                    capturedWatchdog = request;
                    return ExternalHttpResponseData.Bodyless(404);
                },
                new SystemHttpDeadlineWaiter()))
            {
                host.Start();

                var externalBody = new MemoryStream(new byte[] { 1, 2, 3 });
                FakeHttpServerRequest externalRequest = Request(
                    "/api/services?productCode=AB%31",
                    ConfiguredLocal,
                    RemoteClient,
                    externalBody,
                    3,
                    "application/xml; charset=utf-8");
                externalRequest.SetHeader(
                    ExternalApiContract.ApiKeyHeaderName,
                    "first-value",
                    "second-value");
                externalRequest.SetHeader(
                    "Content-Encoding",
                    "gzip",
                    "br");
                var externalContext = new FakeHttpServerContext(
                    externalRequest,
                    null);
                listener.Enqueue(externalContext);
                WaitForResponse(externalContext);

                Assert.IsNotNull(capturedExternal);
                Assert.AreEqual("GET", capturedExternal.Method);
                Assert.AreEqual(
                    "/api/services",
                    capturedExternal.AbsolutePath);
                Assert.AreEqual(
                    "?productCode=AB%31",
                    capturedExternal.RawQuery);
                Assert.AreEqual(
                    2,
                    capturedExternal.ApiKeyHeaderValues.Count);
                Assert.AreEqual(
                    "first-value",
                    capturedExternal.ApiKeyHeaderValues[0]);
                Assert.AreEqual(
                    "second-value",
                    capturedExternal.ApiKeyHeaderValues[1]);
                Assert.AreEqual(
                    "gzip,br",
                    capturedExternal.ContentEncodingHeaderValue);
                Assert.AreEqual(3L, capturedExternal.DeclaredContentLength);
                Assert.AreSame(externalBody, capturedExternal.BodyStream);
                Assert.AreEqual(
                    ConfiguredLocal,
                    capturedExternal.LocalEndpoint);
                Assert.AreEqual(
                    RemoteClient,
                    capturedExternal.RemoteEndpoint);

                var adminContext = new FakeHttpServerContext(
                    Request(
                        "/admin/services?pageSize=10&cursor=A%2fB",
                        LoopbackLocal,
                        LoopbackClient),
                    principal);
                listener.Enqueue(adminContext);
                WaitForResponse(adminContext);

                Assert.IsNotNull(capturedAdmin);
                Assert.AreEqual(
                    "/admin/services",
                    capturedAdmin.AbsolutePath);
                Assert.AreEqual(
                    "?pageSize=10&cursor=A%2fB",
                    capturedAdmin.RawQuery);
                Assert.AreSame(principal, capturedAdmin.Principal);
                Assert.AreEqual(LoopbackLocal, capturedAdmin.LocalEndpoint);
                Assert.AreEqual(LoopbackClient, capturedAdmin.RemoteEndpoint);

                var watchdogContext = new FakeHttpServerContext(
                    Request(
                        "/api/health",
                        LoopbackLocal,
                        LoopbackClient),
                    null);
                listener.Enqueue(watchdogContext);
                WaitForResponse(watchdogContext);

                Assert.IsNotNull(capturedWatchdog);
                Assert.AreEqual(
                    "/api/health",
                    capturedWatchdog.AbsolutePath);
                Assert.IsTrue(host.Stop(TimeSpan.FromSeconds(2)));
            }
        }

        [TestMethod]
        public void EncodedApiCandidatesReachBoundaryButPeerAndOutsideDoNot()
        {
            var listener = new FakeHttpListenerServer();
            int externalCalls = 0;
            int adminCalls = 0;
            int watchdogCalls = 0;
            string externalPath = null;
            string watchdogPath = null;

            using (var host = new ServiceDirectoryHttpListenerHost(
                listener,
                ConfiguredAddress(),
                request =>
                {
                    externalCalls++;
                    externalPath = request.AbsolutePath;
                    return ExternalHttpResponseData.Bodyless(404);
                },
                request =>
                {
                    adminCalls++;
                    return AdminHttpResponseData.Bodyless(404);
                },
                request =>
                {
                    watchdogCalls++;
                    watchdogPath = request.AbsolutePath;
                    return ExternalHttpResponseData.Bodyless(404);
                },
                new SystemHttpDeadlineWaiter()))
            {
                host.Start();

                FakeHttpServerContext encodedExternal = Context(
                    "/api%2fhealth",
                    ConfiguredLocal,
                    RemoteClient);
                listener.Enqueue(encodedExternal);
                WaitForResponse(encodedExternal);
                Assert.AreEqual(1, externalCalls);
                Assert.AreEqual("/api%2fhealth", externalPath);

                FakeHttpServerContext encodedWatchdog = Context(
                    "/api%2fhealth",
                    LoopbackLocal,
                    LoopbackClient);
                listener.Enqueue(encodedWatchdog);
                WaitForResponse(encodedWatchdog);
                Assert.AreEqual(1, watchdogCalls);
                Assert.AreEqual("/api%2fhealth", watchdogPath);

                FakeHttpServerContext encodedAdmin = Context(
                    "/admin%2fservices",
                    LoopbackLocal,
                    LoopbackClient);
                listener.Enqueue(encodedAdmin);
                AssertBodyless404(encodedAdmin);
                Assert.AreEqual(0, adminCalls);
                Assert.AreEqual(1, watchdogCalls);

                FakeHttpServerContext peer = Context(
                    "/api/sync/exchange",
                    ConfiguredLocal,
                    RemoteClient);
                listener.Enqueue(peer);
                AssertBodyless404(peer);
                Assert.AreEqual(1, externalCalls);

                FakeHttpServerContext outside = Context(
                    "/outside",
                    LoopbackLocal,
                    LoopbackClient);
                listener.Enqueue(outside);
                AssertBodyless404(outside);
                Assert.AreEqual(1, watchdogCalls);

                FakeHttpServerContext remoteAdmin = Context(
                    "/admin/services",
                    ConfiguredLocal,
                    RemoteClient);
                listener.Enqueue(remoteAdmin);
                AssertBodyless404(remoteAdmin);
                Assert.AreEqual(0, adminCalls);

                Assert.IsTrue(host.Stop(TimeSpan.FromSeconds(2)));
            }
        }

        [TestMethod]
        public void ResponseFieldsAndRetryAfterAreWrittenWithoutMutation()
        {
            var listener = new FakeHttpListenerServer();
            ExternalHttpResponseData expected =
                ExternalHttpResponseData.XmlError(
                    429,
                    ExternalResponseCode.LimitExceeded,
                    7);
            using (var host = new ServiceDirectoryHttpListenerHost(
                listener,
                ConfiguredAddress(),
                request => expected,
                request => AdminHttpResponseData.Bodyless(404),
                request => ExternalHttpResponseData.Bodyless(404),
                new SystemHttpDeadlineWaiter()))
            {
                host.Start();
                FakeHttpServerContext context = Context(
                    "/api/health",
                    ConfiguredLocal,
                    RemoteClient);
                listener.Enqueue(context);
                HttpTransportResponseData actual = WaitForResponse(context);

                Assert.AreEqual(429, actual.StatusCode);
                Assert.AreEqual(expected.ContentType, actual.ContentType);
                Assert.AreEqual(expected.ContentLength, actual.ContentLength);
                Assert.AreEqual(7, actual.RetryAfterSeconds);
                CollectionAssert.AreEqual(
                    expected.GetBody(),
                    actual.GetBody());
                Assert.IsTrue(host.Stop(TimeSpan.FromSeconds(2)));
            }
        }

        [TestMethod]
        public void DeadlinePolicyCoversWholeAdapterAndResponseWork()
        {
            Assert.AreEqual(
                TimeSpan.FromSeconds(5),
                HttpListenerDeadlinePolicy.GetDeadline(
                    ServiceDirectoryHttpRoute.External,
                    "GET",
                    "/api/services"));
            Assert.AreEqual(
                TimeSpan.FromSeconds(10),
                HttpListenerDeadlinePolicy.GetDeadline(
                    ServiceDirectoryHttpRoute.External,
                    "POST",
                    "/api/registration"));
            Assert.AreEqual(
                TimeSpan.FromSeconds(10),
                HttpListenerDeadlinePolicy.GetDeadline(
                    ServiceDirectoryHttpRoute.Admin,
                    "POST",
                    "/admin/sync/now"));
            Assert.AreEqual(
                TimeSpan.FromSeconds(5),
                HttpListenerDeadlinePolicy.GetDeadline(
                    ServiceDirectoryHttpRoute.WatchdogHealth,
                    "GET",
                    "/api/health"));

            var listener = new FakeHttpListenerServer();
            var deadline = new ControlledDeadlineWaiter();
            var processorEntered = new ManualResetEventSlim(false);
            var releaseProcessor = new ManualResetEventSlim(false);
            using (var host = new ServiceDirectoryHttpListenerHost(
                listener,
                ConfiguredAddress(),
                request =>
                {
                    processorEntered.Set();
                    releaseProcessor.Wait();
                    return ExternalHttpResponseData.Bodyless(404);
                },
                request => AdminHttpResponseData.Bodyless(404),
                request => ExternalHttpResponseData.Bodyless(404),
                deadline))
            {
                var context = new FakeHttpServerContext(
                    Request(
                        "/api/registration",
                        ConfiguredLocal,
                        RemoteClient,
                        new MemoryStream(new byte[0]),
                        0,
                        "application/xml; charset=utf-8",
                        "POST"),
                    null);
                context.AbortAction = releaseProcessor.Set;
                host.Start();
                listener.Enqueue(context);

                Assert.IsTrue(processorEntered.Wait(TimeSpan.FromSeconds(2)));
                Assert.IsTrue(deadline.Called.Wait(TimeSpan.FromSeconds(2)));
                Assert.AreEqual(
                    TimeSpan.FromSeconds(10),
                    deadline.LastTimeout);
                Assert.AreEqual(
                    TimeSpan.FromSeconds(10),
                    context.RequestData.LastBodyReadTimeout);

                deadline.Trigger();
                Assert.IsTrue(context.Aborted.Wait(TimeSpan.FromSeconds(2)));
                Assert.IsTrue(host.Stop(TimeSpan.FromSeconds(2)));
                Assert.IsFalse(context.Written.Task.IsCompleted);
            }

            processorEntered.Dispose();
            releaseProcessor.Dispose();
        }

        [TestMethod]
        public void DeadlineAlsoAbortsBlockedResponseWrite()
        {
            var listener = new FakeHttpListenerServer();
            var deadline = new ControlledDeadlineWaiter();
            using (var host = new ServiceDirectoryHttpListenerHost(
                listener,
                ConfiguredAddress(),
                request => ExternalHttpResponseData.Bodyless(404),
                request => AdminHttpResponseData.Bodyless(404),
                request => ExternalHttpResponseData.Bodyless(404),
                deadline))
            {
                FakeHttpServerContext context = Context(
                    "/api/health",
                    ConfiguredLocal,
                    RemoteClient);
                context.BlockResponseWriteUntilAbort = true;
                host.Start();
                listener.Enqueue(context);

                Assert.IsTrue(
                    context.WriteStarted.Wait(TimeSpan.FromSeconds(2)));
                Assert.IsTrue(deadline.Called.Wait(TimeSpan.FromSeconds(2)));
                deadline.Trigger();
                Assert.IsTrue(context.Aborted.Wait(TimeSpan.FromSeconds(2)));
                Assert.IsTrue(host.Stop(TimeSpan.FromSeconds(2)));
                Assert.IsFalse(context.Written.Task.IsCompleted);
            }
        }

        [TestMethod]
        public void BeginStopSignalsBeforeSharedDeadlineWait()
        {
            var listener = new FakeHttpListenerServer();
            using (ServiceDirectoryHttpListenerHost host = CreateHost(
                listener))
            {
                host.Start();
                host.BeginStop();

                Assert.AreEqual(1, listener.StopCount);
                Assert.IsTrue(
                    host.WaitForStop(TimeSpan.FromSeconds(2)));
                Assert.IsTrue(host.Completion.IsCompleted);
                Assert.IsNull(host.FatalException);
            }
        }

        [TestMethod]
        public void StopUnblocksPendingAcceptAndIsIdempotent()
        {
            var listener = new FakeHttpListenerServer();
            using (ServiceDirectoryHttpListenerHost host = CreateHost(
                listener))
            {
                host.Start();
                Assert.IsTrue(host.Stop(TimeSpan.FromSeconds(2)));
                Assert.IsTrue(host.Completion.IsCompleted);
                Assert.IsNull(host.FatalException);
                Assert.AreEqual(1, listener.StopCount);
                Assert.IsTrue(host.Stop(TimeSpan.Zero));
                Assert.AreEqual(1, listener.StopCount);
            }
        }

        private static ServiceDirectoryHttpListenerHost CreateHost(
            FakeHttpListenerServer listener)
        {
            return new ServiceDirectoryHttpListenerHost(
                listener,
                ConfiguredAddress(),
                request => ExternalHttpResponseData.Bodyless(404),
                request => AdminHttpResponseData.Bodyless(404),
                request => ExternalHttpResponseData.Bodyless(404),
                new SystemHttpDeadlineWaiter());
        }

        private static ServiceDirectoryListenerAddress ConfiguredAddress()
        {
            ServiceDirectoryListenerAddress address;
            Assert.IsTrue(
                ServiceDirectoryListenerAddress.TryCreate(
                    "10.20.30.40",
                    out address));
            return address;
        }

        private static FakeHttpServerContext Context(
            string rawUrl,
            IPEndPoint localEndpoint,
            IPEndPoint remoteEndpoint)
        {
            return new FakeHttpServerContext(
                Request(rawUrl, localEndpoint, remoteEndpoint),
                null);
        }

        private static FakeHttpServerRequest Request(
            string rawUrl,
            IPEndPoint localEndpoint,
            IPEndPoint remoteEndpoint,
            Stream body = null,
            long contentLength = 0,
            string contentType = null,
            string method = "GET")
        {
            return new FakeHttpServerRequest(
                rawUrl,
                method,
                contentType,
                contentLength,
                body ?? new MemoryStream(new byte[0]),
                localEndpoint,
                remoteEndpoint);
        }

        private static HttpTransportResponseData WaitForResponse(
            FakeHttpServerContext context)
        {
            Assert.IsTrue(
                context.Written.Task.Wait(TimeSpan.FromSeconds(2)),
                "The virtual HTTP response was not completed.");
            return context.Written.Task.Result;
        }

        private static void AssertBodyless404(
            FakeHttpServerContext context)
        {
            HttpTransportResponseData response = WaitForResponse(context);
            Assert.AreEqual(404, response.StatusCode);
            Assert.AreEqual(0, response.ContentLength);
            Assert.IsNull(response.ContentType);
            Assert.IsNull(response.RetryAfterSeconds);
        }

        private sealed class FakeHttpListenerServer : IHttpListenerServer
        {
            private readonly object _gate = new object();
            private readonly Queue<IHttpServerContext> _contexts =
                new Queue<IHttpServerContext>();
            private readonly SemaphoreSlim _available =
                new SemaphoreSlim(0);
            private Func<IHttpServerRequest, AuthenticationSchemes>
                _authenticationSelector;
            private bool _stopped;
            private bool _disposed;

            internal FakeHttpListenerServer()
            {
                Prefixes = new List<string>();
            }

            internal List<string> Prefixes { get; }

            internal bool UnsafeConnectionNtlmAuthentication { get; private set; }

            internal int StopCount { get; private set; }

            public void Configure(
                IEnumerable<string> prefixes,
                Func<IHttpServerRequest, AuthenticationSchemes>
                    authenticationSelector,
                bool unsafeConnectionNtlmAuthentication)
            {
                Prefixes.AddRange(prefixes);
                _authenticationSelector = authenticationSelector;
                UnsafeConnectionNtlmAuthentication =
                    unsafeConnectionNtlmAuthentication;
            }

            public void Start()
            {
            }

            public async Task<IHttpServerContext> AcceptAsync()
            {
                await _available.WaitAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed || _stopped)
                    {
                        throw new ObjectDisposedException(
                            nameof(FakeHttpListenerServer));
                    }

                    return _contexts.Dequeue();
                }
            }

            public void Stop()
            {
                lock (_gate)
                {
                    if (_stopped)
                    {
                        return;
                    }

                    _stopped = true;
                    StopCount++;
                }

                _available.Release();
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    if (!_stopped)
                    {
                        _available.Release();
                    }
                }
            }

            internal void Enqueue(IHttpServerContext context)
            {
                lock (_gate)
                {
                    _contexts.Enqueue(context);
                }

                _available.Release();
            }

            internal AuthenticationSchemes SelectAuthentication(
                IHttpServerRequest request)
            {
                return _authenticationSelector(request);
            }
        }

        private sealed class FakeHttpServerRequest : IHttpServerRequest
        {
            private readonly Dictionary<string, IReadOnlyList<string>>
                _headers = new Dictionary<string, IReadOnlyList<string>>(
                    StringComparer.OrdinalIgnoreCase);

            internal FakeHttpServerRequest(
                string rawUrl,
                string httpMethod,
                string contentType,
                long contentLength,
                Stream inputStream,
                IPEndPoint localEndPoint,
                IPEndPoint remoteEndPoint)
            {
                RawUrl = rawUrl;
                HttpMethod = httpMethod;
                ContentType = contentType;
                ContentLength64 = contentLength;
                InputStream = inputStream;
                LocalEndPoint = localEndPoint;
                RemoteEndPoint = remoteEndPoint;
            }

            public string RawUrl { get; }

            public string HttpMethod { get; }

            public string ContentType { get; }

            public long ContentLength64 { get; }

            public Stream InputStream { get; }

            public IPEndPoint LocalEndPoint { get; }

            public IPEndPoint RemoteEndPoint { get; }

            internal TimeSpan LastBodyReadTimeout { get; private set; }

            public IReadOnlyList<string> GetHeaderValues(string name)
            {
                IReadOnlyList<string> values;
                return _headers.TryGetValue(name, out values)
                    ? values
                    : new string[0];
            }

            public void SetBodyReadTimeout(TimeSpan timeout)
            {
                LastBodyReadTimeout = timeout;
            }

            internal void SetHeader(string name, params string[] values)
            {
                _headers[name] = new ReadOnlyCollection<string>(
                    (string[])values.Clone());
            }
        }

        private sealed class FakeHttpServerContext : IHttpServerContext
        {
            private int _aborted;

            internal FakeHttpServerContext(
                FakeHttpServerRequest request,
                IPrincipal principal)
            {
                RequestData = request;
                Principal = principal;
                Written = new TaskCompletionSource<HttpTransportResponseData>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Aborted = new ManualResetEventSlim(false);
                WriteStarted = new ManualResetEventSlim(false);
            }

            public IHttpServerRequest Request => RequestData;

            public IPrincipal Principal { get; }

            internal FakeHttpServerRequest RequestData { get; }

            internal TaskCompletionSource<HttpTransportResponseData> Written
            {
                get;
            }

            internal ManualResetEventSlim Aborted { get; }

            internal ManualResetEventSlim WriteStarted { get; }

            internal Action AbortAction { get; set; }

            internal bool BlockResponseWriteUntilAbort { get; set; }

            public async Task WriteResponseAsync(
                HttpTransportResponseData response,
                CancellationToken cancellationToken)
            {
                WriteStarted.Set();
                if (BlockResponseWriteUntilAbort)
                {
                    await Task.Run(() => Aborted.Wait(cancellationToken))
                        .ConfigureAwait(false);
                }

                if (Volatile.Read(ref _aborted) != 0)
                {
                    throw new HttpListenerTransportException(
                        "The fake response was aborted.",
                        new IOException());
                }

                Written.TrySetResult(response);
            }

            public void Abort()
            {
                if (Interlocked.Exchange(ref _aborted, 1) != 0)
                {
                    return;
                }

                Aborted.Set();
                if (AbortAction != null)
                {
                    AbortAction();
                }
            }

            public void Dispose()
            {
            }
        }

        private sealed class ControlledDeadlineWaiter : IHttpDeadlineWaiter
        {
            private readonly TaskCompletionSource<object> _trigger =
                new TaskCompletionSource<object>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            internal ControlledDeadlineWaiter()
            {
                Called = new ManualResetEventSlim(false);
            }

            internal ManualResetEventSlim Called { get; }

            internal TimeSpan LastTimeout { get; private set; }

            public Task WaitAsync(
                TimeSpan timeout,
                CancellationToken cancellationToken)
            {
                LastTimeout = timeout;
                Called.Set();
                cancellationToken.Register(
                    () => _trigger.TrySetCanceled());
                return _trigger.Task;
            }

            internal void Trigger()
            {
                _trigger.TrySetResult(null);
            }
        }
    }
}
