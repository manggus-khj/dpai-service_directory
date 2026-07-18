using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DEEPAi.ServiceDirectory.Infrastructure.WatchdogProtocol;
using DEEPAi.ServiceDirectory.InternalProtocol.Admin;
using DEEPAi.ServiceDirectory.Tray.Clients;
using DEEPAi.ServiceDirectory.Tray.Commands;

namespace DEEPAi.ServiceDirectory.Tray.ViewModels
{
    public sealed partial class StatusMonitorViewModel : ObservableObject, IDisposable
    {
        private static readonly ImageSource RunningTrayIcon = LoadTrayIcon(
            "pack://application:,,,/DEEPAi.ServiceDirectory.Tray;component/Assets/tray_running.png");
        private static readonly ImageSource StoppedTrayIcon = LoadTrayIcon(
            "pack://application:,,,/DEEPAi.ServiceDirectory.Tray;component/Assets/tray_stopped.png");

        private readonly AdminApiClient _adminClient;
        private readonly WatchdogPipeClient _watchdogClient;
        private readonly Func<string, bool> _confirmDelete;
        private readonly CancellationTokenSource _lifetimeCancellation =
            new CancellationTokenSource();
        private readonly SemaphoreSlim _syncRefreshGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _watchdogRefreshGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _listRefreshGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _loggingRefreshGate = new SemaphoreSlim(1, 1);
        private readonly object _pollingStateGate = new object();

        private int _selectedPageIndex = 1;
        private AdminPendingItem _selectedPending;
        private AdminServiceItem _selectedService;
        private bool _isAdminConnected;
        private bool _isWatchdogConnected;
        private string _mainServiceConnectionText = "연결 안 됨";
        private string _watchdogConnectionText = "연결 안 됨";
        private string _watchdogServiceStatusText = "확인할 수 없음";
        private string _watchdogHealthText = "확인할 수 없음";
        private string _watchdogFailuresText = "—";
        private string _watchdogRestartsText = "—";
        private string _watchdogAutoRestartText = "확인할 수 없음";
        private string _watchdogLastHealthText = "—";
        private Brush _mainConnectionBrush = Brushes.Gray;
        private Brush _watchdogConnectionBrush = Brushes.Gray;
        private Brush _syncConnectionBrush = Brushes.Gray;
        private string _syncConnectionText = "확인할 수 없음";
        private string _syncStateText = "확인할 수 없음";
        private string _peerEndpointText = "—";
        private string _peerInstanceIdText = "—";
        private string _keyEpochText = "—";
        private string _lastSyncText = "—";
        private string _lastResultText = "확인할 수 없음";
        private string _clockSkewText = "—";
        private string _pairingIdText = "—";
        private string _pairingSasText = "—";
        private string _pairingProgressText = "—";
        private string _pairingExpiryText = "—";
        private string _peerNotificationText = "확인할 수 없음";
        private string _peerNotificationWarning;
        private AdminSyncStatus _syncStatus;
        private int? _pendingTotalCount;
        private int? _serviceTotalCount;
        private int _pendingPageNumber = 1;
        private int _servicePageNumber = 1;
        private string _pendingNextCursor;
        private string _serviceNextCursor;
        private string _pendingCursor;
        private string _serviceCursor;
        private readonly System.Collections.Generic.Stack<string>
            _pendingPreviousCursors = new System.Collections.Generic.Stack<string>();
        private readonly System.Collections.Generic.Stack<string>
            _servicePreviousCursors = new System.Collections.Generic.Stack<string>();
        private string _peerEndpointInput = string.Empty;
        private bool _rePairRequested;
        private string _logRetentionDaysText = string.Empty;
        private string _statusMessage = "메인 서비스와 와치독 연결을 확인하는 중입니다.";
        private Brush _statusMessageBrush = Brushes.DimGray;
        private ImageSource _trayIconSource = StoppedTrayIcon;
        private string _trayToolTipText = "DEEPAi Service Directory | UNKNOWN | 연결 안 됨";
        private CancellationTokenSource _pollingCancellation;
        private Task _syncPollingTask;
        private Task _listPollingTask;
        private Task _watchdogPollingTask;
        private DateTimeOffset _adminRetryNotBefore;
        private bool _automaticAdminPollingSuspended;
        private bool _disposed;

        public StatusMonitorViewModel(
            AdminApiClient adminClient,
            WatchdogPipeClient watchdogClient,
            Action openWindow,
            Action exitApplication,
            Func<string, bool> confirmDelete)
        {
            _adminClient = adminClient ?? throw new ArgumentNullException(nameof(adminClient));
            _watchdogClient = watchdogClient ?? throw new ArgumentNullException(nameof(watchdogClient));
            _confirmDelete = confirmDelete ?? throw new ArgumentNullException(nameof(confirmDelete));
            if (openWindow == null)
            {
                throw new ArgumentNullException(nameof(openWindow));
            }

            if (exitApplication == null)
            {
                throw new ArgumentNullException(nameof(exitApplication));
            }

            PendingItems = new ObservableCollection<AdminPendingItem>();
            Services = new ObservableCollection<AdminServiceItem>();

            OpenWindowCommand = new RelayCommand(openWindow);
            ExitCommand = new RelayCommand(exitApplication);
            StartServiceCommand = CreateAsyncCommand(
                StartServiceAsync,
                () => IsWatchdogConnected);
            StopServiceCommand = CreateAsyncCommand(
                StopServiceAsync,
                () => IsWatchdogConnected);
            RestartServiceCommand = CreateAsyncCommand(
                RestartServiceAsync,
                () => IsWatchdogConnected);
            RefreshCurrentPageCommand = CreateAsyncCommand(
                RefreshCurrentPageAsync,
                () => true);
            ApprovePendingCommand = CreateAsyncCommand(
                ApprovePendingAsync,
                () => IsAdminConnected && SelectedPending != null);
            RejectPendingCommand = CreateAsyncCommand(
                RejectPendingAsync,
                () => IsAdminConnected && SelectedPending != null);
            DeleteServiceCommand = CreateAsyncCommand(
                DeleteServiceAsync,
                () => IsAdminConnected && SelectedService != null);
            SyncNowCommand = CreateAsyncCommand(
                SyncNowAsync,
                () => IsAdminConnected
                    && _syncStatus != null
                    && _syncStatus.Enabled);
            EnableSyncCommand = CreateAsyncCommand(
                EnableSyncAsync,
                CanEnableSync);
            ConfirmPairingCommand = CreateAsyncCommand(
                ConfirmPairingAsync,
                () => IsAdminConnected
                    && _syncStatus != null
                    && _syncStatus.PairingId.HasValue
                    && _syncStatus.Sas != null);
            CancelPairingCommand = CreateAsyncCommand(
                CancelPairingAsync,
                () => IsAdminConnected
                    && _syncStatus != null
                    && _syncStatus.PairingId.HasValue);
            DisableSyncCommand = CreateAsyncCommand(
                DisableSyncAsync,
                () => IsAdminConnected
                    && _syncStatus != null
                    && _syncStatus.PairingState != AdminPairingState.Unpaired);
            ForgetPeerCommand = CreateAsyncCommand(
                ForgetPeerAsync,
                () => IsAdminConnected
                    && _syncStatus != null
                    && _syncStatus.PairingState != AdminPairingState.Unpaired);
            SaveLoggingCommand = CreateAsyncCommand(
                SaveLoggingAsync,
                CanSaveLogging);
            PreviousPendingPageCommand = CreateAsyncCommand(
                PreviousPendingPageAsync,
                () => _pendingPreviousCursors.Count > 0);
            NextPendingPageCommand = CreateAsyncCommand(
                NextPendingPageAsync,
                () => !string.IsNullOrEmpty(_pendingNextCursor));
            PreviousServicePageCommand = CreateAsyncCommand(
                PreviousServicePageAsync,
                () => _servicePreviousCursors.Count > 0);
            NextServicePageCommand = CreateAsyncCommand(
                NextServicePageAsync,
                () => !string.IsNullOrEmpty(_serviceNextCursor));
            _watchdogPollingTask = RunWatchdogPollingAsync(
                _lifetimeCancellation.Token);
        }

        public ObservableCollection<AdminPendingItem> PendingItems { get; }

        public ObservableCollection<AdminServiceItem> Services { get; }

        public ICommand OpenWindowCommand { get; }

        public ICommand ExitCommand { get; }

        public AsyncCommand StartServiceCommand { get; }

        public AsyncCommand StopServiceCommand { get; }

        public AsyncCommand RestartServiceCommand { get; }

        public AsyncCommand RefreshCurrentPageCommand { get; }

        public AsyncCommand ApprovePendingCommand { get; }

        public AsyncCommand RejectPendingCommand { get; }

        public AsyncCommand DeleteServiceCommand { get; }

        public AsyncCommand SyncNowCommand { get; }

        public AsyncCommand EnableSyncCommand { get; }

        public AsyncCommand ConfirmPairingCommand { get; }

        public AsyncCommand CancelPairingCommand { get; }

        public AsyncCommand DisableSyncCommand { get; }

        public AsyncCommand ForgetPeerCommand { get; }

        public AsyncCommand SaveLoggingCommand { get; }

        public AsyncCommand PreviousPendingPageCommand { get; }

        public AsyncCommand NextPendingPageCommand { get; }

        public AsyncCommand PreviousServicePageCommand { get; }

        public AsyncCommand NextServicePageCommand { get; }

        public int SelectedPageIndex
        {
            get => _selectedPageIndex;
            set
            {
                if (SetProperty(ref _selectedPageIndex, value))
                {
                    OnPropertyChanged(nameof(CurrentPageTitle));
                    RequestImmediateRefresh();
                }
            }
        }

        public string CurrentPageTitle
        {
            get
            {
                switch (SelectedPageIndex)
                {
                    case 0:
                        return "대시보드";
                    case 1:
                        return "승인 대기";
                    case 2:
                        return "등록 서비스";
                    case 3:
                        return "동기화";
                    case 4:
                        return "설정";
                    default:
                        return "상태 모니터";
                }
            }
        }

        public AdminPendingItem SelectedPending
        {
            get => _selectedPending;
            set
            {
                if (SetProperty(ref _selectedPending, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public AdminServiceItem SelectedService
        {
            get => _selectedService;
            set
            {
                if (SetProperty(ref _selectedService, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool IsAdminConnected
        {
            get => _isAdminConnected;
            private set
            {
                if (SetProperty(ref _isAdminConnected, value))
                {
                    MainServiceConnectionText = value ? "연결됨" : "연결 안 됨";
                    MainConnectionBrush = value ? Brushes.SeaGreen : Brushes.Gray;
                    RaiseCommandStates();
                }
            }
        }

        public bool IsWatchdogConnected
        {
            get => _isWatchdogConnected;
            private set
            {
                if (SetProperty(ref _isWatchdogConnected, value))
                {
                    WatchdogConnectionText = value ? "연결됨" : "연결 안 됨";
                    WatchdogConnectionBrush = value ? Brushes.SeaGreen : Brushes.Gray;
                    RaiseCommandStates();
                }
            }
        }

        public string MainServiceConnectionText
        {
            get => _mainServiceConnectionText;
            private set => SetProperty(ref _mainServiceConnectionText, value);
        }

        public string WatchdogConnectionText
        {
            get => _watchdogConnectionText;
            private set => SetProperty(ref _watchdogConnectionText, value);
        }

        public string WatchdogServiceStatusText
        {
            get => _watchdogServiceStatusText;
            private set => SetProperty(ref _watchdogServiceStatusText, value);
        }

        public string WatchdogHealthText
        {
            get => _watchdogHealthText;
            private set => SetProperty(ref _watchdogHealthText, value);
        }

        public string WatchdogFailuresText
        {
            get => _watchdogFailuresText;
            private set => SetProperty(ref _watchdogFailuresText, value);
        }

        public string WatchdogRestartsText
        {
            get => _watchdogRestartsText;
            private set => SetProperty(ref _watchdogRestartsText, value);
        }

        public string WatchdogAutoRestartText
        {
            get => _watchdogAutoRestartText;
            private set => SetProperty(ref _watchdogAutoRestartText, value);
        }

        public string WatchdogLastHealthText
        {
            get => _watchdogLastHealthText;
            private set => SetProperty(ref _watchdogLastHealthText, value);
        }

        public Brush MainConnectionBrush
        {
            get => _mainConnectionBrush;
            private set => SetProperty(ref _mainConnectionBrush, value);
        }

        public Brush WatchdogConnectionBrush
        {
            get => _watchdogConnectionBrush;
            private set => SetProperty(ref _watchdogConnectionBrush, value);
        }

        public Brush SyncConnectionBrush
        {
            get => _syncConnectionBrush;
            private set => SetProperty(ref _syncConnectionBrush, value);
        }

        public string SyncConnectionText
        {
            get => _syncConnectionText;
            private set => SetProperty(ref _syncConnectionText, value);
        }

        public string SyncStateText
        {
            get => _syncStateText;
            private set => SetProperty(ref _syncStateText, value);
        }

        public string PeerEndpointText
        {
            get => _peerEndpointText;
            private set => SetProperty(ref _peerEndpointText, value);
        }

        public string PeerInstanceIdText
        {
            get => _peerInstanceIdText;
            private set => SetProperty(ref _peerInstanceIdText, value);
        }

        public string KeyEpochText
        {
            get => _keyEpochText;
            private set => SetProperty(ref _keyEpochText, value);
        }

        public string LastSyncText
        {
            get => _lastSyncText;
            private set => SetProperty(ref _lastSyncText, value);
        }

        public string LastResultText
        {
            get => _lastResultText;
            private set => SetProperty(ref _lastResultText, value);
        }

        public string ClockSkewText
        {
            get => _clockSkewText;
            private set => SetProperty(ref _clockSkewText, value);
        }

        public string PairingIdText
        {
            get => _pairingIdText;
            private set => SetProperty(ref _pairingIdText, value);
        }

        public string PairingSasText
        {
            get => _pairingSasText;
            private set => SetProperty(ref _pairingSasText, value);
        }

        public string PairingProgressText
        {
            get => _pairingProgressText;
            private set => SetProperty(ref _pairingProgressText, value);
        }

        public string PairingExpiryText
        {
            get => _pairingExpiryText;
            private set => SetProperty(ref _pairingExpiryText, value);
        }

        public string PeerNotificationText
        {
            get => _peerNotificationText;
            private set => SetProperty(ref _peerNotificationText, value);
        }

        public string PeerNotificationWarning
        {
            get => _peerNotificationWarning;
            private set
            {
                if (SetProperty(ref _peerNotificationWarning, value))
                {
                    OnPropertyChanged(nameof(HasPeerNotificationWarning));
                }
            }
        }

        public bool HasPeerNotificationWarning =>
            !string.IsNullOrWhiteSpace(PeerNotificationWarning);

        public string PendingCountText => _pendingTotalCount.HasValue
            ? _pendingTotalCount.Value.ToString("N0", CultureInfo.CurrentCulture)
            : "—";

        public string ServiceCountText => _serviceTotalCount.HasValue
            ? _serviceTotalCount.Value.ToString("N0", CultureInfo.CurrentCulture)
            : "—";

        public string PendingPageText => "페이지 "
            + _pendingPageNumber.ToString(CultureInfo.CurrentCulture)
            + " · 전체 "
            + PendingCountText
            + "개";

        public string ServicePageText => "페이지 "
            + _servicePageNumber.ToString(CultureInfo.CurrentCulture)
            + " · 전체 "
            + ServiceCountText
            + "개";

        public string PeerEndpointInput
        {
            get => _peerEndpointInput;
            set
            {
                if (SetProperty(ref _peerEndpointInput, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool RePairRequested
        {
            get => _rePairRequested;
            set => SetProperty(ref _rePairRequested, value);
        }

        public string LogRetentionDaysText
        {
            get => _logRetentionDaysText;
            set
            {
                if (SetProperty(ref _logRetentionDaysText, value))
                {
                    SaveLoggingCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public Brush StatusMessageBrush
        {
            get => _statusMessageBrush;
            private set => SetProperty(ref _statusMessageBrush, value);
        }

        public ImageSource TrayIconSource
        {
            get => _trayIconSource;
            private set => SetProperty(ref _trayIconSource, value);
        }

        public string TrayToolTipText
        {
            get => _trayToolTipText;
            private set => SetProperty(ref _trayToolTipText, value);
        }

        public void StartPolling()
        {
            ThrowIfDisposed();
            lock (_pollingStateGate)
            {
                if (_pollingCancellation != null)
                {
                    return;
                }

                _pollingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetimeCancellation.Token);
                _syncPollingTask = RunSyncPollingAsync(_pollingCancellation.Token);
                _listPollingTask = RunListPollingAsync(_pollingCancellation.Token);
            }
        }

        public void StopPolling()
        {
            CancellationTokenSource cancellation;
            Task syncTask;
            Task listTask;
            lock (_pollingStateGate)
            {
                if (_pollingCancellation == null)
                {
                    return;
                }

                cancellation = _pollingCancellation;
                syncTask = _syncPollingTask;
                listTask = _listPollingTask;
                cancellation.Cancel();
                _pollingCancellation = null;
                _syncPollingTask = null;
                _listPollingTask = null;
            }

            ObservePollingCompletionAsync(cancellation, syncTask, listTask);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopPolling();
            _lifetimeCancellation.Cancel();
            ObserveWatchdogCompletionAsync(_watchdogPollingTask);
        }

        private AsyncCommand CreateAsyncCommand(
            Func<Task> execute,
            Func<bool> canExecute)
        {
            return new AsyncCommand(execute, canExecute, HandleUnexpectedException);
        }

        private bool CanSaveLogging()
        {
            int days;
            return IsAdminConnected
                && int.TryParse(
                    LogRetentionDaysText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out days)
                && days >= AdminApiContract.MinimumLogRetentionDays
                && days <= AdminApiContract.MaximumLogRetentionDays;
        }

        private bool CanEnableSync()
        {
            string canonicalEndpoint;
            return IsAdminConnected
                && AdminPeerEndpoint.TryNormalize(
                    PeerEndpointInput,
                    out canonicalEndpoint);
        }

        private void RaiseCommandStates()
        {
            StartServiceCommand?.RaiseCanExecuteChanged();
            StopServiceCommand?.RaiseCanExecuteChanged();
            RestartServiceCommand?.RaiseCanExecuteChanged();
            ApprovePendingCommand?.RaiseCanExecuteChanged();
            RejectPendingCommand?.RaiseCanExecuteChanged();
            DeleteServiceCommand?.RaiseCanExecuteChanged();
            SyncNowCommand?.RaiseCanExecuteChanged();
            EnableSyncCommand?.RaiseCanExecuteChanged();
            ConfirmPairingCommand?.RaiseCanExecuteChanged();
            CancelPairingCommand?.RaiseCanExecuteChanged();
            DisableSyncCommand?.RaiseCanExecuteChanged();
            ForgetPeerCommand?.RaiseCanExecuteChanged();
            SaveLoggingCommand?.RaiseCanExecuteChanged();
            PreviousPendingPageCommand?.RaiseCanExecuteChanged();
            NextPendingPageCommand?.RaiseCanExecuteChanged();
            PreviousServicePageCommand?.RaiseCanExecuteChanged();
            NextServicePageCommand?.RaiseCanExecuteChanged();
        }

        private void SetStatus(string message, bool isError)
        {
            StatusMessage = message;
            StatusMessageBrush = isError ? Brushes.Firebrick : Brushes.DimGray;
        }

        private void HandleUnexpectedException(Exception exception)
        {
            if (exception is OperationCanceledException && _disposed)
            {
                return;
            }

            SetStatus(
                "요청을 처리하는 중 예기치 않은 오류가 발생했습니다. 다시 시도하거나 진단 로그를 확인하십시오.",
                true);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(StatusMonitorViewModel));
            }
        }

        private static ImageSource LoadTrayIcon(string uriText)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(uriText, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }

        private async void ObservePollingCompletionAsync(
            CancellationTokenSource cancellation,
            Task syncTask,
            Task listTask)
        {
            try
            {
                await Task.WhenAll(syncTask, listTask);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (!_disposed)
                {
                    HandleUnexpectedException(exception);
                }
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        private async void ObserveWatchdogCompletionAsync(Task watchdogTask)
        {
            try
            {
                await watchdogTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (!_disposed)
                {
                    HandleUnexpectedException(exception);
                }
            }
            finally
            {
                _lifetimeCancellation.Dispose();
            }
        }
    }
}
