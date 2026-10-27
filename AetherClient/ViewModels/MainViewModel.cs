using System;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
        private readonly DispatcherTimer _uptimeTimer;
        private CancellationTokenSource? _tunnelCts;
        private int _uptimeSeconds;
        private bool _disposed;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
        [NotifyPropertyChangedFor(nameof(CanChangeMode))]
        private ConnectionState _currentState = ConnectionState.Disconnected;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSystemProxyMode))]
        [NotifyPropertyChangedFor(nameof(IsTrueTunMode))]
        private TunnelMode _selectedMode = TunnelMode.SystemProxy;

        [ObservableProperty]
        private string _statusDetailText = "Not connected";

        [ObservableProperty]
        private string _peerIpValue = "—";

        [ObservableProperty]
        private string _peerIpSub = "idle";

        [ObservableProperty]
        private string _exitPopValue = "—";

        [ObservableProperty]
        private string _exitPopSub = "idle";

        [ObservableProperty]
        private string _pingValue = "—";

        [ObservableProperty]
        private string _pingSub = "idle";

        [ObservableProperty]
        private string _pingQualityColor = "#6C7A90";

        [ObservableProperty]
        private bool _isFreshScan = false;

        [ObservableProperty]
        private bool _isLogExpanded = true;

        public ObservableCollection<DiagnosticLogItem> DiagnosticLogs { get; } = new();

        public event Action<ConnectionState, TunnelMode>? StateChanged;

        public string ConnectButtonText => CurrentState switch
        {
            ConnectionState.Connected => "Connected",
            ConnectionState.Hunting   => "Connecting…",
            _                         => "Connect"
        };

        public bool CanChangeMode => CurrentState == ConnectionState.Disconnected;

        public bool IsSystemProxyMode
        {
            get => SelectedMode == TunnelMode.SystemProxy;
            set
            {
                if (value && CanChangeMode)
                {
                    SelectedMode = TunnelMode.SystemProxy;
                    UpdateStatusDetail();
                }
            }
        }

        public bool IsTrueTunMode
        {
            get => SelectedMode == TunnelMode.TrueTun;
            set
            {
                if (value && CanChangeMode)
                {
                    SelectedMode = TunnelMode.TrueTun;
                    UpdateStatusDetail();
                }
            }
        }

        public MainViewModel()
        {
            AetherProcessService.KillOrphanedProcesses();
            WinInetProxyManager.SanitizeOrphanedProxySettings();
            TunEngineManager.CleanupOrphanedTunState();

            _jobManager = new JobObjectManager();
            _processService = new AetherProcessService(_jobManager);
            _proxyManager = new WinInetProxyManager(AppendLog);
            _tunManager = new TunEngineManager(_jobManager, AppendLog);

            _processService.LogReceived += AppendLog;
            _processService.TelemetryUpdated += OnTelemetryUpdated;
            _processService.ActiveEndpointDetected += OnActiveEndpointDetected;
            _processService.ProcessExitedUnexpectedly += OnProcessExitedUnexpectedly;

            _uptimeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _uptimeTimer.Tick += OnUptimeTimerTick;

            AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYS] Aether Desktop Client initialized. Ready.");
        }

        private void OnUptimeTimerTick(object? sender, EventArgs e)
        {
            if (CurrentState != ConnectionState.Connected)
                return;

            _uptimeSeconds++;
            UpdateStatusDetail();
        }

        private void UpdateStatusDetail()
        {
            if (CurrentState == ConnectionState.Disconnected)
            {
                StatusDetailText = "Not connected";
            }
            else if (CurrentState == ConnectionState.Hunting)
            {
                StatusDetailText = "Establishing MASQUE session…";
            }
            else if (CurrentState == ConnectionState.Connected)
            {
                int mm = _uptimeSeconds / 60;
                int ss = _uptimeSeconds % 60;
                string modeStr = SelectedMode == TunnelMode.TrueTun ? "True TUN" : "System Proxy";
                StatusDetailText = $"Protected · {mm:D2}:{ss:D2} · {modeStr}";
            }
        }

        
        [RelayCommand]
        public void SwitchToSystemProxy()
        {
            if (CanChangeMode)
            {
                SelectedMode = TunnelMode.SystemProxy;
                UpdateStatusDetail();
            }
        }

        [RelayCommand]
        public void SwitchToTrueTun()
        {
            if (CanChangeMode)
            {
                SelectedMode = TunnelMode.TrueTun;
                UpdateStatusDetail();
            }
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
                _uptimeSeconds = 0;
                ResetTelemetry();
                SetState(ConnectionState.Hunting);
                UpdateStatusDetail();

                AppendLog($"[{DateTime.Now:HH:mm:ss}] [CORE] Launching Cloudflare MASQUE H2 engine...");
                bool started = await _processService.StartAsync(IsFreshScan, _tunnelCts.Token).ConfigureAwait(false);

                if (!started)
                {
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [ERR] Failed to establish MASQUE core proxy listener.");
                    await DisconnectAsync().ConfigureAwait(false);
                    return;
                }

                if (SelectedMode == TunnelMode.SystemProxy)
                {
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [PROXY] Enabling WinINET system proxy on 127.0.0.1:1820...");
                    _proxyManager.EnableProxy();
                }
                else
                {
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] [TUN] Engaging Wintun L3 interface and locking DNS to 1.1.1.1...");
                    await _tunManager.StartTunAsync(null, _tunnelCts.Token).ConfigureAwait(false);
                }

                SetState(ConnectionState.Connected);
                _uptimeTimer.Start();
                UpdateStatusDetail();
            }
            catch (OperationCanceledException)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYS] Connection attempt aborted.");
                await DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYS-ERR] Startup failure: {ex.Message}");
                await DisconnectAsync().ConfigureAwait(false);
            }
        }

        public async Task DisconnectAsync()
        {
            try
            {
                _uptimeTimer.Stop();
                _uptimeSeconds = 0;

                if (_tunnelCts is not null)
                {
                    await _tunnelCts.CancelAsync().ConfigureAwait(false);
                    _tunnelCts.Dispose();
                    _tunnelCts = null;
                }

                if (SelectedMode == TunnelMode.SystemProxy)
                {
                    _proxyManager.DisableProxy();
                }
                else
                {
                    _tunManager.StopTun();
                }

                await _processService.StopAsync().ConfigureAwait(false);
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYS] Disconnected cleanly.");
            }
            catch (Exception ex)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [SYS-WARN] Error during teardown: {ex.Message}");
            }
            finally
            {
                ResetTelemetry();
                SetState(ConnectionState.Disconnected);
                UpdateStatusDetail();
            }
        }

        private void OnActiveEndpointDetected(string endpoint)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                string ipOnly = endpoint.Split(':')[0];
                PeerIpValue = ipOnly;
                PeerIpSub = "IPv4 · UDP/443";
            });
        }

        private void OnTelemetryUpdated(ExitNodeTelemetry telemetry)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (PeerIpValue == "—" && !string.IsNullOrWhiteSpace(telemetry.IpAddress))
                {
                    PeerIpValue = telemetry.IpAddress;
                    PeerIpSub = "IPv4 · UDP/443";
                }

                // Format Exit PoP
                string route = telemetry.Route;
                if (route.Contains("FRA", StringComparison.OrdinalIgnoreCase))
                {
                    ExitPopValue = "FRA · 02";
                    ExitPopSub = "Frankfurt, DE";
                }
                else
                {
                    ExitPopValue = string.IsNullOrWhiteSpace(route) ? "FRA" : route;
                    ExitPopSub = "Cloudflare Edge";
                }

                // Format Rtt / Ping
                var match = Regex.Match(telemetry.Rtt, @"(\d+)\s*ms");
                if (match.Success)
                {
                    PingValue = match.Groups[1].Value;
                    PingSub = "jitter 2 ms";
                    if (int.TryParse(match.Groups[1].Value, out int ms))
                    {
                        if (ms < 180) PingQualityColor = "#00E676";
                        else if (ms < 350) PingQualityColor = "#38BDF8";
                        else if (ms < 600) PingQualityColor = "#F2B84B";
                        else PingQualityColor = "#EF4444";
                    }
                }
                else
                {
                    PingValue = telemetry.Rtt.Replace("ms", "").Trim();
                    PingSub = "latency";
                    PingQualityColor = "#00E676";
                }
            });

            // CRITICAL FIX: NEVER call _tunManager.AddBypassHostRoute for the Exit IP!
            // The Exit IP is Cloudflare's public egress IP, not the physical tunnel gateway!
        }

        private async void OnProcessExitedUnexpectedly(int exitCode)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] [ERR] Core engine stopped unexpectedly (Code: {exitCode}).");
            await DisconnectAsync().ConfigureAwait(false);
        }

        private void SetState(ConnectionState state)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                CurrentState = state;
                StateChanged?.Invoke(state, SelectedMode);
            });
        }

        private void ResetTelemetry()
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                PeerIpValue = "—";
                PeerIpSub = "idle";
                ExitPopValue = "—";
                ExitPopSub = "idle";
                PingValue = "—";
                PingSub = "idle";
                PingQualityColor = "#6C7A90";
            });
        }

        public void AppendLog(string rawMessage)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var match = Regex.Match(rawMessage, @"^\[(?<time>\d{2}:\d{2}:\d{2})\]\s*(?:\[(?<tag>[A-Z0-9_\-]+)\])?\s*(?<msg>.*)$");
                DiagnosticLogItem item;

                if (match.Success)
                {
                    string time = match.Groups["time"].Value;
                    string tag = match.Groups["tag"].Success ? match.Groups["tag"].Value : "SYS";
                    string msg = match.Groups["msg"].Value;

                    string color = tag.ToUpperInvariant() switch
                    {
                        "CORE" => "#8DB4F5",
                        "MASQUE" => "#B7A4F2",
                        "TUN" or "NET-TUN" => "#7FD1BE",
                        "SYS" or "UI" or "EXEC" => "#F2B84B",
                        "ERR" or "ERROR" or "CRITICAL" => "#FF6B6B",
                        _ => "#A9B6C9"
                    };

                    item = new DiagnosticLogItem
                    {
                        Timestamp = time,
                        Tag = tag,
                        TagColor = color,
                        Message = msg
                    };
                }
                else
                {
                    item = new DiagnosticLogItem
                    {
                        Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                        Tag = "LOG",
                        TagColor = "#A9B6C9",
                        Message = rawMessage
                    };
                }

                if (DiagnosticLogs.Count >= MaxLogEntries)
                    DiagnosticLogs.RemoveAt(0);

                DiagnosticLogs.Add(item);
            });
        }

        [RelayCommand]
        public void ToggleLogDrawer()
        {
            IsLogExpanded = !IsLogExpanded;
        }

        [RelayCommand]
        public void CopyLogs()
        {
            try
            {
                string text = string.Join(Environment.NewLine, DiagnosticLogs);
                Clipboard.SetText(text);
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [UI] Logs copied to clipboard.");
            }
            catch (Exception ex)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [UI-ERR] Failed to copy logs: {ex.Message}");
            }
        }

        [RelayCommand]
        public void ClearLogs()
        {
            DiagnosticLogs.Clear();
            AppendLog($"[{DateTime.Now:HH:mm:ss}] [UI] Logs cleared.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _uptimeTimer.Stop(); } catch { }
            try { _tunnelCts?.Cancel(); } catch { }
            try { _proxyManager.DisableProxy(); } catch { }
            try { _tunManager.Dispose(); } catch { }
            try { _processService.Dispose(); } catch { }
            try { _jobManager.Dispose(); } catch { }
        }
    }
}
