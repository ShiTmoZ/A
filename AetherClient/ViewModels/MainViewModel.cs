using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AetherClient.Core;
using AetherClient.Models;
using AetherClient.Network;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AetherClient.ViewModels
{
    public enum TunnelConnectionState
    {
        Idle,
        Hunting,
        Connected
    }

    public sealed class TelemetryLogEntry
    {
        public string Timestamp { get; init; } = string.Empty;
        public string Tag { get; init; } = "[INFO]";
        public string Message { get; init; } = string.Empty;
        public Brush TagBrush { get; init; } = Brushes.Cyan;
        public Brush MessageBrush { get; init; } = Brushes.WhiteSmoke;
    }

    public sealed partial class MainViewModel : ObservableObject, IDisposable
    {
        private const int MaxLogEntries = 600;

        // Frozen Brushes for High-Performance UI rendering
        private static readonly Brush TealTagBrush = CreateFrozenBrush("#22B8CF");
        private static readonly Brush GoldTagBrush = CreateFrozenBrush("#FFD166");
        private static readonly Brush EmeraldTagBrush = CreateFrozenBrush("#4ADE80");
        private static readonly Brush CrimsonTagBrush = CreateFrozenBrush("#FF5C6A");
        private static readonly Brush CyanTagBrush = CreateFrozenBrush("#38BDF8");
        private static readonly Brush PrimaryTextBrush = CreateFrozenBrush("#F1F5F9");
        private static readonly Brush SecondaryTextBrush = CreateFrozenBrush("#CBD5E1");

        private readonly JobObjectManager _jobManager;
        private readonly AetherProcessService _processService;
        private readonly WinInetProxyManager _proxyManager;
        private readonly TunEngineManager _tunManager;
        private readonly DispatcherTimer _uptimeTimer;

        private CancellationTokenSource? _connectionCts;
        private DateTime _connectedSinceUtc;
        private string? _detectedPeerIp;
        private bool _isDisposed;

        public ObservableCollection<TelemetryLogEntry> Logs { get; } = new();

        public event Action<ConnectionState, TunnelMode>? StateChanged;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIdle))]
        [NotifyPropertyChangedFor(nameof(IsHunting))]
        [NotifyPropertyChangedFor(nameof(IsConnected))]
        [NotifyPropertyChangedFor(nameof(StatusBadgeText))]
        [NotifyPropertyChangedFor(nameof(PrimaryActionButtonText))]
        [NotifyPropertyChangedFor(nameof(CompanionStatusTitle))]
        [NotifyPropertyChangedFor(nameof(CompanionAuraDescription))]
        private TunnelConnectionState _currentState = TunnelConnectionState.Idle;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSystemProxySelected))]
        [NotifyPropertyChangedFor(nameof(ActiveRoutingDescription))]
        private bool _isTunModeSelected = true;

        [ObservableProperty]
        private string _companionSubtitle = "ELVEN MANA & HEARTH // MASQUE EDITION";

        [ObservableProperty]
        private string _gatewayPeer = "162.159.198.4:443";

        [ObservableProperty]
        private string _routeDatacenter = "FRA (Frankfurt Core)";

        [ObservableProperty]
        private string _latencyRtt = "175 ms";

        [ObservableProperty]
        private string _localListeners = "127.0.0.1:1819 (SOCKS5) | :1820 (HTTP)";

        [ObservableProperty]
        private string _lastExecutedCommand = "aether.exe --masque --h2 -4 --turbo --quick-reconnect --http-proxy 127.0.0.1:1820";

        [ObservableProperty]
        private string _logCountBadgeText = "0 events";

        [ObservableProperty]
        private string _copyButtonText = "Copy Logs";

        [ObservableProperty]
        private bool _isCopyFeedbackActive;

        public bool IsIdle => CurrentState == TunnelConnectionState.Idle;
        public bool IsHunting => CurrentState == TunnelConnectionState.Hunting;
        public bool IsConnected => CurrentState == TunnelConnectionState.Connected;

        public bool IsSystemProxySelected
        {
            get => !IsTunModeSelected;
            set => IsTunModeSelected = !value;
        }

        public string ActiveRoutingDescription => IsTunModeSelected
            ? "Full-tunnel Wintun kernel driver active. Physical DNS locked to 1.1.1.1 (Anti-DPI)."
            : "WinINET system HTTP/HTTPS proxy hooked to 127.0.0.1:1820 (Bypasses local loopback).";

        public string StatusBadgeText => CurrentState switch
        {
            TunnelConnectionState.Idle => "STANDBY",
            TunnelConnectionState.Hunting => "HUNTING",
            TunnelConnectionState.Connected => "CONNECTED",
            _ => "STANDBY"
        };

        public string PrimaryActionButtonText => CurrentState switch
        {
            TunnelConnectionState.Idle => "ENGAGE MASQUE LINK",
            TunnelConnectionState.Hunting => "CANCEL HUNT",
            TunnelConnectionState.Connected => "DISCONNECT TUNNEL",
            _ => "ENGAGE MASQUE LINK"
        };

        public string CompanionStatusTitle => CurrentState switch
        {
            TunnelConnectionState.Idle => "Hearth Warmth Restored",
            TunnelConnectionState.Hunting => "Hunting Anycast Mesh...",
            TunnelConnectionState.Connected => "Mana Shield Synchronized",
            _ => "Standby"
        };

        public string CompanionAuraDescription => CurrentState switch
        {
            TunnelConnectionState.Idle => "Aura breathing softly in standby mode. Ready to weave MASQUE HTTP/2 tunnel.",
            TunnelConnectionState.Hunting => "Probing Cloudflare Anycast edge mesh via parallel HTTP/2 CONNECT-IP.",
            TunnelConnectionState.Connected => "Secure zero-leak L3 tunnel locked. Traffic fully insulated against DPI filters.",
            _ => "Aura resting."
        };

        private TunnelMode SelectedMode => IsTunModeSelected ? TunnelMode.TrueTun : TunnelMode.SystemProxy;

        public MainViewModel()
        {
            // Sanitize any orphaned OS state from prior unexpected exits
            AetherProcessService.KillOrphanedProcesses();
            WinInetProxyManager.SanitizeOrphanedProxySettings();
            TunEngineManager.CleanupOrphanedTunState();

            _jobManager = new JobObjectManager();
            _processService = new AetherProcessService(_jobManager);
            _proxyManager = new WinInetProxyManager(AppendRawLog);
            _tunManager = new TunEngineManager(_jobManager, AppendRawLog);

            _processService.LogReceived += HandleAetherLog;
            _processService.TunnelStateChanged += HandleProcessTunnelStateChanged;
            _processService.ActiveEndpointDetected += HandleActiveEndpointDetected;
            _processService.TelemetryUpdated += HandleTelemetryUpdated;

            _uptimeTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _uptimeTimer.Tick += (_, _) =>
            {
                if (CurrentState == TunnelConnectionState.Connected)
                {
                    TimeSpan elapsed = DateTime.UtcNow - _connectedSinceUtc;
                    CompanionSubtitle = $"ACTIVE LINK // {elapsed:hh\\:mm\\:ss} // {RouteDatacenter}";
                }
            };

            AppendLog("[CORE]", "Aether MASQUE Core v2.4 initialized. Ready for turbo mesh hunt.", TealTagBrush);
            AppendLog("[THEME]", "Marcille Donato (Elven Mana & Hearth Edition) visual profile loaded.", GoldTagBrush);
            AppendLog("[CONFIG]", "Default arguments: --masque --h2 -4 --turbo --quick-reconnect --http-proxy 127.0.0.1:1820", CyanTagBrush);
        }

        private static Brush CreateFrozenBrush(string hexColor)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
            brush.Freeze();
            return brush;
        }

        [RelayCommand]
        public async Task ToggleTunnel()
        {
            if (CurrentState == TunnelConnectionState.Connected || CurrentState == TunnelConnectionState.Hunting)
            {
                await DisconnectInternalAsync().ConfigureAwait(false);
            }
            else
            {
                await ConnectInternalAsync(forceFreshScan: false).ConfigureAwait(false);
            }
        }

        public Task ToggleConnectionAsync() => ToggleTunnel();

        [RelayCommand]
        public async Task HuntNextEdge()
        {
            AppendLog("[HUNT]", "Initiating forced edge re-scan across Anycast pool (--no-quick-reconnect)...", GoldTagBrush);
            await DisconnectInternalAsync().ConfigureAwait(false);
            await ConnectInternalAsync(forceFreshScan: true).ConfigureAwait(false);
        }

        [RelayCommand]
        public async Task CopyLogsAsync()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var log in Logs)
                {
                    sb.AppendLine($"{log.Timestamp} {log.Tag} {log.Message}");
                }

                Clipboard.SetText(sb.ToString());

                CopyButtonText = "Copied!";
                IsCopyFeedbackActive = true;
                await Task.Delay(1800);
            }
            catch
            {
                // Ignore clipboard failures
            }
            finally
            {
                CopyButtonText = "Copy Logs";
                IsCopyFeedbackActive = false;
            }
        }

        [RelayCommand]
        public void ClearLogs()
        {
            Logs.Clear();
            AppendLog("[CLEAR]", "Telemetry console cleared by operator.", GoldTagBrush);
            LogCountBadgeText = "1 event";
        }

        private async Task ConnectInternalAsync(bool forceFreshScan)
        {
            _connectionCts?.Cancel();
            _connectionCts = new CancellationTokenSource();

            string reconnectFlag = forceFreshScan ? "--no-quick-reconnect" : "--quick-reconnect";
            LastExecutedCommand = $"aether.exe --masque --h2 -4 --turbo {reconnectFlag} --http-proxy 127.0.0.1:1820";

            await RunOnUiThreadAsync(() =>
            {
                CurrentState = TunnelConnectionState.Hunting;
                StateChanged?.Invoke(ConnectionState.Hunting, SelectedMode);
                CompanionSubtitle = "PROBING ANYCAST MESH // CONNECT-IP RFC 9298";
            });

            AppendLog("[HUNT]", $"Engaging Anycast Turbo Hunter ({reconnectFlag})...", GoldTagBrush);

            bool started = await _processService.StartAsync(
                forceFreshScan: forceFreshScan,
                customPeer: null, // Always rely on Aether's proven internal hunter!
                useForeignExit: false,
                cancellationToken: _connectionCts.Token).ConfigureAwait(false);

            if (!started)
            {
                await RunOnUiThreadAsync(() =>
                {
                    CurrentState = TunnelConnectionState.Idle;
                    StateChanged?.Invoke(ConnectionState.Disconnected, SelectedMode);
                    CompanionSubtitle = "CONNECTION FAILED // RETRY HUNT";
                });
                AppendLog("[FAIL]", "Could not initiate Aether process or proxy sockets timed out.", CrimsonTagBrush);
                return;
            }

            // Engage system routing layer (Wintun kernel TUN vs WinINET proxy)
            try
            {
                if (IsTunModeSelected)
                {
                    AppendLog("[TUN]", "Engaging Wintun L3 interface and locking DNS to 1.1.1.1...", TealTagBrush);
                    await _tunManager.StartTunAsync(_detectedPeerIp, _connectionCts.Token).ConfigureAwait(false);
                    AppendLog("[TUN]", "Wintun L3 kernel driver active. Default route established.", EmeraldTagBrush);
                }
                else
                {
                    AppendLog("[PROXY]", "Setting WinINET system proxy to 127.0.0.1:1820...", TealTagBrush);
                    _proxyManager.EnableProxy();
                    AppendLog("[PROXY]", "WinINET system proxy active on 127.0.0.1:1820.", EmeraldTagBrush);
                }
            }
            catch (Exception ex)
            {
                AppendLog("[NET-ERR]", $"Failed to engage network layer: {ex.Message}", CrimsonTagBrush);
                await DisconnectInternalAsync().ConfigureAwait(false);
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                _connectedSinceUtc = DateTime.UtcNow;
                _uptimeTimer.Start();
                CurrentState = TunnelConnectionState.Connected;
                StateChanged?.Invoke(ConnectionState.Connected, SelectedMode);
                CompanionSubtitle = $"PROTECTED VIA {GatewayPeer} // {RouteDatacenter}";
            });

            AppendLog("[LINK]", "MASQUE zero-leak tunnel fully armed and active.", EmeraldTagBrush);
        }

        private async Task DisconnectInternalAsync()
        {
            try
            {
                _connectionCts?.Cancel();
                await RunOnUiThreadAsync(() =>
                {
                    _uptimeTimer.Stop();
                    CompanionSubtitle = "DISENGAGING MASQUE CORE...";
                });

                _tunManager.StopTun();
                _proxyManager.DisableProxy();
                await _processService.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                await RunOnUiThreadAsync(() =>
                {
                    CurrentState = TunnelConnectionState.Idle;
                    StateChanged?.Invoke(ConnectionState.Disconnected, SelectedMode);
                    CompanionSubtitle = "ELVEN MANA & HEARTH // MASQUE EDITION";
                });
                AppendLog("[LINK]", "Tunnel disengaged cleanly. Adapters restored.", TealTagBrush);
            }
        }

        private void HandleAetherLog(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;

            string tag = "[CORE]";
            Brush brush = TealTagBrush;

            if (raw.Contains("masque exit:", StringComparison.OrdinalIgnoreCase))
            {
                tag = "[EXIT]";
                brush = EmeraldTagBrush;
            }
            else if (raw.Contains("Selected edge:", StringComparison.OrdinalIgnoreCase) ||
                     raw.Contains("connect-ip status: 200", StringComparison.OrdinalIgnoreCase))
            {
                tag = "[HANDSHAKE]";
                brush = EmeraldTagBrush;
            }
            else if (raw.Contains("hunting", StringComparison.OrdinalIgnoreCase) ||
                     raw.Contains("probing", StringComparison.OrdinalIgnoreCase))
            {
                tag = "[RADAR]";
                brush = GoldTagBrush;
            }
            else if (raw.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                     raw.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                     raw.Contains("10054", StringComparison.OrdinalIgnoreCase))
            {
                tag = "[WARN]";
                brush = CrimsonTagBrush;
            }

            AppendLog(tag, raw, brush);
        }

        private void AppendRawLog(string message)
        {
            string tag = "[NET]";
            Brush brush = TealTagBrush;

            if (message.StartsWith("[TUN2SOCKS]", StringComparison.OrdinalIgnoreCase))
            {
                tag = "[TUN]";
                brush = TealTagBrush;
            }
            else if (message.StartsWith("[DNS]", StringComparison.OrdinalIgnoreCase))
            {
                tag = "[DNS]";
                brush = GoldTagBrush;
            }
            else if (message.StartsWith("[PROXY]", StringComparison.OrdinalIgnoreCase))
            {
                tag = "[PROXY]";
                brush = CyanTagBrush;
            }
            else if (message.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("fail", StringComparison.OrdinalIgnoreCase))
            {
                tag = "[ERR]";
                brush = CrimsonTagBrush;
            }

            AppendLog(tag, message, brush);
        }

        private void HandleTelemetryUpdated(ExitNodeTelemetry telemetry)
        {
            RunOnUiThread(() =>
            {
                RouteDatacenter = $"{telemetry.Route} ({telemetry.IpAddress})";
                LatencyRtt = telemetry.Rtt;
            });
            AppendLog("[EXIT]", $"MASQUE Exit: {telemetry.IpAddress} ({telemetry.Route}) | RTT: {telemetry.Rtt}", EmeraldTagBrush);
        }

        private void HandleActiveEndpointDetected(string endpoint)
        {
            _detectedPeerIp = endpoint.Split(':')[0];
            RunOnUiThread(() =>
            {
                GatewayPeer = endpoint;
            });

            if (IsTunModeSelected && !string.IsNullOrWhiteSpace(endpoint))
            {
                _tunManager.AddBypassHostRoute(endpoint);
            }
        }

        private void HandleProcessTunnelStateChanged(bool isRunning)
        {
            RunOnUiThread(() =>
            {
                if (!isRunning && CurrentState == TunnelConnectionState.Connected)
                {
                    _uptimeTimer.Stop();
                    _tunManager.StopTun();
                    _proxyManager.DisableProxy();
                    CurrentState = TunnelConnectionState.Idle;
                    StateChanged?.Invoke(ConnectionState.Disconnected, SelectedMode);
                    CompanionSubtitle = "BACKEND DISCONNECTED // READY TO RECONNECT";
                    AppendLog("[WARN]", "Aether backend terminated unexpectedly.", CrimsonTagBrush);
                }
            });
        }

        public void AppendLog(string tag, string message, Brush tagBrush, Brush? messageBrush = null)
        {
            var entry = new TelemetryLogEntry
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                Tag = tag,
                Message = message,
                TagBrush = tagBrush,
                MessageBrush = messageBrush ?? SecondaryTextBrush
            };

            RunOnUiThread(() =>
            {
                Logs.Add(entry);
                while (Logs.Count > MaxLogEntries)
                {
                    Logs.RemoveAt(0);
                }
                LogCountBadgeText = $"{Logs.Count} events";
            });
        }

        private static void RunOnUiThread(Action action)
        {
            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.HasShutdownStarted)
            {
                if (dispatcher.CheckAccess())
                    action();
                else
                    dispatcher.InvokeAsync(action);
            }
        }

        private static Task RunOnUiThreadAsync(Action action)
        {
            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.HasShutdownStarted)
            {
                if (dispatcher.CheckAccess())
                {
                    action();
                    return Task.CompletedTask;
                }
                return dispatcher.InvokeAsync(action).Task;
            }

            action();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _uptimeTimer.Stop();
            _connectionCts?.Cancel();

            _tunManager.Dispose();
            _proxyManager.DisableProxy();
            _processService.Dispose();
            _jobManager.Dispose();
        }
    }
}
