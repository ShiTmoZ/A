using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AetherClient.Core;

namespace AetherClient.Network
{
    public sealed class TunEngineManager : IDisposable
    {
        public const string AdapterName = "wintun";
        private const string Tun2SocksExe = "tun2socks.exe";
        private const string WintunDll = "wintun.dll";
        private const string TunAddress = "10.0.85.2";
        private const string TunGateway = "10.0.85.1";
        private const string TunNetmask = "255.255.255.0";
        private const string PrimaryDns = "1.1.1.1";

        // Cloudflare Anycast IPv4 ranges that must NEVER route into TUN
        private static readonly (string Network, string Mask)[] MasqueBypassSubnets =
        [
            ("162.159.192.0", "255.255.192.0"),
            ("188.114.96.0", "255.255.240.0")
        ];

        private readonly JobObjectManager _jobManager;
        private readonly Action<string> _logger;
        private readonly HashSet<string> _installedBypassHosts = new(StringComparer.OrdinalIgnoreCase);

        private Process? _tunProcess;
        private string? _physicalAdapterName;
        private string? _physicalGatewayIp;
        private int _physicalIfIndex = -1;
        private bool _disposed;

        public TunEngineManager(JobObjectManager jobManager, Action<string> logger)
        {
            _jobManager = jobManager;
            _logger = logger;
        }

        public static void CleanupOrphanedTunState()
        {
            foreach (var proc in Process.GetProcessesByName("tun2socks"))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                finally { proc.Dispose(); }
            }

            RunSilentCommand("route", $"delete 0.0.0.0 mask 0.0.0.0 {TunGateway}");
        }

        public async Task StartTunAsync(string? knownPeerEndpoint, CancellationToken cancellationToken)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string tun2SocksPath = Path.Combine(baseDir, Tun2SocksExe);
            if (!File.Exists(tun2SocksPath))
                throw new FileNotFoundException($"Virtual TUN engine binary '{Tun2SocksExe}' was not found in {baseDir}.", tun2SocksPath);

            // 1. Snapshot primary physical network interface and gateway
            DiscoverPhysicalRouteInfo();
            if (string.IsNullOrWhiteSpace(_physicalGatewayIp))
            {
                _logger("[NET-TUN] Warning: Could not detect default physical gateway; routing failover will be limited.");
            }

            _logger($"[NET-TUN] Bound physical egress to '{_physicalAdapterName}' (Gateway: {_physicalGatewayIp}, IfIndex: {_physicalIfIndex})");

            // 2. Launch tun2socks with wintun device
            string args = $"--device wintun --proxy socks5://127.0.0.1:1819 --interface \"{_physicalAdapterName}\" --loglevel warn";
            var psi = new ProcessStartInfo
            {
                FileName = tun2SocksPath,
                Arguments = args,
                WorkingDirectory = baseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!proc.Start())
                throw new InvalidOperationException("Failed to launch tun2socks.exe kernel engine.");

            _jobManager.AssignProcess(proc);
            _tunProcess = proc;

            // 3. Wait for the Wintun adapter to appear in the OS network stack
            int tunIfIndex = await WaitForTunInterfaceIndexAsync(cancellationToken).ConfigureAwait(false);
            _logger($"[NET-TUN] Virtual interface '{AdapterName}' active (IfIndex: {tunIfIndex}). Configuring IPv4 & Routes...");

            // 4. Configure static IP, Netmask, Gateway, and DNS on wintun adapter
            await RunCommandAsync("netsh", $"interface ip set address name=\"{AdapterName}\" source=static addr={TunAddress} mask={TunNetmask} gateway={TunGateway} gwmetric=1", cancellationToken).ConfigureAwait(false);
            await RunCommandAsync("netsh", $"interface ipv4 set interface \"{AdapterName}\" metric=1 mtu=1360", cancellationToken).ConfigureAwait(false);
            await RunCommandAsync("netsh", $"interface ip set dns name=\"{AdapterName}\" static {PrimaryDns} validate=no", cancellationToken).ConfigureAwait(false);
            await RunCommandAsync("netsh", $"interface ip add dns name=\"{AdapterName}\" 1.0.0.1 index=2 validate=no", cancellationToken).ConfigureAwait(false);

            // 4.1 Redirect physical adapter DNS to 1.1.1.1 to prevent local router DNS poisoning (10.10.34.34)
            if (!string.IsNullOrWhiteSpace(_physicalAdapterName))
            {
                await RunCommandAsync("netsh", $"interface ip set dns name=\"{_physicalAdapterName}\" static {PrimaryDns} validate=no", cancellationToken).ConfigureAwait(false);
                _logger($"[NET-TUN] Secured physical adapter '{_physicalAdapterName}' DNS to {PrimaryDns} (anti-poisoning).");
            }

            // 5. Pin MASQUE subnets & Exit IP to physical gateway to prevent routing loops
            if (!string.IsNullOrWhiteSpace(_physicalGatewayIp) && _physicalIfIndex > 0)
            {
                foreach (var (network, mask) in MasqueBypassSubnets)
                {
                    await RunCommandAsync("route", $"add {network} mask {mask} {_physicalGatewayIp} metric 1 if {_physicalIfIndex}", cancellationToken).ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(knownPeerEndpoint))
                {
                    AddBypassHostRoute(knownPeerEndpoint);
                }
            }

            // 6. Set system default route through TUN
            await RunCommandAsync("route", $"add 0.0.0.0 mask 0.0.0.0 {TunGateway} metric 1 if {tunIfIndex}", cancellationToken).ConfigureAwait(false);

            NativeMethods.DnsFlushResolverCache();
            _logger("[NET-TUN] True TUN active: All system IPv4 traffic encapsulated over MASQUE H2.");
        }

        public void AddBypassHostRoute(string rawEndpoint)
        {
            if (string.IsNullOrWhiteSpace(rawEndpoint) || string.IsNullOrWhiteSpace(_physicalGatewayIp) || _physicalIfIndex <= 0)
                return;

            string cleanIp = rawEndpoint;
            int colonIndex = rawEndpoint.IndexOf(':');
            if (colonIndex > 0)
                cleanIp = rawEndpoint[..colonIndex];

            if (!System.Net.IPAddress.TryParse(cleanIp, out _))
                return;

            lock (_installedBypassHosts)
            {
                if (_installedBypassHosts.Add(cleanIp))
                {
                    RunSilentCommand("route", $"add {cleanIp} mask 255.255.255.255 {_physicalGatewayIp} metric 1 if {_physicalIfIndex}");
                    _logger($"[NET-TUN] Added physical egress bypass route for peer gateway {cleanIp}/32.");
                }
            }
        }

        public void StopTun()
        {
            try
            {
                // Remove default route via TUN gateway
                RunSilentCommand("route", $"delete 0.0.0.0 mask 0.0.0.0 {TunGateway}");

                // Remove bypass subnet routes
                if (!string.IsNullOrWhiteSpace(_physicalGatewayIp))
                {
                    foreach (var (network, _) in MasqueBypassSubnets)
                    {
                        RunSilentCommand("route", $"delete {network} {_physicalGatewayIp}");
                    }

                    lock (_installedBypassHosts)
                    {
                        foreach (string host in _installedBypassHosts)
                        {
                            RunSilentCommand("route", $"delete {host} {_physicalGatewayIp}");
                        }
                        _installedBypassHosts.Clear();
                    }
                }

                // Restore physical adapter DNS to DHCP
                if (!string.IsNullOrWhiteSpace(_physicalAdapterName))
                {
                    RunSilentCommand("netsh", $"interface ip set dns name=\"{_physicalAdapterName}\" dhcp");
                    _logger($"[NET-TUN] Restored physical adapter '{_physicalAdapterName}' DNS to DHCP.");
                }

                _logger("[NET-TUN] System routing table and DNS restored.");
            }
            catch (Exception ex)
            {
                _logger($"[NET-TUN-WARN] Teardown warning: {ex.Message}");
            }

            if (_tunProcess is not null)
            {
                try
                {
                    if (!_tunProcess.HasExited)
                    {
                        _tunProcess.Kill(entireProcessTree: true);
                        _tunProcess.WaitForExit(1500);
                    }
                }
                catch { }
                finally
                {
                    _tunProcess.Dispose();
                    _tunProcess = null;
                }
            }

            NativeMethods.DnsFlushResolverCache();
        }

        private void DiscoverPhysicalRouteInfo()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                                  nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                  nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                                  !nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) &&
                                  !nic.Description.Contains("TAP", StringComparison.OrdinalIgnoreCase) &&
                                  !nic.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var nic in interfaces)
                {
                    var props = nic.GetIPProperties();
                    var ipv4Props = props.GetIPv4Properties();
                    if (ipv4Props is null) continue;

                    var gateway = props.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

                    if (gateway is not null)
                    {
                        _physicalAdapterName = nic.Name;
                        _physicalGatewayIp = gateway.Address.ToString();
                        _physicalIfIndex = ipv4Props.Index;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"[NET-TUN-WARN] Error querying routing tables: {ex.Message}");
            }
        }

        private static async Task<int> WaitForTunInterfaceIndexAsync(CancellationToken token)
        {
            for (int i = 0; i < 40; i++)
            {
                token.ThrowIfCancellationRequested();

                var nic = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name.Equals(AdapterName, StringComparison.OrdinalIgnoreCase) ||
                                         n.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase));

                if (nic is not null)
                {
                    var ipv4 = nic.GetIPProperties().GetIPv4Properties();
                    if (ipv4 is not null && ipv4.Index > 0)
                        return ipv4.Index;
                }

                await Task.Delay(250, token).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for virtual TUN interface to register in Windows network stack.");
        }

        private static async Task RunCommandAsync(string command, string args, CancellationToken token)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            proc.Start();
            await proc.WaitForExitAsync(token).ConfigureAwait(false);
        }

        private static void RunSilentCommand(string command, string args)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                proc?.WaitForExit(1000);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopTun();
        }
    }
}
