using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AetherClient.Core;

namespace AetherClient.Network
{
    public sealed class TunEngineManager : IDisposable
    {
        public const string AdapterName = "Aether-Tun";
        private const string Tun2SocksExe = "tun2socks.exe";
        private const string WintunDll = "wintun.dll";
        private const string TunAddress = "10.0.85.2";
        private const string TunGateway = "10.0.85.1";
        private const string TunNetmask = "255.255.255.0";
        private const string PrimaryDns = "1.1.1.1";

        // Cloudflare MASQUE / WARP Endpoint CIDRs to bypass the TUN adapter so aether.exe UDP/H2 never loops
        private static readonly (string Network, string Mask)[] MasqueBypassSubnets =
        [
            ("162.159.192.0", "255.255.192.0"),
            ("188.114.96.0", "255.255.240.0")
        ];

        private readonly JobObjectManager _jobManager;
        private readonly Action<string> _logger;
        private readonly HashSet<string> _installedBypassHosts = new(StringComparer.OrdinalIgnoreCase);

        private Process? _tunProcess;
        private string? _physicalGatewayIp;
        private int _physicalIfIndex = -1;
        private string? _physicalAdapterName;
        private bool _routesConfigured;

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

            RunSilentCommand("route", $"delete 0.0.0.0 mask 128.0.0.0 {TunGateway}");
            RunSilentCommand("route", $"delete 128.0.0.0 mask 128.0.0.0 {TunGateway}");
            RunSilentCommand("route", $"delete 0.0.0.0 mask 0.0.0.0 {TunGateway}");
        }

        public async Task StartTunAsync(string? knownExitIp, CancellationToken cancellationToken)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string tun2SocksPath = Path.Combine(baseDir, Tun2SocksExe);
            string wintunPath = Path.Combine(baseDir, WintunDll);

            if (!File.Exists(tun2SocksPath))
                throw new FileNotFoundException($"Required TUN engine '{Tun2SocksExe}' is missing from {baseDir}.", tun2SocksPath);

            if (!File.Exists(wintunPath))
                throw new FileNotFoundException($"Required Wintun driver '{WintunDll}' is missing from {baseDir}.", wintunPath);

            // 1. Discover primary physical NIC and Default Gateway before modifying the routing table
            DiscoverPhysicalDefaultGateway();
            _logger($"[NET-TUN] Bound physical egress to '{_physicalAdapterName}' (Gateway: {_physicalGatewayIp}, IfIndex: {_physicalIfIndex})");

            // 2. Launch tun2socks with wintun device 'Aether-Tun'
            string args = $"-device tun://{AdapterName} -proxy socks5://127.0.0.1:1819 -interface \"{_physicalAdapterName}\" -loglevel warn";
            var psi = new ProcessStartInfo
            {
                FileName = tun2SocksPath,
                Arguments = args,
                WorkingDirectory = baseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            _tunProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!_tunProcess.Start())
                throw new InvalidOperationException("Failed to start tun2socks.exe.");

            _jobManager.AssignProcess(_tunProcess);
            _ = ConsumeTunLogsAsync(_tunProcess.StandardOutput, cancellationToken);
            _ = ConsumeTunLogsAsync(_tunProcess.StandardError, cancellationToken);

            // 3. Wait for Wintun kernel interface 'Aether-Tun' to initialize
            int tunIfIndex = await WaitForTunInterfaceIndexAsync(cancellationToken).ConfigureAwait(false);
            _logger($"[NET-TUN] Virtual interface '{AdapterName}' active (IfIndex: {tunIfIndex}). Configuring IPv4 & Routes...");

            // 4. Configure static IP, Netmask, and DNS on Aether-Tun
            await RunCommandAsync("netsh", $"interface ip set address name=\"{AdapterName}\" source=static addr={TunAddress} mask={TunNetmask} gateway=none", cancellationToken).ConfigureAwait(false);
            await RunCommandAsync("netsh", $"interface ipv4 set interface \"{AdapterName}\" metric=1 mtu=1400", cancellationToken).ConfigureAwait(false);
            await RunCommandAsync("netsh", $"interface ip set dns name=\"{AdapterName}\" static {PrimaryDns} validate=no", cancellationToken).ConfigureAwait(false);

            // 5. Pin MASQUE subnets & Exit IP to the physical gateway to prevent routing loops
            if (!string.IsNullOrWhiteSpace(_physicalGatewayIp) && _physicalIfIndex > 0)
            {
                foreach (var (network, mask) in MasqueBypassSubnets)
                {
                    await RunCommandAsync("route", $"add {network} mask {mask} {_physicalGatewayIp} metric 1 if {_physicalIfIndex}", cancellationToken).ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(knownExitIp))
                {
                    AddBypassHostRoute(knownExitIp);
                }
            }

            // 6. Route 0.0.0.0/0 (via 0.0.0.0/1 + 128.0.0.0/1 and 0.0.0.0/0 metric 1) into Aether-Tun
            await RunCommandAsync("route", $"add 0.0.0.0 mask 128.0.0.0 {TunGateway} metric 1 if {tunIfIndex}", cancellationToken).ConfigureAwait(false);
            await RunCommandAsync("route", $"add 128.0.0.0 mask 128.0.0.0 {TunGateway} metric 1 if {tunIfIndex}", cancellationToken).ConfigureAwait(false);
            await RunCommandAsync("route", $"add 0.0.0.0 mask 0.0.0.0 {TunGateway} metric 1 if {tunIfIndex}", cancellationToken).ConfigureAwait(false);

            _routesConfigured = true;
            NativeMethods.DnsFlushResolverCache();
            _logger($"[NET-TUN] Full system tunnel online. 0.0.0.0/0 -> {AdapterName} ({TunGateway}) -> 127.0.0.1:1819.");
        }

        public void AddBypassHostRoute(string ipCandidate)
        {
            if (string.IsNullOrWhiteSpace(_physicalGatewayIp) || _physicalIfIndex <= 0)
                return;

            string cleanIp = ipCandidate.Trim();
            int colonIndex = cleanIp.IndexOf(':');
            if (colonIndex > 0)
                cleanIp = cleanIp[..colonIndex];

            if (!IPAddress.TryParse(cleanIp, out IPAddress? parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
                return;

            lock (_installedBypassHosts)
            {
                if (_installedBypassHosts.Add(cleanIp))
                {
                    RunSilentCommand("route", $"add {cleanIp} mask 255.255.255.255 {_physicalGatewayIp} metric 1 if {_physicalIfIndex}");
                    _logger($"[NET-TUN] Added physical egress bypass route for exit node {cleanIp}/32.");
                }
            }
        }

        public void StopTun()
        {
            if (_routesConfigured)
            {
                _routesConfigured = false;
                RunSilentCommand("route", $"delete 0.0.0.0 mask 128.0.0.0 {TunGateway}");
                RunSilentCommand("route", $"delete 128.0.0.0 mask 128.0.0.0 {TunGateway}");
                RunSilentCommand("route", $"delete 0.0.0.0 mask 0.0.0.0 {TunGateway}");

                if (!string.IsNullOrWhiteSpace(_physicalGatewayIp))
                {
                    foreach (var (network, mask) in MasqueBypassSubnets)
                    {
                        RunSilentCommand("route", $"delete {network} mask {mask} {_physicalGatewayIp}");
                    }

                    lock (_installedBypassHosts)
                    {
                        foreach (string host in _installedBypassHosts)
                        {
                            RunSilentCommand("route", $"delete {host} mask 255.255.255.255 {_physicalGatewayIp}");
                        }
                        _installedBypassHosts.Clear();
                    }
                }

                _logger("[NET-TUN] System routing table restored.");
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
                _logger($"[NET-TUN] {AdapterName} adapter torn down.");
            }

            NativeMethods.DnsFlushResolverCache();
        }

        private void DiscoverPhysicalDefaultGateway()
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni =>
                    ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    !ni.Name.Equals(AdapterName, StringComparison.OrdinalIgnoreCase) &&
                    !ni.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase));

            foreach (var ni in interfaces)
            {
                var ipProps = ni.GetIPProperties();
                var gw = ipProps.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(a));

                if (gw is not null)
                {
                    var ipv4Props = ipProps.GetIPv4Properties();
                    _physicalGatewayIp = gw.ToString();
                    _physicalIfIndex = ipv4Props.Index;
                    _physicalAdapterName = ni.Name;
                    return;
                }
            }

            throw new InvalidOperationException("No active physical IPv4 Default Gateway found on this system.");
        }

        private static async Task<int> WaitForTunInterfaceIndexAsync(CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                token.ThrowIfCancellationRequested();

                var nic = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name.Equals(AdapterName, StringComparison.OrdinalIgnoreCase));

                if (nic is not null)
                {
                    try
                    {
                        var ipv4Props = nic.GetIPProperties().GetIPv4Properties();
                        if (ipv4Props.Index > 0)
                            return ipv4Props.Index;
                    }
                    catch { }
                }

                await Task.Delay(150, token).ConfigureAwait(false);
            }

            throw new TimeoutException($"Timed out waiting for Wintun adapter '{AdapterName}' to initialize.");
        }

        private async Task ConsumeTunLogsAsync(StreamReader reader, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (line is null) break;
                    if (!string.IsNullOrWhiteSpace(line))
                        _logger($"[TUN2SOCKS] {line.Trim()}");
                }
            }
            catch { }
        }

        private static async Task RunCommandAsync(string fileName, string arguments, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                await proc.WaitForExitAsync(token).ConfigureAwait(false);
            }
        }

        private static void RunSilentCommand(string fileName, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(1500);
            }
            catch { }
        }

        public void Dispose()
        {
            StopTun();
        }
    }
}
