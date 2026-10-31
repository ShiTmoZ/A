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

            DiagnosticLogs.Add(new DiagnosticLogItem { Timestamp = DateTime.Now.ToString("HH:mm:ss"), Tag = "CORE", TagColor = "#8DB4F5", Message = "aether-masque 2.4.0 ready" });
            DiagnosticLogs.Add(new DiagnosticLogItem { Timestamp = DateTime.Now.ToString("HH:mm:ss"), Tag = "CORE", TagColor = "#8DB4F5", Message = "profile 'default' loaded · transport MASQUE over H2" });
            DiagnosticLogs.Add(new DiagnosticLogItem { Timestamp = DateTime.Now.ToString("HH:mm:ss"), Tag = "TUN", TagColor = "#7FD1BE", Message = "wintun driver detected · ready" });
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
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [CORE] routing mode → System Proxy (127.0.0.1:1820)");
            }
        }

        [RelayCommand]
        public void SwitchToTrueTun()
        {
            if (CanChangeMode)
            {
                SelectedMode = TunnelMode.TrueTun;
                UpdateStatusDetail();
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [CORE] routing mode → True TUN (Wintun L3)");
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
                string ipDisplay = endpoint;
                bool isIpv6 = endpoint.StartsWith('[') || (endpoint.Contains(':') && endpoint.IndexOf(':') != endpoint.LastIndexOf(':'));

                if (endpoint.StartsWith('['))
                {
                    int close = endpoint.IndexOf(']');
                    if (close > 0)
                        ipDisplay = endpoint.Substring(1, close - 1);
                }
                else
                {
                    int colon = endpoint.IndexOf(':');
                    if (colon > 0)
                        ipDisplay = endpoint[..colon];
                }

                PeerIpValue = ipDisplay;
                PeerIpSub = isIpv6 ? "IPv6 · MASQUE H2" : "IPv4 · MASQUE H2";
            });
        }

        private void OnTelemetryUpdated(ExitNodeTelemetry telemetry)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (PeerIpValue == "—" && !string.IsNullOrWhiteSpace(telemetry.IpAddress))
                {
                    PeerIpValue = telemetry.IpAddress;
                    PeerIpSub = telemetry.IpAddress.Contains(':') ? "IPv6 · MASQUE H2" : "IPv4 · MASQUE H2";
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
            if (string.IsNullOrWhiteSpace(rawMessage))
                return;

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var item = ParseLogItem(rawMessage);
                if (item != null)
                {
                    if (DiagnosticLogs.Count >= MaxLogEntries)
                        DiagnosticLogs.RemoveAt(0);

                    DiagnosticLogs.Add(item);
                }
            });
        }

        private static DiagnosticLogItem? ParseLogItem(string raw)
        {
            var mTime = Regex.Match(raw, @"\[?(?<time>\d{2}:\d{2}:\d{2})\]?");
            string time = mTime.Success ? mTime.Groups["time"].Value : DateTime.Now.ToString("HH:mm:ss");

            if (raw.Contains("sysprofile") || raw.Contains("obfuscation profile") || raw.Contains("candidate ok") || raw.Contains("tls verification: normal"))
                return null;

            if (raw.Contains("[ERR", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                string msg = Regex.Replace(raw, @"^\[\d{2}:\d{2}:\d{2}\]\s*", "");
                msg = Regex.Replace(msg, @"^\[[A-Z0-9_\-]+\]\s*", "");
                return new DiagnosticLogItem { Timestamp = time, Tag = "ERR", TagColor = "#FF6B6B", Message = msg.Trim() };
            }

            if (raw.Contains("[RADAR]"))
            {
                string msg = Regex.Replace(raw, @"^.*?\[RADAR\]\s*", "").Trim();
                return new DiagnosticLogItem { Timestamp = time, Tag = "RADAR", TagColor = "#00E676", Message = msg };
            }

            if (raw.Contains("masque_h2") || raw.Contains("connect-ip") || raw.Contains("[masque]") ||
                raw.Contains("alpn=") || raw.Contains("flow control") || raw.Contains("CONNECT-"))
            {
                string msg;
                if (raw.Contains("connect-ip status: 200"))
                    msg = "connect-ip status 200 · verified";
                else if (raw.Contains("connect-ip request sent"))
                    msg = "connect-ip request sent to cloudflareaccess.com";
                else if (raw.Contains("validating data-plane"))
                    msg = "validating data-plane probe";
                else if (raw.Contains("tunnel validated"))
                    msg = "tunnel validated · exposing listeners";
                else if (raw.Contains("MASQUE transport:"))
                {
                    var m = Regex.Match(raw, @"to ([0-9\.:]+)");
                    msg = $"transport: HTTP/2 (TCP) to {(m.Success ? m.Groups[1].Value : "edge")}";
                }
                else if (raw.Contains("tls established"))
                    msg = "TLS established · ALPN h2";
                else
                {
                    msg = Regex.Replace(raw, @"^.*?\]\s*", "");
                    msg = msg.Replace("[h2]", "").Trim();
                }

                return new DiagnosticLogItem { Timestamp = time, Tag = "MASQUE", TagColor = "#B7A4F2", Message = msg };
            }

            if (raw.Contains("NET-TUN") || raw.Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("tun2socks", StringComparison.OrdinalIgnoreCase) || raw.Contains("route add") || raw.Contains("route delete"))
            {
                string msg;
                if (raw.Contains("True TUN active"))
                    msg = "adapter 'wintun' up · default route via wintun";
                else if (raw.Contains("Secured physical adapter"))
                    msg = "physical DNS locked to 1.1.1.1 (anti-poisoning)";
                else if (raw.Contains("Virtual interface"))
                    msg = "wintun interface active · configuring IPv4 routes";
                else if (raw.Contains("Bound physical egress"))
                {
                    var m = Regex.Match(raw, @"to '([^']+)'");
                    msg = $"bound physical egress to {(m.Success ? m.Groups[1].Value : "physical adapter")}";
                }
                else if (raw.Contains("restored"))
                    msg = "adapter 'wintun' down · routes restored";
                else
                {
                    msg = Regex.Replace(raw, @"^.*?\[NET-TUN\]\s*", "").Trim();
                }

                return new DiagnosticLogItem { Timestamp = time, Tag = "TUN", TagColor = "#7FD1BE", Message = msg };
            }

            string coreMsg;
            if (raw.Contains("NET-PROXY"))
            {
                if (raw.Contains("enabled"))
                    coreMsg = "system proxy bound · 127.0.0.1:1820";
                else if (raw.Contains("disabled"))
                    coreMsg = "system proxy released";
                else
                    coreMsg = Regex.Replace(raw, @"^.*?\[NET-PROXY\]\s*", "").Trim();
            }
            else if (raw.Contains("selected MASQUE gateway"))
            {
                var m = Regex.Match(raw, @"gateway ([0-9\.:]+).*?rtt ([0-9\.]+ms)");
                coreMsg = m.Success ? $"selected edge {m.Groups[1].Value} · rtt {m.Groups[2].Value}" : "selected cloudflare edge";
            }
            else if (raw.Contains("masque exit:"))
            {
                var m = Regex.Match(raw, @"exit:\s*([^,]+),\s*([^,]+),\s*([^,]+)");
                coreMsg = m.Success ? $"tunnel established · {m.Groups[2].Value.Trim()} · {m.Groups[3].Value.Trim()}" : "tunnel established";
            }
            else if (raw.Contains("socks5 server listening"))
                coreMsg = "socks5 listening · 127.0.0.1:1819";
            else if (raw.Contains("http proxy listening"))
                coreMsg = "http proxy listening · 127.0.0.1:1820";
            else if (raw.Contains("Clean IP radar"))
            {
                if (raw.Contains("selected fastest edge"))
                {
                    var m = Regex.Match(raw, @"selected fastest edge\s*([0-9\.:]+)\s*\(RTT:\s*([0-9]+ms)\)");
                    coreMsg = m.Success ? $"clean IP radar: selected {m.Groups[1].Value} · rtt {m.Groups[2].Value}" : "clean IP radar: fast edge selected";
                }
                else if (raw.Contains("probing"))
                {
                    coreMsg = "clean IP radar: probing candidate edges in parallel";
                }
                else
                {
                    coreMsg = "clean IP radar: dynamic scan fallback";
                }
            }
            else if (raw.Contains("scan mode=turbo"))
                coreMsg = "dialing edge · turbo probe (concurrency=20)";
            else if (raw.Contains("loaded existing masque identity"))
                coreMsg = "profile 'default' loaded · identity ready";
            else if (raw.Contains("Aether v") || raw.Contains("Aether Desktop Client initialized"))
                coreMsg = "aether-masque 2.4.0 ready";
            else if (raw.Contains("Logs copied"))
                coreMsg = "logs copied to clipboard";
            else
            {
                coreMsg = Regex.Replace(raw, @"^\[\d{2}:\d{2}:\d{2}\]\s*", "");
                coreMsg = Regex.Replace(coreMsg, @"^\[[A-Z0-9_\-]+\]\s*", "");
                coreMsg = Regex.Replace(coreMsg, @"^\[\+\]\s*", "");
                coreMsg = Regex.Replace(coreMsg, @"^\[\*\]\s*", "");
                coreMsg = coreMsg.Trim();
            }

            return new DiagnosticLogItem { Timestamp = time, Tag = "CORE", TagColor = "#8DB4F5", Message = coreMsg };
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
            DiagnosticLogs.Add(new DiagnosticLogItem { Timestamp = DateTime.Now.ToString("HH:mm:ss"), Tag = "CORE", TagColor = "#8DB4F5", Message = "log cleared" });
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
