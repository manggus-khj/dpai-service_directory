using System;
using System.Security;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace DEEPAi.ServiceDirectory.Watchdog
{
    public sealed class WatchdogWindowsService : ServiceBase
    {
        public const string WatchdogServiceName =
            "DEEPAi.ServiceDirectory.Watchdog";
        public const string MainServiceName =
            "DEEPAi.ServiceDirectory";

        private const int ErrorExceptionInService = 1064;
        private const int ErrorServiceRequestTimeout = 1053;

        private readonly object _gate = new object();
        private WatchdogRuntimeHost _runtime;

        public WatchdogWindowsService()
        {
            ServiceName = WatchdogServiceName;
            CanStop = true;
            CanShutdown = true;
            CanPauseAndContinue = false;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            EnsureExpectedVirtualServiceAccount();
            var runtime = WatchdogRuntimeHost.CreateDefault(
                MainServiceName,
                WatchdogServiceName);
            runtime.Start();

            lock (_gate)
            {
                _runtime = runtime;
                runtime.Completion.ContinueWith(
                    OnRuntimeCompleted,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        protected override void OnStop()
        {
            WatchdogRuntimeHost runtime;
            lock (_gate)
            {
                runtime = _runtime;
                _runtime = null;
            }

            if (runtime == null)
            {
                return;
            }

            bool stopped = runtime.Stop(TimeSpan.FromSeconds(5));
            if (stopped)
            {
                runtime.Dispose();
            }
            else
            {
                ExitCode = ErrorServiceRequestTimeout;
            }
        }

        protected override void OnShutdown()
        {
            OnStop();
            base.OnShutdown();
        }

        private void OnRuntimeCompleted(Task completion)
        {
            if (!completion.IsFaulted)
            {
                return;
            }

            AggregateException observed = completion.Exception;
            if (observed == null)
            {
                return;
            }

            ExitCode = ErrorExceptionInService;
            try
            {
                Stop();
            }
            catch (InvalidOperationException)
            {
                // The SCM may already be stopping the service. ExitCode keeps
                // the worker failure visible to service recovery handling.
            }
        }

        private static void EnsureExpectedVirtualServiceAccount()
        {
            try
            {
                var account = new NTAccount(
                    "NT SERVICE",
                    WatchdogServiceName);
                var expectedSid = (SecurityIdentifier)account.Translate(
                    typeof(SecurityIdentifier));
                using (WindowsIdentity identity =
                    WindowsIdentity.GetCurrent(TokenAccessLevels.Query))
                {
                    if (identity.User == null)
                    {
                        throw new InvalidOperationException(
                            "The watchdog service identity has no Windows SID.");
                    }

                    if (!identity.User.Equals(expectedSid))
                    {
                        throw new InvalidOperationException(
                            "The watchdog service must run as its dedicated "
                            + "Windows virtual service account.");
                    }
                }
            }
            catch (IdentityNotMappedException exception)
            {
                throw new InvalidOperationException(
                    "The watchdog Windows virtual service account could not "
                    + "be resolved.",
                    exception);
            }
            catch (SecurityException exception)
            {
                throw new InvalidOperationException(
                    "The watchdog service identity could not be verified.",
                    exception);
            }
        }
    }
}
