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
        private DateTimeOffset _lastInputUtc = DateTimeOffset.UtcNow;
        private string _agentStatus = "Agent: unknown";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string AgentStatus
        {
            get => _agentStatus;
            private set
            {
                if (_agentStatus != value)
                {
                    _agentStatus = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AgentStatus)));
                }
            }
        }

        public MainWindow()
        {
            DataContext = this;
            InitializeComponent();
            HookInputTracking();

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
                var idleMinutes = (int)Math.Floor((DateTimeOffset.UtcNow - _lastInputUtc).TotalMinutes);
                await AgentIpcClient.SendHeartbeatAsync(
                    Environment.ProcessId,
                    responsive: true,
                    idleMinutes: Math.Max(0, idleMinutes),
                    CancellationToken.None);
            }
            catch
            {
                // Agent may not be running.
            }
        }

        private async Task UpdateStatusAsync()
        {
            try
            {
                var response = await AgentIpcClient.GetStatusAsync(CancellationToken.None);
                if (response is null)
                {
                    AgentStatus = "Agent: offline";
                    return;
                }

                var payload = response.Payload;
                var state = TryGetString(payload, "state") ?? "unknown";
                var current = TryGetString(payload, "currentVersion");
                var available = TryGetString(payload, "availableVersion");
                var channel = TryGetString(payload, "channel");

                AgentStatus = $"Agent: {state}, channel={channel ?? "n/a"}, current={current ?? "n/a"}, available={available ?? "n/a"}";
            }
            catch
            {
                AgentStatus = "Agent: offline";
            }
        }

        private static string? TryGetString(System.Text.Json.JsonElement payload, string name)
        {
            return payload.TryGetProperty(name, out var element) ? element.GetString() : null;
        }
    }
}
