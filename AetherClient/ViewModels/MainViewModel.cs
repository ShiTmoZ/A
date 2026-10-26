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
                : "System Proxy Active (WinINET -> 127.0.0.1:1820)",
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
            AetherProcessService.KillOrphanedProcesses();
            WinInetProxyManager.SanitizeOrphanedProxySettings();
            TunEngineManager.CleanupOrphanedTunState();

            _jobManager = new JobObjectManager();
            _processService = new AetherProcessService(_jobManager);
            _proxyManager = new WinInetProxyManager(AppendLog);
            _tunManager = new TunEngineManager(_jobManager, AppendLog);

            _processService.LogReceived += AppendLog;
            _processService.TelemetryUpdated += OnTelemetryUpdated;
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
                    string? currentExitIp = ExitIp != "—" ? ExitIp : null;
                    await _tunManager.StartTunAsync(currentExitIp, _tunnelCts.Token).ConfigureAwait(false);
                }

                SetState(ConnectionState.Connected);
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
            }
        }

        private void OnTelemetryUpdated(ExitNodeTelemetry telemetry)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                ExitIp = telemetry.IpAddress;
                ExitRoute = telemetry.Route;
                ExitRtt = telemetry.Rtt;

                string color = "#38BDF8";
                string raw = telemetry.Rtt.Replace("ms", "").Trim();
                if (double.TryParse(raw, out double ms))
                {
                    if (ms < 180) color = "#10B981";       // Emerald
                    else if (ms < 350) color = "#38BDF8";  // Sky Blue
                    else if (ms < 600) color = "#F59E0B";  // Amber
                    else color = "#EF4444";               // Crimson
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
                ExitIp = "—";
                ExitRoute = "—";
                ExitRtt = "—";
                PingQualityColor = "#64748B";
            });
        }

        public void AppendLog(string message)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (DiagnosticLogs.Count >= MaxLogEntries)
                    DiagnosticLogs.RemoveAt(0);

                DiagnosticLogs.Add(message);
            });
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

            try { _tunnelCts?.Cancel(); } catch { }
            try { _proxyManager.DisableProxy(); } catch { }
            try { _tunManager.Dispose(); } catch { }
            try { _processService.Dispose(); } catch { }
            try { _jobManager.Dispose(); } catch { }
        }
    }
}
