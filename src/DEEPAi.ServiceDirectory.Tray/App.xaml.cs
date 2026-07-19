using System;
using System.Windows;
using DEEPAi.ServiceDirectory.Tray.Clients;
using DEEPAi.ServiceDirectory.Tray.ViewModels;
using H.NotifyIcon;

namespace DEEPAi.ServiceDirectory.Tray
{
    public partial class App : System.Windows.Application
    {
        private AdminApiClient _adminClient;
        private StatusMonitorViewModel _viewModel;
        private MainWindow _mainWindow;
        private TaskbarIcon _trayIcon;
        private bool _exitRequested;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _adminClient = new AdminApiClient();
            _mainWindow = new MainWindow();
            _viewModel = new StatusMonitorViewModel(
                _adminClient,
                new WatchdogPipeClient(),
                OpenMainWindow,
                ExitApplication,
                ConfirmDelete);
            _mainWindow.DataContext = _viewModel;

            _trayIcon = (TaskbarIcon)FindResource("ServiceDirectoryTrayIcon");
            _trayIcon.DataContext = _viewModel;
            _trayIcon.ForceCreate();

            MainWindow = _mainWindow;
            _mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _trayIcon?.Dispose();
            _viewModel?.Dispose();
            _adminClient?.Dispose();
            base.OnExit(e);
        }

        private void OpenMainWindow()
        {
            if (_mainWindow == null)
            {
                return;
            }

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Show();
            _mainWindow.Activate();
        }

        private void ExitApplication()
        {
            if (_exitRequested)
            {
                return;
            }

            _exitRequested = true;
            _mainWindow?.AllowClose();
            _mainWindow?.Close();
            Shutdown();
        }

        private bool ConfirmDelete(string message)
        {
            return MessageBox.Show(
                _mainWindow,
                message,
                "관리 작업 확인",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
        }
    }
}
