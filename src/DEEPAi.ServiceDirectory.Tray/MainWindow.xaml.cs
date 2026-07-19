using System.ComponentModel;
using System.Windows;
using DEEPAi.ServiceDirectory.Tray.ViewModels;

namespace DEEPAi.ServiceDirectory.Tray
{
    public partial class MainWindow : Window
    {
        private bool _allowClose;

        public MainWindow()
        {
            InitializeComponent();
            IsVisibleChanged += OnIsVisibleChanged;
            StateChanged += OnStateChanged;
        }

        public void AllowClose()
        {
            _allowClose = true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            ViewModel?.StopPolling();
            base.OnClosing(e);
        }

        private StatusMonitorViewModel ViewModel =>
            DataContext as StatusMonitorViewModel;

        private void OnIsVisibleChanged(
            object sender,
            DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
            {
                ViewModel?.StartPolling();
            }
            else
            {
                ViewModel?.StopPolling();
            }
        }

        private void OnStateChanged(object sender, System.EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
            }
        }

        private async void OnCreateCaBackupClick(
            object sender,
            RoutedEventArgs e)
        {
            StatusMonitorViewModel viewModel = ViewModel;
            if (viewModel == null)
            {
                return;
            }

            CreateCaBackupButton.IsEnabled = false;
            try
            {
                await viewModel.CreateCaBackupFromUiAsync(
                    CaBackupPasswordBox.Password,
                    CaBackupPasswordConfirmBox.Password);
            }
            finally
            {
                CaBackupPasswordBox.Clear();
                CaBackupPasswordConfirmBox.Clear();
                CreateCaBackupButton.IsEnabled = true;
            }
        }
    }
}
