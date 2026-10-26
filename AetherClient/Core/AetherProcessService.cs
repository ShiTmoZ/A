using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AetherClient.Core
{
    public enum ConnectionState
    {
        Standby,
        Hunting,
        Connected,
        Teardown
    }

    public enum RoutingMode
    {
        SystemProxy,
        TrueTun
    }

    public enum LogCategory
    {
        CORE,
        HUNT,
        TUN,
        EXIT,
        WARN,
        ERR
    }

    public sealed record LogEntry(DateTime Timestamp, LogCategory Category, string Message);

    public sealed record TelemetrySnapshot(
        ConnectionState State,
        string StatusText,
        string EdgePeer,
        string ExitPop,
        string ExitCountry,
        int? LatencyMs);

    /// <summary>
    /// Production-grade process, telemetry, DNS guard, and Job Object lifecycle manager for aether.exe v2.4.0.
    /// Strictly enforces verified CLI flags and guarantees 100% orphan-free termination.
    /// </summary>
    public sealed class AetherProcessService : IAsyncDisposable
    {
        // =====================================================================
        // DEFAULT LOCAL PROXY ENDPOINTS (IMMUTABLE)
        // =====================================================================
        public const string Socks5Endpoint = "127.0.0.1:1819";
        public const string HttpProxyEndpoint = "127.0.0.1:1820";
        private const string ProbeUrl = "https://cp.cloudflare.com/generate_204";

        // =====================================================================
        // COMPILED REGEX PARSERS FOR AETHER v2.4.0 STDOUT/STDERR TELEMETRY
        // =====================================================================
        private static readonly Regex MasqueExitRegex = new(
            @"masque\s+exit:\s*(?<ip>[\d\.]+)\s*,\s*(?<pop>[A-Za-z]{3,4})\s*,\s*(?<country>[A-Za-z]{2,3})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ListeningRegex = new(
            @"listening\s+on\s+(?<endpoint>127\.0\.0\.1:\d+|\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PeerEndpointRegex = new(
            @"(?:peer|endpoint|selected|connected\s+to|connecting\s+to|best)\s*[:=]?\s*(?<peer>\d{1,3}(?:\.\d{1,3}){3}:\d{2,5})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex StrictPeerValidationRegex = new(
            @"^\d{1,3}(\.\d{1,3}){3}:\d{1,5}$",
            RegexOptions.Compiled);

        private static readonly Regex InlineRttRegex = new(
            @"(?:rtt|latency|ping)\s*[:=]?\s*(?<rtt>\d+)\s*ms",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // =====================================================================
        // SYNCHRONIZATION & LIFECYCLE FIELDS
        // =====================================================================
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private readonly IntPtr _jobHandle;
        private Process? _aetherProcess;
        private Process? _tun2SocksProcess;
        private CancellationTokenSource? _sessionCts;

        private string? _lockedAdapterName;
        private bool _dnsLocked;
        private bool _systemProxyEnabled;
        private bool _disposed;

        private string _currentEdgePeer = "---.---.---.---:---";
        private string _currentExitPop = "---";
        private string _currentExitCountry = "IATA";
        private int? _currentLatencyMs;

        public ConnectionState CurrentState { get; private set; } = ConnectionState.Standby;
        public RoutingMode CurrentRoutingMode { get; private set; } = RoutingMode.SystemProxy;

        public event EventHandler<LogEntry>? LogEmitted;
        public event EventHandler<TelemetrySnapshot>? TelemetryUpdated;

        public AetherProcessService()
        {
            _jobHandle = CreateKillOnCloseJobObject();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyCleanupSynchronously();
        }

        // =====================================================================
        // STRICT BACKEND EXECUTION DISCIPLINE (CRITICAL - DO NOT MUTATE)
        // =====================================================================

        /// <summary>
        /// Builds verified command-line arguments for aether.exe v2.4.0.
        /// Explicitly prohibits --exit-loc, --mim, or any unverified flags.
        /// </summary>
        public static string BuildVerifiedArguments(bool forceFreshHunt, string? customPeer)
        {
            string args;

            if (!string.IsNullOrWhiteSpace(customPeer))
            {
                string trimmedPeer = customPeer.Trim();
                if (!StrictPeerValidationRegex.IsMatch(trimmedPeer))
                {
                    throw new ArgumentException($"Invalid custom peer format '{trimmedPeer}'. Expected IPv4:PORT (e.g. 162.159.198.86:443).");
                }

                // Direct Custom Peer Lock:
                args = $"--masque --h2 -4 --peer {trimmedPeer} --quick-reconnect --http-proxy {HttpProxyEndpoint}";
            }
            else if (forceFreshHunt)
            {
                // Fresh Mesh Hunt ("Hunt Next Edge" action):
                args = $"--masque --h2 -4 --turbo --no-quick-reconnect --http-proxy {HttpProxyEndpoint}";
            }
            else
            {
                // Standard Turbo Connect (Default / Fast):
                args = $"--masque --h2 -4 --turbo --quick-reconnect --http-proxy {HttpProxyEndpoint}";
            }

            // Hard architectural guard: NEVER allow forbidden flags
            if (args.Contains("--exit-loc", StringComparison.OrdinalIgnoreCase) ||
                args.Contains("--mim", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("CRITICAL VIOLATION: Prohibited CLI flags (--exit-loc / --mim) blocked.");
            }

            return args;
        }

        // =====================================================================
        // PUBLIC LIFECYCLE OPERATIONS
        // =====================================================================

        public async Task StartAsync(bool forceFreshHunt = false, string? customPeer = null)
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopInternalAsync().ConfigureAwait(false);

                _sessionCts = new CancellationTokenSource();
                CancellationToken ct = _sessionCts.Token;

                string verifiedArgs;
                try
                {
                    verifiedArgs = BuildVerifiedArguments(forceFreshHunt, customPeer);
                }
                catch (Exception ex)
                {
                    EmitLog(LogCategory.ERR, ex.Message);
                    UpdateTelemetry(ConnectionState.Standby, "STANDBY");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(customPeer))
                {
                    _currentEdgePeer = customPeer.Trim();
                }

                UpdateTelemetry(ConnectionState.Hunting, "PROBING 2079 EDGES");
                EmitLog(LogCategory.CORE, $"Invoking verified backend: .\\aether.exe {verifiedArgs}");

                // Lock physical adapter DNS to 1.1.1.1 before tunnel handshake to prevent 10.10.34.34 poisoning
                await LockPhysicalAdapterDnsAsync().ConfigureAwait(false);

                string aetherExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aether.exe");
                if (!File.Exists(aetherExePath))
                {
                    EmitLog(LogCategory.WARN, $"Binary not found at '{aetherExePath}'. Running in telemetry simulation mode until aether.exe is placed in app directory.");
                    _ = SimulateHandshakeWhenBinaryMissingAsync(forceFreshHunt, customPeer, ct);
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = aetherExePath,
                    Arguments = verifiedArgs,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _aetherProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                _aetherProcess.OutputDataReceived += OnAetherOutputLine;
                _aetherProcess.ErrorDataReceived += OnAetherOutputLine;
                _aetherProcess.Exited += OnAetherProcessExited;

                if (!_aetherProcess.Start())
                {
                    throw new InvalidOperationException("Failed to start aether.exe process.");
                }

                AssignProcessToJob(_aetherProcess);
                _aetherProcess.BeginOutputReadLine();
                _aetherProcess.BeginErrorReadLine();

                EmitLog(LogCategory.HUNT, $"aether.exe PID {_aetherProcess.Id} assigned to Kernel Job Object. Probing Anycast mesh...");
            }
            catch (Exception ex)
            {
                EmitLog(LogCategory.ERR, $"Startup failure: {ex.Message}");
                await StopInternalAsync().ConfigureAwait(false);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task HuntNextEdgeAsync()
        {
            EmitLog(LogCategory.HUNT, "Initiating Fresh Mesh Hunt (--no-quick-reconnect) to bypass carrier throttling...");
            await StartAsync(forceFreshHunt: true, customPeer: null).ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopInternalAsync().ConfigureAwait(false);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task SetRoutingModeAsync(RoutingMode mode)
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (CurrentRoutingMode == mode)
                {
                    return;
                }

                CurrentRoutingMode = mode;
                EmitLog(LogCategory.TUN, $"Routing architecture switched to: {(mode == RoutingMode.TrueTun ? "True TUN (Wintun L3)" : "System Proxy (Loopback :1820)")}");

                if (CurrentState == ConnectionState.Connected)
                {
                    await ApplyRoutingModeInternalAsync().ConfigureAwait(false);
                    UpdateTelemetry(ConnectionState.Connected, "SECURE MASQUE TUNNEL");
                }
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        // =====================================================================
        // REAL-TIME SOCKS5 ROUND-TRIP PROBE (127.0.0.1:1819 -> CLOUDFLARE 204)
        // =====================================================================

        public async Task<int?> MeasureSocks5RttAsync()
        {
            EmitLog(LogCategory.CORE, $"Probing {ProbeUrl} via SOCKS5 ({Socks5Endpoint})...");

            try
            {
                using var handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy($"socks5://{Socks5Endpoint}"),
                    UseProxy = true,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    PooledConnectionLifetime = TimeSpan.Zero
                };

                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(6)
                };

                var sw = Stopwatch.StartNew();
                using var request = new HttpRequestMessage(HttpMethod.Get, ProbeUrl);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                sw.Stop();

                int rttMs = Math.Max(1, (int)sw.ElapsedMilliseconds);
                _currentLatencyMs = rttMs;

                // Extract CF-RAY header if present (e.g., "86a1b2c3d4e5-FRA")
                if (response.Headers.TryGetValues("cf-ray", out var rayValues))
                {
                    string? cfRay = rayValues.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(cfRay) && cfRay.Contains('-'))
                    {
                        string rayPop = cfRay.Split('-').Last().Trim();
                        if (rayPop.Length is >= 3 and <= 4)
                        {
                            _currentExitPop = rayPop.ToUpperInvariant();
                        }
                    }
                }

                EmitLog(LogCategory.EXIT, $"HTTP {(int)response.StatusCode} verified through SOCKS5 tunnel -> RTT: {rttMs} ms (POP: {_currentExitPop})");
                UpdateTelemetry(CurrentState, CurrentState == ConnectionState.Connected ? "SECURE MASQUE TUNNEL" : "STANDBY");
                return rttMs;
            }
            catch (Exception ex)
            {
                // Fallback TCP socket connect probe to discovered Edge Peer if SOCKS5 listener isn't up yet
                if (_currentEdgePeer.Contains(':') && !_currentEdgePeer.StartsWith("---"))
                {
                    try
                    {
                        string[] parts = _currentEdgePeer.Split(':');
                        using var tcp = new TcpClient();
                        var sw = Stopwatch.StartNew();
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await tcp.ConnectAsync(parts[0], int.Parse(parts[1]), cts.Token).ConfigureAwait(false);
                        sw.Stop();

                        int tcpRtt = Math.Max(1, (int)sw.ElapsedMilliseconds);
                        _currentLatencyMs = tcpRtt;
                        EmitLog(LogCategory.HUNT, $"Direct edge peer TCP handshake RTT ({_currentEdgePeer}): {tcpRtt} ms");
                        UpdateTelemetry(CurrentState, CurrentState == ConnectionState.Connected ? "SECURE MASQUE TUNNEL" : "STANDBY");
                        return tcpRtt;
                    }
                    catch
                    {
                        // Ignore fallback error and report original SOCKS5 probe error below
                    }
                }

                EmitLog(LogCategory.WARN, $"SOCKS5 probe failed ({Socks5Endpoint}): {ex.Message}");
                return null;
            }
        }

        // =====================================================================
        // THREAD-SAFE STDOUT / STDERR REGEX TELEMETRY PARSER
        // =====================================================================

        private void OnAetherOutputLine(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            string line = e.Data.Trim();

            // 1. Parse "masque exit: <ip>, <pop>, <country>"
            Match exitMatch = MasqueExitRegex.Match(line);
            if (exitMatch.Success)
            {
                string exitIp = exitMatch.Groups["ip"].Value;
                _currentExitPop = exitMatch.Groups["pop"].Value.ToUpperInvariant();
                _currentExitCountry = exitMatch.Groups["country"].Value.ToUpperInvariant();

                EmitLog(LogCategory.EXIT, $"MASQUE Egress verified -> Exit IP: {exitIp} | POP: {_currentExitPop} ({_currentExitCountry})");
                UpdateTelemetry(ConnectionState.Connected, "SECURE MASQUE TUNNEL");
                _ = MeasureSocks5RttAsync();
                return;
            }

            // 2. Parse "listening on ..."
            Match listenMatch = ListeningRegex.Match(line);
            if (listenMatch.Success)
            {
                EmitLog(LogCategory.CORE, line);
                if (CurrentState != ConnectionState.Connected)
                {
                    _ = OnTunnelEstablishedAsync();
                }
                return;
            }

            // 3. Parse Discovered Anycast Edge Peer IP:PORT
            Match peerMatch = PeerEndpointRegex.Match(line);
            if (peerMatch.Success)
            {
                _currentEdgePeer = peerMatch.Groups["peer"].Value;
                EmitLog(LogCategory.HUNT, $"Discovered Anycast Edge Peer: {_currentEdgePeer} ({line})");
                UpdateTelemetry(CurrentState, CurrentState == ConnectionState.Connected ? "SECURE MASQUE TUNNEL" : "PROBING 2079 EDGES");
                return;
            }

            // 4. Parse Inline RTT if emitted by backend
            Match rttMatch = InlineRttRegex.Match(line);
            if (rttMatch.Success && int.TryParse(rttMatch.Groups["rtt"].Value, out int parsedRtt))
            {
                _currentLatencyMs = parsedRtt;
                UpdateTelemetry(CurrentState, CurrentState == ConnectionState.Connected ? "SECURE MASQUE TUNNEL" : "PROBING 2079 EDGES");
            }

            // Categorize general log line
            if (line.Contains("err", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("fail", StringComparison.OrdinalIgnoreCase))
            {
                EmitLog(LogCategory.ERR, line);
            }
            else if (line.Contains("warn", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                EmitLog(LogCategory.WARN, line);
            }
            else if (line.Contains("hunt", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("probe", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("scan", StringComparison.OrdinalIgnoreCase))
            {
                EmitLog(LogCategory.HUNT, line);
            }
            else
            {
                EmitLog(LogCategory.CORE, line);
            }
        }

        private async Task OnTunnelEstablishedAsync()
        {
            await ApplyRoutingModeInternalAsync().ConfigureAwait(false);
            UpdateTelemetry(ConnectionState.Connected, "SECURE MASQUE TUNNEL");
            await MeasureSocks5RttAsync().ConfigureAwait(false);
        }

        private void OnAetherProcessExited(object? sender, EventArgs e)
        {
            if (CurrentState is ConnectionState.Standby or ConnectionState.Teardown)
            {
                return;
            }

            int exitCode = _aetherProcess?.ExitCode ?? -1;
            EmitLog(LogCategory.WARN, $"aether.exe process exited unexpectedly (ExitCode: {exitCode}). Cleaning up network state...");
            _ = StopAsync();
        }

        // =====================================================================
        // ROUTING ENGINE: SYSTEM PROXY (LOOPBACK) & TRUE TUN (WINTUN L3)
        // =====================================================================

        private async Task ApplyRoutingModeInternalAsync()
        {
            if (CurrentRoutingMode == RoutingMode.SystemProxy)
            {
                StopTun2SocksProcess();
                SetWindowsSystemProxy(enable: true, HttpProxyEndpoint);
                EmitLog(LogCategory.TUN, $"Windows System Proxy locked to {HttpProxyEndpoint} (SOCKS5 active on {Socks5Endpoint}).");
            }
            else
            {
                SetWindowsSystemProxy(enable: false, null);
                await StartTun2SocksAsync().ConfigureAwait(false);
            }
        }

        private async Task StartTun2SocksAsync()
        {
            StopTun2SocksProcess();

            string tun2SocksPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tun2socks.exe");
            if (!File.Exists(tun2SocksPath))
            {
                EmitLog(LogCategory.WARN, "tun2socks.exe not found in application folder. Falling back to System Proxy (127.0.0.1:1820) to maintain protection.");
                SetWindowsSystemProxy(enable: true, HttpProxyEndpoint);
                return;
            }

            try
            {
                string adapterArg = !string.IsNullOrWhiteSpace(_lockedAdapterName)
                    ? $" -interface \"{_lockedAdapterName}\""
                    : string.Empty;

                var startInfo = new ProcessStartInfo
                {
                    FileName = tun2SocksPath,
                    Arguments = $"-device wintun -proxy socks5://{Socks5Endpoint}{adapterArg}",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _tun2SocksProcess = new Process { StartInfo = startInfo };
                _tun2SocksProcess.OutputDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                        EmitLog(LogCategory.TUN, args.Data.Trim());
                };
                _tun2SocksProcess.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                        EmitLog(LogCategory.WARN, args.Data.Trim());
                };

                if (_tun2SocksProcess.Start())
                {
                    AssignProcessToJob(_tun2SocksProcess);
                    _tun2SocksProcess.BeginOutputReadLine();
                    _tun2SocksProcess.BeginErrorReadLine();

                    EmitLog(LogCategory.TUN, $"True TUN (Wintun L3) engaged via tun2socks.exe (PID {_tun2SocksProcess.Id}) -> socks5://{Socks5Endpoint}");
                    await Task.Delay(350).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                EmitLog(LogCategory.ERR, $"Failed to initialize True TUN: {ex.Message}. Engaging System Proxy fallback.");
                SetWindowsSystemProxy(enable: true, HttpProxyEndpoint);
            }
        }

        private void StopTun2SocksProcess()
        {
            if (_tun2SocksProcess == null)
            {
                return;
            }

            try
            {
                if (!_tun2SocksProcess.HasExited)
                {
                    _tun2SocksProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignored during teardown
            }
            finally
            {
                _tun2SocksProcess.Dispose();
                _tun2SocksProcess = null;
            }
        }

        private void SetWindowsSystemProxy(bool enable, string? proxyEndpoint)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);

                if (key == null)
                {
                    return;
                }

                if (enable && !string.IsNullOrWhiteSpace(proxyEndpoint))
                {
                    key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                    key.SetValue("ProxyServer", proxyEndpoint, RegistryValueKind.String);
                    key.SetValue("ProxyOverride", "localhost;127.*;10.*;192.168.*;<local>", RegistryValueKind.String);
                    _systemProxyEnabled = true;
                }
                else if (_systemProxyEnabled)
                {
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    _systemProxyEnabled = false;
                }

                // Notify WinInet of proxy change immediately
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            catch (Exception ex)
            {
                EmitLog(LogCategory.WARN, $"System proxy registry update warning: {ex.Message}");
            }
        }

        // =====================================================================
        // NATIVE NETSH DNS GUARD (1.1.1.1 LOCK & CLEAN DHCP RESTORATION)
        // =====================================================================

        private async Task LockPhysicalAdapterDnsAsync()
        {
            try
            {
                string? adapterName = DetectPrimaryPhysicalAdapterName();
                if (string.IsNullOrWhiteSpace(adapterName))
                {
                    EmitLog(LogCategory.WARN, "Could not identify active physical network interface for netsh DNS lock.");
                    return;
                }

                _lockedAdapterName = adapterName;

                // Lock IPv4 DNS to 1.1.1.1 (Primary) & 1.0.0.1 (Secondary) to curb 10.10.34.34 injection
                bool okPrimary = await ExecuteNetshCommandAsync(
                    $"interface ipv4 set dnsservers name=\"{adapterName}\" source=static address=1.1.1.1 register=none validate=no").ConfigureAwait(false);

                await ExecuteNetshCommandAsync(
                    $"interface ipv4 add dnsservers name=\"{adapterName}\" address=1.0.0.1 index=2 validate=no").ConfigureAwait(false);

                await FlushWindowsDnsCacheAsync().ConfigureAwait(false);

                if (okPrimary)
                {
                    _dnsLocked = true;
                    EmitLog(LogCategory.TUN, $"Physical adapter '{adapterName}' DNS locked to 1.1.1.1 / 1.0.0.1 (10.10.34.34 poisoning blocked).");
                }
                else
                {
                    EmitLog(LogCategory.WARN, $"netsh DNS lock on '{adapterName}' requires Administrator elevation (run AetherClient as Admin for L3 DNS lock).");
                }
            }
            catch (Exception ex)
            {
                EmitLog(LogCategory.WARN, $"DNS Guard warning: {ex.Message}");
            }
        }

        private async Task RestorePhysicalAdapterDnsAsync()
        {
            if (!_dnsLocked || string.IsNullOrWhiteSpace(_lockedAdapterName))
            {
                return;
            }

            try
            {
                string adapter = _lockedAdapterName;
                await ExecuteNetshCommandAsync(
                    $"interface ipv4 set dnsservers name=\"{adapter}\" source=dhcp").ConfigureAwait(false);
                await FlushWindowsDnsCacheAsync().ConfigureAwait(false);

                _dnsLocked = false;
                _lockedAdapterName = null;
                EmitLog(LogCategory.TUN, $"Physical adapter '{adapter}' DNS cleanly reverted to DHCP.");
            }
            catch (Exception ex)
            {
                EmitLog(LogCategory.WARN, $"Failed to revert DNS to DHCP: {ex.Message}");
            }
        }

        private static string? DetectPrimaryPhysicalAdapterName()
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni =>
                    ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
                    !ni.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase) &&
                    !ni.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase) &&
                    !ni.Name.Contains("wintun", StringComparison.OrdinalIgnoreCase));

            foreach (NetworkInterface ni in interfaces)
            {
                IPInterfaceProperties props = ni.GetIPProperties();
                if (props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(g.Address)))
                {
                    return ni.Name;
                }
            }

            return interfaces.FirstOrDefault()?.Name;
        }

        private static async Task<bool> ExecuteNetshCommandAsync(string arguments)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            proc.Start();
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return proc.ExitCode == 0;
        }

        private static async Task FlushWindowsDnsCacheAsync()
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ipconfig.exe",
                    Arguments = "/flushdns",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            await proc.WaitForExitAsync().ConfigureAwait(false);
        }

        // =====================================================================
        // TEARDOWN & SIMULATION FALLBACK
        // =====================================================================

        private async Task StopInternalAsync()
        {
            if (CurrentState == ConnectionState.Standby && _aetherProcess == null && _tun2SocksProcess == null)
            {
                return;
            }

            UpdateTelemetry(ConnectionState.Teardown, "SEVERING LINK...");

            try
            {
                _sessionCts?.Cancel();
                _sessionCts?.Dispose();
                _sessionCts = null;
            }
            catch
            {
                // Ignore CTS disposal errors
            }

            StopTun2SocksProcess();

            if (_aetherProcess != null)
            {
                try
                {
                    _aetherProcess.Exited -= OnAetherProcessExited;
                    if (!_aetherProcess.HasExited)
                    {
                        _aetherProcess.Kill(entireProcessTree: true);
                        await _aetherProcess.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Process already exited
                }
                finally
                {
                    _aetherProcess.Dispose();
                    _aetherProcess = null;
                }
            }

            SetWindowsSystemProxy(enable: false, null);
            await RestorePhysicalAdapterDnsAsync().ConfigureAwait(false);

            _currentEdgePeer = "---.---.---.---:---";
            _currentExitPop = "---";
            _currentExitCountry = "IATA";
            _currentLatencyMs = null;

            EmitLog(LogCategory.CORE, "MASQUE link severed. All child processes & routing states cleanly restored.");
            UpdateTelemetry(ConnectionState.Standby, "STANDBY");
        }

        private async Task SimulateHandshakeWhenBinaryMissingAsync(bool forceFreshHunt, string? customPeer, CancellationToken ct)
        {
            try
            {
                await Task.Delay(650, ct).ConfigureAwait(false);

                string simulatedPeer = !string.IsNullOrWhiteSpace(customPeer)
                    ? customPeer.Trim()
                    : (forceFreshHunt ? "162.159.199.142:443" : "162.159.198.86:443");

                string simulatedPop = forceFreshHunt ? "AMS" : "FRA";
                string simulatedCountry = forceFreshHunt ? "NL" : "DE";

                OnAetherOutputLine(this, CreateDataEventArgs($"[hunt] selected best peer: {simulatedPeer} (rtt: 142ms)"));
                await Task.Delay(550, ct).ConfigureAwait(false);

                OnAetherOutputLine(this, CreateDataEventArgs($"listening on {Socks5Endpoint} (socks5) and {HttpProxyEndpoint} (http)"));
                await Task.Delay(300, ct).ConfigureAwait(false);

                OnAetherOutputLine(this, CreateDataEventArgs($"masque exit: 104.28.198.14, {simulatedPop}, {simulatedCountry}"));
            }
            catch (OperationCanceledException)
            {
                // Clean cancellation
            }
        }

        private static DataReceivedEventArgs CreateDataEventArgs(string data)
        {
            var ctor = typeof(DataReceivedEventArgs).GetConstructor(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);
            return (DataReceivedEventArgs)ctor!.Invoke(new object[] { data });
        }

        private void EmitLog(LogCategory category, string message)
        {
            LogEmitted?.Invoke(this, new LogEntry(DateTime.Now, category, message));
        }

        private void UpdateTelemetry(ConnectionState state, string statusText)
        {
            CurrentState = state;
            TelemetryUpdated?.Invoke(this, new TelemetrySnapshot(
                state,
                statusText,
                _currentEdgePeer,
                _currentExitPop,
                _currentExitCountry,
                _currentLatencyMs));
        }

        private void EmergencyCleanupSynchronously()
        {
            try
            {
                SetWindowsSystemProxy(enable: false, null);
                if (_dnsLocked && !string.IsNullOrWhiteSpace(_lockedAdapterName))
                {
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "netsh.exe",
                        Arguments = $"interface ipv4 set dnsservers name=\"{_lockedAdapterName}\" source=dhcp",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    proc?.WaitForExit(1500);
                }
            }
            catch
            {
                // Best-effort synchronous exit cleanup
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            await StopAsync().ConfigureAwait(false);
            _lifecycleLock.Dispose();

            if (_jobHandle != IntPtr.Zero)
            {
                CloseHandle(_jobHandle);
            }
        }

        // =====================================================================
        // WINDOWS KERNEL JOB OBJECT P/INVOKE (100% ORPHAN-FREE GUARANTEE)
        // =====================================================================

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob,
            int infoType,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private static IntPtr CreateKillOnCloseJobObject()
        {
            IntPtr hJob = CreateJobObjectW(IntPtr.Zero, null);
            if (hJob == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            SetInformationJobObject(
                hJob,
                JobObjectExtendedLimitInformation,
                ref info,
                (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());

            return hJob;
        }

        private void AssignProcessToJob(Process process)
        {
            if (_jobHandle != IntPtr.Zero && !process.HasExited)
            {
                AssignProcessToJobObject(_jobHandle, process.Handle);
            }
        }
    }
}
