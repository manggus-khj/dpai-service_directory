using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.ServiceProcess;
using System.Threading;
using DEEPAi.ServiceDirectory.Infrastructure.WatchdogProtocol;

namespace DEEPAi.ServiceDirectory.Watchdog
{
    internal interface IMainServiceController
    {
        bool TryGetStatus(out WatchdogServiceStatus status);

        bool TryStart(TimeSpan timeout);

        bool TryStop(TimeSpan timeout);

        bool TryRestart(TimeSpan timeout);
    }

    internal sealed class MainServiceController : IMainServiceController
    {
        private static readonly TimeSpan PollInterval =
            TimeSpan.FromMilliseconds(50);

        private readonly string _serviceName;

        internal MainServiceController(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName)
                || serviceName.Length > 256)
            {
                throw new ArgumentException(
                    "A bounded main Windows Service name is required.",
                    nameof(serviceName));
            }

            _serviceName = serviceName;
        }

        public bool TryGetStatus(out WatchdogServiceStatus status)
        {
            status = default(WatchdogServiceStatus);
            try
            {
                using (var controller = new ServiceController(_serviceName))
                {
                    controller.Refresh();
                    status = MapStatus(controller.Status);
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Win32Exception)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (SecurityException)
            {
                return false;
            }
        }

        public bool TryStart(TimeSpan timeout)
        {
            return ExecuteControl(
                timeout,
                (controller, stopwatch, allowed) =>
                    StartCore(controller, stopwatch, allowed));
        }

        public bool TryStop(TimeSpan timeout)
        {
            return ExecuteControl(
                timeout,
                (controller, stopwatch, allowed) =>
                    StopCore(controller, stopwatch, allowed));
        }

        public bool TryRestart(TimeSpan timeout)
        {
            return ExecuteControl(
                timeout,
                (controller, stopwatch, allowed) =>
                    StopCore(controller, stopwatch, allowed)
                    && StartCore(controller, stopwatch, allowed));
        }

        internal static WatchdogServiceStatus MapStatus(
            ServiceControllerStatus status)
        {
            switch (status)
            {
                case ServiceControllerStatus.Stopped:
                    return WatchdogServiceStatus.Stopped;
                case ServiceControllerStatus.StartPending:
                    return WatchdogServiceStatus.StartPending;
                case ServiceControllerStatus.StopPending:
                    return WatchdogServiceStatus.StopPending;
                case ServiceControllerStatus.Running:
                    return WatchdogServiceStatus.Running;
                case ServiceControllerStatus.ContinuePending:
                    return WatchdogServiceStatus.ContinuePending;
                case ServiceControllerStatus.PausePending:
                    return WatchdogServiceStatus.PausePending;
                case ServiceControllerStatus.Paused:
                    return WatchdogServiceStatus.Paused;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(status),
                        status,
                        "A defined Windows Service status is required.");
            }
        }

        private bool ExecuteControl(
            TimeSpan timeout,
            Func<ServiceController, Stopwatch, TimeSpan, bool> operation)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            try
            {
                using (var controller = new ServiceController(_serviceName))
                {
                    return operation(
                        controller,
                        Stopwatch.StartNew(),
                        timeout);
                }
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Win32Exception)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (SecurityException)
            {
                return false;
            }
        }

        private static bool StartCore(
            ServiceController controller,
            Stopwatch stopwatch,
            TimeSpan allowed)
        {
            ServiceControllerStatus status;
            if (!TryReadStatusWithinDeadline(
                    controller,
                    stopwatch,
                    allowed,
                    out status))
            {
                return false;
            }

            if (status == ServiceControllerStatus.Running)
            {
                return true;
            }

            if (status == ServiceControllerStatus.StartPending
                || status == ServiceControllerStatus.ContinuePending)
            {
                return WaitForStatus(
                    controller,
                    ServiceControllerStatus.Running,
                    stopwatch,
                    allowed);
            }

            if (status == ServiceControllerStatus.StopPending
                && !WaitForStatus(
                    controller,
                    ServiceControllerStatus.Stopped,
                    stopwatch,
                    allowed))
            {
                return false;
            }

            if (!TryReadStatusWithinDeadline(
                    controller,
                    stopwatch,
                    allowed,
                    out status))
            {
                return false;
            }

            if (status == ServiceControllerStatus.PausePending
                && !WaitForStatus(
                    controller,
                    ServiceControllerStatus.Paused,
                    stopwatch,
                    allowed))
            {
                return false;
            }

            if (!TryReadStatusWithinDeadline(
                    controller,
                    stopwatch,
                    allowed,
                    out status))
            {
                return false;
            }

            if (status == ServiceControllerStatus.Paused)
            {
                controller.Continue();
            }
            else if (status == ServiceControllerStatus.Stopped)
            {
                controller.Start();
            }
            else if (status != ServiceControllerStatus.Running)
            {
                return false;
            }

            return WaitForStatus(
                controller,
                ServiceControllerStatus.Running,
                stopwatch,
                allowed);
        }

        private static bool StopCore(
            ServiceController controller,
            Stopwatch stopwatch,
            TimeSpan allowed)
        {
            ServiceControllerStatus status;
            if (!TryReadStatusWithinDeadline(
                    controller,
                    stopwatch,
                    allowed,
                    out status))
            {
                return false;
            }

            if (status == ServiceControllerStatus.Stopped)
            {
                return true;
            }

            if (status == ServiceControllerStatus.StopPending)
            {
                return WaitForStatus(
                    controller,
                    ServiceControllerStatus.Stopped,
                    stopwatch,
                    allowed);
            }

            controller.Stop();
            return WaitForStatus(
                controller,
                ServiceControllerStatus.Stopped,
                stopwatch,
                allowed);
        }

        private static bool WaitForStatus(
            ServiceController controller,
            ServiceControllerStatus expected,
            Stopwatch stopwatch,
            TimeSpan allowed)
        {
            while (stopwatch.Elapsed < allowed)
            {
                ServiceControllerStatus status;
                if (!TryReadStatusWithinDeadline(
                        controller,
                        stopwatch,
                        allowed,
                        out status))
                {
                    return false;
                }

                if (status == expected)
                {
                    return true;
                }

                TimeSpan remaining = allowed - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                TimeSpan delay = remaining < PollInterval
                    ? remaining
                    : PollInterval;
                Thread.Sleep(delay);
            }

            return false;
        }

        private static bool TryReadStatusWithinDeadline(
            ServiceController controller,
            Stopwatch stopwatch,
            TimeSpan allowed,
            out ServiceControllerStatus status)
        {
            controller.Refresh();
            status = controller.Status;
            return stopwatch.Elapsed < allowed;
        }
    }
}
