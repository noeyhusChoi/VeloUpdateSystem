using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Diagnostics;
using Velopack;

namespace VeloUpdateSystem
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, System.ComponentModel.INotifyPropertyChanged
    {
        public string AppVersion { get; } =
            $"v{AppVersionProvider.GetVersion()}";

        private readonly DispatcherTimer _heartbeatTimer;
        private readonly DispatcherTimer _statusTimer;
        private readonly DispatcherTimer _applyTimer;
        private readonly AppSettings _settings;
        private readonly WatchdogIpcClient _watchdogClient;
        private readonly UpdateManager _updateManager;
        private readonly SemaphoreSlim _updateGate = new(1, 1);
        private DateTimeOffset _lastInputUtc = DateTimeOffset.UtcNow;
        private string _watchdogStatus = "Watchdog: unknown";
        private string _updateStatus = "Update: idle";
        private string _currentVersionDisplay = "Current: unknown";
        private string _availableVersionDisplay = "Available: n/a";
        private string _downloadStatus = "Download: idle";
        private UpdateInfo? _pendingUpdate;
        private bool _updateDownloaded;
        private bool _applyRequested;
        private bool _updateInProgress;
        private readonly CancellationTokenSource _updateLoopCts = new();

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string WatchdogStatus
        {
            get => _watchdogStatus;
            private set
            {
                if (_watchdogStatus != value)
                {
                    _watchdogStatus = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(WatchdogStatus)));
                }
            }
        }

        public string UpdateStatus
        {
            get => _updateStatus;
            private set
            {
                if (_updateStatus != value)
                {
                    _updateStatus = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(UpdateStatus)));
                }
            }
        }

        public string CurrentVersionDisplay
        {
            get => _currentVersionDisplay;
            private set
            {
                if (_currentVersionDisplay != value)
                {
                    _currentVersionDisplay = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CurrentVersionDisplay)));
                }
            }
        }

        public string AvailableVersionDisplay
        {
            get => _availableVersionDisplay;
            private set
            {
                if (_availableVersionDisplay != value)
                {
                    _availableVersionDisplay = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AvailableVersionDisplay)));
                }
            }
        }

        public string DownloadStatus
        {
            get => _downloadStatus;
            private set
            {
                if (_downloadStatus != value)
                {
                    _downloadStatus = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DownloadStatus)));
                }
            }
        }

        public MainWindow()
        {
            DataContext = this;
            InitializeComponent();
            HookInputTracking();

            _settings = AppSettings.Load();
            _watchdogClient = new WatchdogIpcClient(_settings.WatchdogBaseUri);
            _updateManager = new UpdateManager(
                _settings.GetUpdateUrl(),
                new UpdateOptions { ExplicitChannel = _settings.Channel });
            CurrentVersionDisplay = $"Current: {_updateManager.CurrentVersion?.ToString() ?? AppVersionProvider.GetVersion()}";

            _heartbeatTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _heartbeatTimer.Tick += async (_, _) => await SendHeartbeatAsync();
            _heartbeatTimer.Start();

            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _statusTimer.Tick += async (_, _) => await UpdateStatusAsync();
            _statusTimer.Start();

            _applyTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _applyTimer.Tick += async (_, _) => await TryApplyUpdateAsync("idleCheck");
            _applyTimer.Start();

            _ = Task.Run(() => RunUpdateLoopAsync(_updateLoopCts.Token));
        }

        private void HookInputTracking()
        {
            PreviewMouseDown += (_, _) => _lastInputUtc = DateTimeOffset.UtcNow;
            PreviewMouseMove += (_, _) => _lastInputUtc = DateTimeOffset.UtcNow;
            PreviewMouseWheel += (_, _) => _lastInputUtc = DateTimeOffset.UtcNow;
            PreviewKeyDown += (_, _) => _lastInputUtc = DateTimeOffset.UtcNow;
            PreviewTextInput += (_, _) => _lastInputUtc = DateTimeOffset.UtcNow;
            TouchDown += (_, _) => _lastInputUtc = DateTimeOffset.UtcNow;
        }

        private async Task SendHeartbeatAsync()
        {
            try
            {
                var idleSeconds = (int)Math.Floor((DateTimeOffset.UtcNow - _lastInputUtc).TotalSeconds);
                await _watchdogClient.SendHeartbeatAsync(
                    Environment.ProcessId,
                    responsive: true,
                    idleSeconds: Math.Max(0, idleSeconds),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Heartbeat send failed: {ex}");
            }
        }

        private async Task UpdateStatusAsync()
        {
            try
            {
                var payload = await _watchdogClient.GetStatusAsync(CancellationToken.None);
                if (payload is null)
                {
                    WatchdogStatus = "Watchdog: offline";
                    return;
                }

                var running = TryGetString(payload.Value, "appRunning");
                var suppressed = TryGetString(payload.Value, "updateSuppressed");
                WatchdogStatus = $"Watchdog: appRunning={running ?? "n/a"}, updateSuppressed={suppressed ?? "n/a"}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Status request failed: {ex}");
                WatchdogStatus = "Watchdog: offline";
            }
        }

        private static string? TryGetString(System.Text.Json.JsonElement payload, string name)
        {
            if (!payload.TryGetProperty(name, out var element))
            {
                return null;
            }

            return element.ValueKind == System.Text.Json.JsonValueKind.String
                ? element.GetString()
                : element.ToString();
        }

        private async Task RunUpdateLoopAsync(CancellationToken cancellationToken)
        {
            await CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);

            var interval = TimeSpan.FromMinutes(_settings.PollIntervalMinutes);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                    await CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }

        private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SetUpdateStatus("Update: checking");
                var updateInfo = await _updateManager.CheckForUpdatesAsync().ConfigureAwait(false);
                if (updateInfo is null)
                {
                    _pendingUpdate = null;
                    _updateDownloaded = false;
                    SetUpdateStatus("Update: idle");
                    SetAvailableVersion("Available: n/a");
                    SetDownloadStatus("Download: idle");
                    return;
                }

                _pendingUpdate = updateInfo;
                SetAvailableVersion($"Available: {updateInfo.TargetFullRelease?.Version}");
                SetUpdateStatus($"Update: downloading {updateInfo.TargetFullRelease?.Version}");
                SetDownloadStatus("Download: in progress");
                await _updateManager.DownloadUpdatesAsync(updateInfo, progress =>
                {
                    SetUpdateStatus($"Update: downloading {progress}%");
                }, cancellationToken).ConfigureAwait(false);

                _updateDownloaded = true;
                SetDownloadStatus("Download: complete");
                SetUpdateStatus($"Update: ready {updateInfo.TargetFullRelease?.Version}");
            }
            catch (Velopack.Exceptions.NotInstalledException)
            {
                _pendingUpdate = null;
                _updateDownloaded = false;
                SetUpdateStatus("Update: not installed");
                SetAvailableVersion("Available: n/a");
                SetDownloadStatus("Download: idle");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Update check failed: {ex}");
                SetUpdateStatus("Update: error");
                SetDownloadStatus("Download: error");
            }
            finally
            {
                _updateGate.Release();
            }

            await TryApplyUpdateAsync("downloadComplete").ConfigureAwait(false);
        }

        private async Task TryApplyUpdateAsync(string reason)
        {
            if (_updateInProgress || !_updateDownloaded || _pendingUpdate is null)
            {
                return;
            }

            var idleRequired = TimeSpan.FromSeconds(_settings.IdleSecondsBeforeApply);
            if (!_applyRequested && !IsIdleFor(idleRequired))
            {
                SetUpdateStatus($"Update: waiting for idle {idleRequired.TotalSeconds:0}s");
                return;
            }

            _applyRequested = false;
            _updateInProgress = true;
            SetUpdateStatus($"Update: applying ({reason})");

            try
            {
                await _watchdogClient.SetUpdateModeAsync(true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Watchdog update start failed: {ex}");
            }

            _updateManager.ApplyUpdatesAndRestart(_pendingUpdate, Array.Empty<string>());
        }

        private bool IsIdleFor(TimeSpan duration)
        {
            return DateTimeOffset.UtcNow - _lastInputUtc >= duration;
        }

        private void SetUpdateStatus(string value)
        {
            if (Dispatcher.CheckAccess())
            {
                UpdateStatus = value;
                return;
            }

            Dispatcher.Invoke(() => UpdateStatus = value);
        }

        private void SetAvailableVersion(string value)
        {
            if (Dispatcher.CheckAccess())
            {
                AvailableVersionDisplay = value;
                return;
            }

            Dispatcher.Invoke(() => AvailableVersionDisplay = value);
        }

        private void SetDownloadStatus(string value)
        {
            if (Dispatcher.CheckAccess())
            {
                DownloadStatus = value;
                return;
            }

            Dispatcher.Invoke(() => DownloadStatus = value);
        }

        private async void OnIdleApplyClick(object sender, RoutedEventArgs e)
        {
            _applyRequested = true;
            await TryApplyUpdateAsync("manual").ConfigureAwait(false);
        }

        protected override void OnClosed(EventArgs e)
        {
            _updateLoopCts.Cancel();
            base.OnClosed(e);
        }
    }
}
