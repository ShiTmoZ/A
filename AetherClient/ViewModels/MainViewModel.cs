using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AetherClient.Core;
using AetherClient.Models;
using AetherClient.Network;

namespace AetherClient.ViewModels
{
    public sealed partial class MainViewModel : ObservableObject, IDisposable
    {
        private const int MaxLogEntries = 800;

        private readonly JobObjectManager _jobManager;
        private readonly AetherProcessService _processService;
        private readonly WinInetProxyManager _proxyManager;
        private readonly TunEngineManager _tunManager;
        private CancellationTokenSource? _tunnelCts;
        private bool _disposed;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateBadgeText))]
        [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
        [NotifyPropertyChangedFor(nameof(ConnectButtonSubText))]
        [NotifyPropertyChangedFor(nameof(CanChangeMode))]
        private ConnectionState _currentState = ConnectionState.Disconnected;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSystemProxyMode))]
        [NotifyPropertyChangedFor(nameof(IsTrueTunMode))]
        private TunnelMode _selectedMode = TunnelMode.SystemProxy;

        [ObservableProperty]
        private string _exitIp = "—";

        [ObservableProperty]
        private string _exitRoute = "—";

        [ObservableProperty]
        private string _exitRtt = "—";

        [ObservableProperty]
        private string _pingQualityColor = "#64748B";

        [ObservableProperty]
        private bool _isFreshScan = false;

        [ObservableProperty]
        private bool _isLogExpanded = false;

        public ObservableCollection<string> DiagnosticLogs { get; } = new();

        public event Action<ConnectionState, TunnelMode>? StateChanged;

        public string StateBadgeText => CurrentState switch
        {
            ConnectionState.Connected => "CONNECTED",
            ConnectionState.Hunting   => "HUNTING",
            _                         => "DISCONNECTED"
        };

        public string ConnectButtonText => CurrentState switch
        {
            ConnectionState.Connected => "DISCONNECT",
            ConnectionState.Hunting   => "ABORT HUNT",
            _                         => "CONNECT"
        };

        public string ConnectButtonSubText => CurrentState switch
        {
            ConnectionState.Connected => SelectedMode == TunnelMode.TrueTun
                ? "Full TUN Active (0.0.0.0/0 -> 127.0.0.1:1819)"
                : "System Proxy Active (WinINET -> 127.0.0.1:1819)",
            ConnectionState.Hunting   => IsFreshScan
                ? "Hunting new edge gateway (Deep Scan)..."
                : "Verifying cached gateway & hunting...",
            _                         => IsFreshScan
                ? "Click to start deep gateway hunt"
                : "Click for instant quick reconnect"
        };

        public bool CanChangeMode => CurrentState == ConnectionState.Disconnected;

        public bool IsSystemProxyMode
        {
            get => SelectedMode == TunnelMode.SystemProxy;
            set
            {
                if (value && CanChangeMode)
                    SelectedMode = TunnelMode.SystemProxy;
            }
        }

        public bool IsTrueTunMode
        {
            get => SelectedMode == TunnelMode.TrueTun;
            set
            {
                if (value && CanChangeMode)
                    SelectedMode = TunnelMode.TrueTun;
            }
        }

        public MainViewModel()
        {
            // Sanitize any leftover OS state from prior forced power-offs
            AetherProcessService.KillOrphanedProcesses();
            WinInetProxyManager.SanitizeOrphanedProxySettings();
            TunEngineManager.CleanupOrphanedTunState();

            _jobManager = new JobObjectManager();
            _processService = new AetherProcessService(_jobManager);
            _proxyManager = new WinInetProxyManager(AppendLog);
            _tunManager = new TunEngineManager(_jobManager, AppendLog);

            _processService.LogReceived += AppendLog;
            _processService.TelemetryUpdated += OnTelemetryUpdated;
            _processService.SocksServerReady += OnSocksServerReady;
            _processService.ProcessExitedUnexpectedly += OnProcessExitedUnexpectedly;

            AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYS] Aether Desktop Client initialized. Ready.");
        }

        [RelayCommand]
        public async Task ToggleConnectionAsync()
        {
            if (CurrentState != ConnectionState.Disconnected)
            {
                await DisconnectAsync().ConfigureAwait(false);
                return;
            }

            await ConnectAsync().ConfigureAwait(false);
        }

        private async Task ConnectAsync()
        {
            try
            {
                _tunnelCts = new CancellationTokenSource();
                ResetTelemetry();
                SetState(ConnectionState.Hunting);

                await _processService.StartAsync(IsFreshScan).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYS-ERR] Startup failure: {ex.Message}");
                await DisconnectAsync().ConfigureAwait(false);
            }
        }

        private async void OnSocksServerReady()
        {
            if (CurrentState == ConnectionState.Connected)
                return;

            try
            {
                CancellationToken token = _tunnelCts?.Token ?? CancellationToken.None;

                if (SelectedMode == TunnelMode.SystemProxy)
                {
                    _proxyManager.EnableProxy();
                }
                else
                {
                    string? currentExitIp = ExitIp != "—" ? ExitIp : null;
                    await _tunManager.StartTunAsync(currentExitIp, token).ConfigureAwait(false);
                }

                SetState(ConnectionState.Connected);
            }
            catch (OperationCanceledException)
            {
                // Canceled by user during initialization
            }
            catch (Exception ex)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [NET-ERR] Tunnel routing failed: {ex.Message}");
                await DisconnectAsync().ConfigureAwait(false);
            }
        }

        private void OnTelemetryUpdated(ExitNodeTelemetry telemetry)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                ExitIp = telemetry.IpAddress;
                ExitRoute = telemetry.Route;
                ExitRtt = telemetry.Rtt;

                // Color code latency
                string color = "#38BDF8";
                string raw = telemetry.Rtt.Replace("ms", "").Trim();
                if (double.TryParse(raw, out double ms))
                {
                    if (ms < 180) color = "#10B981"; // Emerald
                    else if (ms < 350) color = "#38BDF8"; // Sky Blue
                    else if (ms < 600) color = "#F59E0B"; // Amber
                    else color = "#EF4444"; // Crimson
                }
                PingQualityColor = color;
            });

            if (SelectedMode == TunnelMode.TrueTun)
            {
                _tunManager.AddBypassHostRoute(telemetry.IpAddress);
            }
        }

        private async void OnProcessExitedUnexpectedly(int exitCode)
        {
            await DisconnectAsync().ConfigureAwait(false);
        }

        public Task DisconnectAsync()
        {
            try { _tunnelCts?.Cancel(); } catch { }
            try { _tunnelCts?.Dispose(); } catch { }
            _tunnelCts = null;

            _tunManager.StopTun();
            _proxyManager.DisableProxy();
            _processService.Stop();

            ResetTelemetry();
            SetState(ConnectionState.Disconnected);
            return Task.CompletedTask;
        }

        [RelayCommand]
        private void ClearLogs()
        {
            DiagnosticLogs.Clear();
        }

        [RelayCommand]
        private void CopyLogs()
        {
            if (DiagnosticLogs.Count == 0) return;
            string joined = string.Join(Environment.NewLine, DiagnosticLogs);
            Clipboard.SetText(joined);
            AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYS] Copied {DiagnosticLogs.Count} log lines to clipboard.");
        }

        private void ResetTelemetry()
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                ExitIp = "—";
                ExitRoute = "—";
                ExitRtt = "—";
                PingQualityColor = "#64748B";
            });
        }

        private void SetState(ConnectionState newState)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                CurrentState = newState;
                OnPropertyChanged(nameof(ConnectButtonSubText));
                StateChanged?.Invoke(CurrentState, SelectedMode);
            });
        }

        private void AppendLog(string line)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
                return;

            dispatcher.InvokeAsync(() =>
            {
                if (DiagnosticLogs.Count >= MaxLogEntries)
                    DiagnosticLogs.RemoveAt(0);

                DiagnosticLogs.Add(line);
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _tunnelCts?.Cancel(); } catch { }
            _tunManager.Dispose();
            _proxyManager.DisableProxy();
            _processService.Dispose();
            _jobManager.Dispose();
        }
    }
}
