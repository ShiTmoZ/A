using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AetherClient.Core;
using AetherClient.Models;
using AetherClient.Network;
using AetherClient.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AetherClient.ViewModels;

public enum CyberConnectionState
{
    Idle,
    Hunting,
    Connected
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogEntries = 300;

    private readonly EdgePingService _pingService;
    private readonly JobObjectManager _jobManager;
    private readonly AetherProcessService _processService;
    private readonly WinInetProxyManager _proxyManager;
    private readonly TunEngineManager _tunManager;
    private readonly DispatcherTimer _uptimeTimer;

    private CancellationTokenSource? _connectionCts;
    private CancellationTokenSource? _pingCts;
    private DateTime _connectedSinceUtc;
    private bool _disposed;

    public ObservableCollection<EdgeNodeModel> EdgeNodes { get; } = new();
    public ObservableCollection<string> TelemetryLogs { get; } = new();

    public event Action<ConnectionState, TunnelMode>? StateChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsHunting))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionSubtext))]
    private CyberConnectionState _connectionState = CyberConnectionState.Idle;

    [ObservableProperty]
    private EdgeNodeModel? _selectedNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemProxyMode))]
    [NotifyPropertyChangedFor(nameof(ActiveRoutingBadge))]
    private bool _isTunMode = true;

    [ObservableProperty]
    private bool _isPingingRadar;

    [ObservableProperty]
    private string _statusHeadline = "STANDBY // AETHER MASQUE READY";

    [ObservableProperty]
    private string _statusSubtext = "Select an Anycast Clean IP or engage Auto Turbo Mesh";

    [ObservableProperty]
    private string _activeEndpointDisplay = "AUTO TURBO MESH";

    [ObservableProperty]
    private string _sessionUptime = "00:00:00";

    [ObservableProperty]
    private string _radarSummaryText = "Embedded Anycast Radar Armed";

    [ObservableProperty]
    private string _lastHandshakeDuration = "-- ms";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExitLocationBadge))]
    private bool _isForeignExitMode = false;

    public string ExitLocationBadge => IsForeignExitMode ? "EUROPEAN EXIT (MIM)" : "DIRECT MASQUE";

    public bool IsIdle => ConnectionState == CyberConnectionState.Idle;
    public bool IsHunting => ConnectionState == CyberConnectionState.Hunting;
    public bool IsConnected => ConnectionState == CyberConnectionState.Connected;
    public bool IsSystemProxyMode => !IsTunMode;

    public string ActiveRoutingBadge => IsTunMode ? "WINTUN L3 KERNEL TUN" : "WININET SYSTEM PROXY";

    public string PrimaryActionText => ConnectionState switch
    {
        CyberConnectionState.Idle => "ENGAGE MASQUE LINK",
        CyberConnectionState.Hunting => "LOCKING CLEAN IP...",
        CyberConnectionState.Connected => "TERMINATE LINK",
        _ => "ENGAGE MASQUE LINK"
    };

    public string PrimaryActionSubtext => ConnectionState switch
    {
        CyberConnectionState.Idle => SelectedNode is { IsAuto: false }
            ? $"DIRECT PEER LOCK // {SelectedNode.Endpoint}"
            : "TURBO ANYCAST MESH // HTTP/2 RFC 9298",
        CyberConnectionState.Hunting => "ESTABLISHING ZERO-LEAK TLS 1.3 MASQUE STREAM",
        CyberConnectionState.Connected => $"PROTECTED VIA {ActiveEndpointDisplay} // {SessionUptime}",
        _ => string.Empty
    };

    public MainViewModel()
    {
        // Sanitize any orphaned OS state from prior unexpected exits
        AetherProcessService.KillOrphanedProcesses();
        WinInetProxyManager.SanitizeOrphanedProxySettings();
        TunEngineManager.CleanupOrphanedTunState();

        _jobManager = new JobObjectManager();
        _pingService = new EdgePingService();
        _processService = new AetherProcessService(_jobManager);
        _proxyManager = new WinInetProxyManager(AppendLog);
        _tunManager = new TunEngineManager(_jobManager, AppendLog);

        _processService.LogReceived += AppendLog;
        _processService.TunnelStateChanged += OnProcessTunnelStateChanged;
        _processService.ActiveEndpointDetected += OnActiveEndpointDetected;
        _processService.TelemetryUpdated += OnTelemetryUpdated;

        _uptimeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uptimeTimer.Tick += (_, _) =>
        {
            if (ConnectionState == CyberConnectionState.Connected)
            {
                TimeSpan elapsed = DateTime.UtcNow - _connectedSinceUtc;
                SessionUptime = elapsed.ToString(@"hh\:mm\:ss");
                OnPropertyChanged(nameof(PrimaryActionSubtext));
            }
        };

        InitializeNodes();

        // Background non-blocking ping sweep on startup
        _ = PingAllNodesAsync();
    }

    private void InitializeNodes()
    {
        var initialNodes = _pingService.LoadInitialNodePool();
        EdgeNodes.Clear();
        foreach (var node in initialNodes)
        {
            EdgeNodes.Add(node);
        }

        SelectedNode = EdgeNodes.FirstOrDefault(n => n.IsAuto) ?? EdgeNodes.FirstOrDefault();
        if (SelectedNode is not null)
        {
            SelectedNode.IsSelected = true;
            ActiveEndpointDisplay = SelectedNode.IsAuto ? "AUTO TURBO MESH" : SelectedNode.Endpoint;
        }

        AppendLog("[INIT] Aether Cyber-Glass HUD ready. Clean IP pool armed.");
    }

    partial void OnSelectedNodeChanged(EdgeNodeModel? value)
    {
        if (value is null)
            return;

        foreach (var node in EdgeNodes)
        {
            node.IsSelected = ReferenceEquals(node, value);
        }

        if (ConnectionState == CyberConnectionState.Idle)
        {
            ActiveEndpointDisplay = value.IsAuto ? "AUTO TURBO MESH" : value.Endpoint;
            StatusSubtext = value.IsAuto
                ? "Automatic Anycast selection enabled (--turbo)"
                : $"Pinned to {value.Name} (--peer {value.Endpoint})";
        }

        OnPropertyChanged(nameof(PrimaryActionSubtext));
    }

    [RelayCommand]
    private void SelectNode(EdgeNodeModel? node)
    {
        if (node is null)
            return;

        SelectedNode = node;
    }

    [RelayCommand]
    private void LockFastestNode()
    {
        var fastest = EdgeNodes
            .Where(n => !n.IsAuto && n.LatencyMs > 0)
            .OrderBy(n => n.LatencyMs)
            .FirstOrDefault();

        if (fastest is not null)
        {
            SelectedNode = fastest;
            AppendLog($"[RADAR] Locked onto lowest-latency edge: {fastest.Name} ({fastest.Endpoint} @ {fastest.LatencyMs} ms)");
        }
    }

    [RelayCommand]
    private void SetRoutingMode(string? mode)
    {
        if (ConnectionState != CyberConnectionState.Idle)
        {
            AppendLog("[WARN] Disconnect the active tunnel before changing routing mode.");
            return;
        }

        bool targetTun = string.Equals(mode, "TUN", StringComparison.OrdinalIgnoreCase);
        if (IsTunMode == targetTun)
            return;

        IsTunMode = targetTun;
        AppendLog($"[MODE] Routing architecture set to {(IsTunMode ? "True Wintun Kernel TUN" : "WinINET System Proxy (127.0.0.1:1820)")}.");
    }

    [RelayCommand]
    private void ToggleForeignExit()
    {
        if (ConnectionState != CyberConnectionState.Idle)
        {
            AppendLog("[WARN] Disconnect active link before toggling European Exit mode.");
            return;
        }

        IsForeignExitMode = !IsForeignExitMode;
        AppendLog(IsForeignExitMode
            ? "[CONFIG] European Foreign Exit enabled (MASQUE-in-MASQUE). Tunnel will route via inner European hop to show outside Iran."
            : "[CONFIG] Direct MASQUE enabled. Maximum single-hop throughput and lowest latency.");
    }

    [RelayCommand]
    private async Task PingAllNodesAsync()
    {
        if (IsPingingRadar)
            return;

        try
        {
            IsPingingRadar = true;
            _pingCts?.Cancel();
            _pingCts = new CancellationTokenSource();

            RadarSummaryText = "Sweeping Anycast TCP Edges...";
            AppendLog("[RADAR] Initiating parallel TCP handshake sweep across Cloudflare Anycast pool (1200ms timeout)...");

            string? currentSelectedEndpoint = SelectedNode?.Endpoint;
            var sortedNodes = await _pingService.PingAllNodesAsync(EdgeNodes, _pingCts.Token).ConfigureAwait(true);

            EdgeNodes.Clear();
            foreach (var node in sortedNodes)
            {
                EdgeNodes.Add(node);
            }

            var restored = EdgeNodes.FirstOrDefault(n => string.Equals(n.Endpoint, currentSelectedEndpoint, StringComparison.OrdinalIgnoreCase))
                           ?? EdgeNodes.FirstOrDefault();

            SelectedNode = restored;

            int reachableCount = EdgeNodes.Count(n => !n.IsAuto && n.LatencyMs > 0);
            int totalStatic = EdgeNodes.Count(n => !n.IsAuto);
            var bestNode = EdgeNodes.FirstOrDefault(n => !n.IsAuto && n.LatencyMs > 0);

            if (bestNode is not null)
            {
                RadarSummaryText = $"{reachableCount}/{totalStatic} Online • Best: {bestNode.RegionCode} {bestNode.LatencyMs} ms";
                AppendLog($"[RADAR] Sweep complete: {reachableCount}/{totalStatic} clean IPs verified. Fastest: {bestNode.Name} ({bestNode.Endpoint}) @ {bestNode.LatencyMs} ms.");
            }
            else
            {
                RadarSummaryText = $"0/{totalStatic} Reachable • Check ISP Link";
                AppendLog("[RADAR] Sweep complete: All tested endpoints timed out (>1200ms).");
            }
        }
        catch (OperationCanceledException)
        {
            RadarSummaryText = "Radar Sweep Canceled";
        }
        catch (Exception ex)
        {
            RadarSummaryText = "Radar Sweep Error";
            AppendLog($"[RADAR-ERR] {ex.Message}");
        }
        finally
        {
            IsPingingRadar = false;
        }
    }

    [RelayCommand]
    public async Task ToggleConnectionAsync()
    {
        if (ConnectionState == CyberConnectionState.Connected ||
            ConnectionState == CyberConnectionState.Hunting)
        {
            await DisconnectInternalAsync().ConfigureAwait(false);
            return;
        }

        await ConnectInternalAsync(forceFreshScan: false).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ForceRescanConnectAsync()
    {
        await ConnectInternalAsync(forceFreshScan: true).ConfigureAwait(false);
    }

    private async Task ConnectInternalAsync(bool forceFreshScan)
    {
        _connectionCts?.Cancel();
        _connectionCts = new CancellationTokenSource();

        await RunOnUiThreadAsync(() =>
        {
            ConnectionState = CyberConnectionState.Hunting;
            StateChanged?.Invoke(Models.ConnectionState.Hunting, IsTunMode ? TunnelMode.TrueTun : TunnelMode.SystemProxy);

            string? customPeer = SelectedNode is { IsAuto: false } ? SelectedNode.Endpoint : null;
            bool isDirectPeer = !string.IsNullOrWhiteSpace(customPeer);

            StatusHeadline = isDirectPeer
                ? $"LOCKING PEER // {SelectedNode?.Name.ToUpperInvariant()}"
                : "HUNTING CLEAN MASQUE EDGE...";

            StatusSubtext = isDirectPeer
                ? $"Establishing direct HTTP/2 MASQUE tunnel via {customPeer}"
                : "Negotiating Cloudflare WARP Anycast Turbo handshake...";
        });

        long startTick = Stopwatch.GetTimestamp();
        string? peerToPass = SelectedNode is { IsAuto: false } ? SelectedNode.Endpoint : null;

        bool started = await _processService.StartAsync(
            forceFreshScan: forceFreshScan,
            customPeer: peerToPass,
            useForeignExit: IsForeignExitMode,
            cancellationToken: _connectionCts.Token).ConfigureAwait(false);

        if (!started)
        {
            await RunOnUiThreadAsync(() =>
            {
                ConnectionState = CyberConnectionState.Idle;
                StateChanged?.Invoke(Models.ConnectionState.Disconnected, IsTunMode ? TunnelMode.TrueTun : TunnelMode.SystemProxy);
                StatusHeadline = "LINK FAILED // RETRY OR SWITCH PEER";
                StatusSubtext = "Select another green (<180ms) Anycast node from the Clean IP Radar";
            });
            return;
        }

        // Engage system routing layer (Wintun kernel TUN vs WinINET proxy)
        try
        {
            if (IsTunMode)
            {
                string? endpointIp = SelectedNode?.IpOnly;
                await _tunManager.StartTunAsync(endpointIp, _connectionCts.Token).ConfigureAwait(false);
            }
            else
            {
                _proxyManager.EnableProxy();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[NET-ERR] Routing activation failed: {ex.Message}");
            await DisconnectInternalAsync().ConfigureAwait(false);
            return;
        }

        int connectMs = (int)Math.Round(Stopwatch.GetElapsedTime(startTick).TotalMilliseconds);

        await RunOnUiThreadAsync(() =>
        {
            LastHandshakeDuration = $"{connectMs} ms";
            _connectedSinceUtc = DateTime.UtcNow;
            SessionUptime = "00:00:00";
            _uptimeTimer.Start();

            ConnectionState = CyberConnectionState.Connected;
            StateChanged?.Invoke(Models.ConnectionState.Connected, IsTunMode ? TunnelMode.TrueTun : TunnelMode.SystemProxy);

            ActiveEndpointDisplay = peerToPass ?? "TURBO ANYCAST MESH";
            StatusHeadline = "MASQUE TUNNEL ACTIVE // ZERO LEAK";
            StatusSubtext = $"Connected in {connectMs} ms • Mode: {ActiveRoutingBadge}";
            AppendLog($"[LINK] MASQUE Tunnel fully armed in {connectMs} ms ({ActiveRoutingBadge}).");
        });
    }

    public async Task DisconnectAsync()
    {
        await DisconnectInternalAsync().ConfigureAwait(false);
    }

    private async Task DisconnectInternalAsync()
    {
        try
        {
            _connectionCts?.Cancel();
            await RunOnUiThreadAsync(() =>
            {
                _uptimeTimer.Stop();
                StatusHeadline = "DISENGAGING MASQUE CORE...";
            });

            _tunManager.StopTun();
            _proxyManager.DisableProxy();
            await _processService.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                ConnectionState = CyberConnectionState.Idle;
                StateChanged?.Invoke(Models.ConnectionState.Disconnected, IsTunMode ? TunnelMode.TrueTun : TunnelMode.SystemProxy);
                SessionUptime = "00:00:00";
                StatusHeadline = "STANDBY // AETHER MASQUE READY";
                StatusSubtext = SelectedNode is { IsAuto: false }
                    ? $"Pinned to {SelectedNode.Name} (--peer {SelectedNode.Endpoint})"
                    : "Select an Anycast Clean IP or engage Auto Turbo Mesh";
                AppendLog("[LINK] Tunnel disconnected cleanly.");
            });
        }
    }

    private void OnTelemetryUpdated(ExitNodeTelemetry telemetry)
    {
        AppendLog($"[TELEMETRY] Exit Node: {telemetry.IpAddress} ({telemetry.Route}) | RTT: {telemetry.Rtt}");
    }

    private void OnActiveEndpointDetected(string endpoint)
    {
        RunOnUiThread(() =>
        {
            ActiveEndpointDisplay = endpoint;
            OnPropertyChanged(nameof(PrimaryActionSubtext));
        });

        if (IsTunMode && !string.IsNullOrWhiteSpace(endpoint))
        {
            _tunManager.AddBypassHostRoute(endpoint);
        }
    }

    private void OnProcessTunnelStateChanged(bool isRunning)
    {
        RunOnUiThread(() =>
        {
            if (!isRunning && ConnectionState == CyberConnectionState.Connected)
            {
                _uptimeTimer.Stop();
                _tunManager.StopTun();
                _proxyManager.DisableProxy();
                ConnectionState = CyberConnectionState.Idle;
                StateChanged?.Invoke(Models.ConnectionState.Disconnected, IsTunMode ? TunnelMode.TrueTun : TunnelMode.SystemProxy);
                StatusHeadline = "STANDBY // LINK TERMINATED";
                StatusSubtext = "Core process stopped. Ready to re-engage.";
                AppendLog("[WARN] MASQUE backend closed unexpectedly.");
            }
        });
    }

    private void AppendLog(string message)
    {
        RunOnUiThread(() =>
        {
            TelemetryLogs.Add(message);
            while (TelemetryLogs.Count > MaxLogEntries)
            {
                TelemetryLogs.RemoveAt(0);
            }
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
        if (_disposed)
            return;

        _disposed = true;
        _uptimeTimer.Stop();
        _pingCts?.Cancel();
        _connectionCts?.Cancel();

        _tunManager.Dispose();
        _proxyManager.DisableProxy();
        _processService.Dispose();
        _jobManager.Dispose();
    }
}
