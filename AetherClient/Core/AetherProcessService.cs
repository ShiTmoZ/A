using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AetherClient.Models;

namespace AetherClient.Core
{
    public sealed partial class AetherProcessService : IDisposable
    {
        private const string ExecutableName = "aether.exe";
        public const string LocalProxyHost = "127.0.0.1";
        public const int LocalHttpProxyPort = 1820;
        public const int LocalSocksProxyPort = 1819;

        private static readonly string[] DefaultCleanEdges =
        [
            "188.114.97.3:443",
            "188.114.96.3:443",
            "188.114.98.2:443",
            "188.114.99.1:443",
            "162.159.192.1:443",
            "162.159.193.5:443",
            "162.159.198.198:443",
            "162.159.198.4:443",
            "162.159.198.65:443"
        ];

        [GeneratedRegex(@"masque(?:-in-masque)? exit:\s*([^,]+),\s*([^,]+),\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex MasqueExitRegex();

        [GeneratedRegex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}:\d{2,5}\b", RegexOptions.Compiled)]
        private static partial Regex EndpointRegex();

        private readonly JobObjectManager _jobManager;
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private Process? _process;
        private CancellationTokenSource? _cts;
        private bool _isStopping;

        public event Action<string>? LogReceived;
        public event Action? SocksServerReady;
        public event Action<bool>? TunnelStateChanged;
        public event Action<ExitNodeTelemetry>? TelemetryUpdated;
        public event Action<string>? ActiveEndpointDetected;
        public event Action<int>? ProcessExitedUnexpectedly;

        public bool IsRunning => _process is { HasExited: false };
        public string? ActivePeer { get; private set; }

        public AetherProcessService(JobObjectManager jobManager)
        {
            _jobManager = jobManager ?? throw new ArgumentNullException(nameof(jobManager));
        }

        public static void KillOrphanedProcesses()
        {
            foreach (var proc in Process.GetProcessesByName("aether"))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        public async Task<bool> StartAsync(bool forceFreshScan = false, CancellationToken cancellationToken = default)
        {
            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsRunning)
                {
                    await StopInternalAsync().ConfigureAwait(false);
                }

                string exePath = ResolveBinaryPath();
                if (!File.Exists(exePath))
                {
                    EmitLog($"[CRITICAL] Backend core binary '{ExecutableName}' not found in {AppDomain.CurrentDomain.BaseDirectory}");
                    return false;
                }

                _isStopping = false;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                string? fastestPeer = null;
                if (!forceFreshScan)
                {
                    fastestPeer = await FindFastestPeerAsync(_cts.Token).ConfigureAwait(false);
                }

                string peerArg = !string.IsNullOrWhiteSpace(fastestPeer)
                    ? $"--h2-peer {fastestPeer}"
                    : "--scan balanced --no-quick-reconnect";

                string arguments = $"--masque --h2 -4 --perf high {peerArg} --http-proxy {LocalProxyHost}:{LocalHttpProxyPort}";
                ActivePeer = fastestPeer ?? "Auto (Balanced)";
                EmitLog($"[EXEC] {ExecutableName} {arguments}");

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                _process = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };

                _process.Exited += OnProcessExited;

                var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                void OnReadyDetected()
                {
                    readyTcs.TrySetResult(true);
                }

                SocksServerReady += OnReadyDetected;

                if (!_process.Start())
                {
                    SocksServerReady -= OnReadyDetected;
                    EmitLog("[ERROR] Failed to spawn aether.exe process.");
                    return false;
                }

                _jobManager.AssignProcess(_process);

                try { _process.StandardInput.Close(); } catch { }

                _ = ConsumeStreamAsync(_process.StandardOutput, _cts.Token);
                _ = ConsumeStreamAsync(_process.StandardError, _cts.Token);

                // Wait for core to expose SOCKS5 or HTTP proxy listener (up to 25s)
                using var timeoutCts = new CancellationTokenSource(25000);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                try
                {
                    var probeTask = WaitForProxySocketAsync(linkedCts.Token);
                    var completed = await Task.WhenAny(readyTcs.Task, probeTask).ConfigureAwait(false);

                    if (completed == readyTcs.Task && await readyTcs.Task.ConfigureAwait(false))
                    {
                        // Ready via log trigger
                    }
                    else if (completed == probeTask && await probeTask.ConfigureAwait(false))
                    {
                        // Ready via socket probe
                    }
                    else
                    {
                        EmitLog("[TIMEOUT] MASQUE core proxy listener did not respond in time.");
                        await StopInternalAsync().ConfigureAwait(false);
                        return false;
                    }
                }
                finally
                {
                    SocksServerReady -= OnReadyDetected;
                }

                TunnelStateChanged?.Invoke(true);
                return true;
            }
            catch (OperationCanceledException)
            {
                EmitLog("[SYS] StartAsync canceled.");
                await StopInternalAsync().ConfigureAwait(false);
                return false;
            }
            catch (Exception ex)
            {
                EmitLog($"[EXCEPTION] StartAsync error: {ex.Message}");
                await StopInternalAsync().ConfigureAwait(false);
                return false;
            }
            finally
            {
                _lifecycleLock.Release();
            }
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

        public void Stop()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        private async Task StopInternalAsync()
        {
            _isStopping = true;

            try
            {
                if (_cts is not null)
                {
                    await _cts.CancelAsync().ConfigureAwait(false);
                    _cts.Dispose();
                    _cts = null;
                }
            }
            catch { }

            if (_process is not null)
            {
                try
                {
                    _process.Exited -= OnProcessExited;
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                        using var killTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await _process.WaitForExitAsync(killTimeoutCts.Token).ConfigureAwait(false);
                        EmitLog("[SYS] aether.exe process tree terminated cleanly.");
                    }
                }
                catch { }
                finally
                {
                    _process.Dispose();
                    _process = null;
                }
            }

            ActivePeer = null;
            TunnelStateChanged?.Invoke(false);
        }

        private async Task<bool> WaitForProxySocketAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (_process is { HasExited: true })
                    return false;

                try
                {
                    using var tcp = new TcpClient();
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probeCts.CancelAfter(200);

                    await tcp.ConnectAsync(LocalProxyHost, LocalHttpProxyPort, probeCts.Token).ConfigureAwait(false);
                    return true;
                }
                catch
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }

            return false;
        }

        private async Task ConsumeStreamAsync(StreamReader reader, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (line is null)
                        break;

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    ParseAndDispatchLine(line);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                EmitLog($"[SYS-WARN] StreamReader exception: {ex.Message}");
            }
        }

        private void ParseAndDispatchLine(string rawLine)
        {
            string line = rawLine.Trim();
            EmitLog(line);

            var exitMatch = MasqueExitRegex().Match(line);
            if (exitMatch.Success)
            {
                string ip = exitMatch.Groups[1].Value.Trim();
                string route = exitMatch.Groups[2].Value.Trim();
                string rtt = exitMatch.Groups[3].Value.Trim();
                TelemetryUpdated?.Invoke(new ExitNodeTelemetry(ip, route, rtt));
            }

            if (line.Contains("socks5 server listening", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("http proxy listening", StringComparison.OrdinalIgnoreCase))
            {
                SocksServerReady?.Invoke();
            }

            var epMatch = EndpointRegex().Match(line);
            if (epMatch.Success && (line.Contains("forced peer") ||
                                    line.Contains("selected MASQUE gateway") ||
                                    line.Contains("using cloudflare edge") ||
                                    line.Contains("MASQUE transport")))
            {
                ActivePeer = epMatch.Value;
                ActiveEndpointDetected?.Invoke(epMatch.Value);
            }
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            int exitCode = -1;
            if (sender is Process proc)
            {
                try { exitCode = proc.ExitCode; } catch { }
            }

            if (!_isStopping)
            {
                EmitLog($"[SYS-ERR] aether.exe exited unexpectedly (Code: {exitCode}).");
                TunnelStateChanged?.Invoke(false);
                ProcessExitedUnexpectedly?.Invoke(exitCode);
            }
        }

                private List<string> LoadCleanIpCandidates()
        {
            var list = new List<string>();
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clean_ips.txt");
            if (File.Exists(path))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        string trimmed = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                            continue;

                        string rawEp = trimmed;
                        int pipeIdx = rawEp.IndexOf('|');
                        if (pipeIdx > 0)
                            rawEp = rawEp[..pipeIdx].Trim();

                        if (!rawEp.Contains(':'))
                            rawEp += ":443";

                        if (!list.Contains(rawEp, StringComparer.OrdinalIgnoreCase))
                            list.Add(rawEp);
                    }
                }
                catch { }
            }

            foreach (var def in DefaultCleanEdges)
            {
                if (!list.Contains(def, StringComparer.OrdinalIgnoreCase))
                    list.Add(def);
            }

            return list;
        }

        private async Task<string?> FindFastestPeerAsync(CancellationToken ct)
        {
            var candidates = LoadCleanIpCandidates();
            if (candidates.Count == 0) return null;

            EmitLog($"[SYS] Clean IP radar: probing {candidates.Count} candidate edges in parallel...");

            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(600);

            var tasks = candidates.Select(async ep =>
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    using var tcp = new TcpClient();
                    var colon = ep.IndexOf(':');
                    string host = colon > 0 ? ep[..colon] : ep;
                    int port = colon > 0 && int.TryParse(ep[(colon + 1)..], out int p) ? p : 443;

                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(probeCts.Token);
                    connectCts.CancelAfter(450);

                    await tcp.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);
                    sw.Stop();
                    return (Endpoint: ep, RttMs: sw.ElapsedMilliseconds, Success: true);
                }
                catch
                {
                    return (Endpoint: ep, RttMs: 9999L, Success: false);
                }
            }).ToList();

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var best = results.Where(r => r.Success).OrderBy(r => r.RttMs).FirstOrDefault();

            if (best.Success && best.RttMs < 9999)
            {
                EmitLog($"[SYS] Clean IP radar: selected fastest edge {best.Endpoint} (RTT: {best.RttMs}ms)");
                return best.Endpoint;
            }

            EmitLog("[SYS] Clean IP radar: probe timed out; falling back to dynamic balanced scan.");
            return null;
        }

        private static string ResolveBinaryPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            [
                Path.Combine(baseDir, ExecutableName),
                Path.Combine(baseDir, "bin", ExecutableName),
                Path.Combine(baseDir, "core", ExecutableName)
            ];

            foreach (string path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }

            return candidates[0];
        }

        private void EmitLog(string message)
        {
            LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        public void Dispose()
        {
            Stop();
            _lifecycleLock.Dispose();
        }
    }
}
