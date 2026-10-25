using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AetherClient.Models;

namespace AetherClient.Core;

public sealed partial class AetherProcessService : IDisposable
{
    private const string ExecutableName = "aether.exe";
    public const string LocalProxyHost = "127.0.0.1";
    public const int LocalHttpProxyPort = 1820;
    public const int LocalSocksProxyPort = 1819;

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

    /// <summary>
    /// Starts the underlying MASQUE HTTP/2 core engine.
    /// If customPeer is provided, locks the edge peer and connects directly.
    /// If exitMode is EuropeanTunnel, launches in MASQUE-in-MASQUE (--mim) mode.
    /// If exitMode is BypassIran, appends --exit-loc !IR to reject Iranian exit nodes.
    /// </summary>
    public async Task<bool> StartAsync(
        bool forceFreshScan = false,
        string? customPeer = null,
        ExitLocationMode exitMode = ExitLocationMode.AutoAnycast,
        CancellationToken cancellationToken = default)
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

            bool isCustomPeer = !string.IsNullOrWhiteSpace(customPeer) &&
                                !customPeer.Equals("Auto", StringComparison.OrdinalIgnoreCase);

            bool isMim = exitMode == ExitLocationMode.EuropeanTunnel;
            string proto = isMim ? "--mim" : "--masque";
            string modeArgs;

            if (isCustomPeer)
            {
                string peer = customPeer!.Trim();
                string peerFlag = isMim ? "--mim-outer" : "--peer";
                modeArgs = $"{peerFlag} {peer} --quick-reconnect";
                ActivePeer = peer;
                EmitLog($"[RADAR-LOCK] Engaging direct Anycast peer lock -> {peer} (Proto: {proto})");
            }
            else
            {
                string scanFlag = forceFreshScan ? "--no-quick-reconnect" : "--quick-reconnect";
                modeArgs = $"--turbo {scanFlag}";
                ActivePeer = "Auto (Turbo)";
                EmitLog($"[TURBO-MESH] Engaging Cloudflare Anycast Turbo auto-probe ({proto} {modeArgs})");
            }

            string exitLocArg = exitMode == ExitLocationMode.BypassIran ? "--exit-loc !IR " : "";
            string arguments = $"{proto} --h2 -4 {modeArgs} {exitLocArg}--http-proxy {LocalProxyHost}:{LocalHttpProxyPort}";
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

            // Bind process to Windows Job Object for guaranteed orphan termination
            _jobManager.AssignProcess(_process);

            // Close stdin so Aether never waits for interactive CLI prompts
            try { _process.StandardInput.Close(); } catch { }

            _ = ConsumeStreamAsync(_process.StandardOutput, _cts.Token);
            _ = ConsumeStreamAsync(_process.StandardError, _cts.Token);

            if (isCustomPeer)
            {
                ActiveEndpointDetected?.Invoke(customPeer!);
            }

            // Wait for core to expose SOCKS5 or HTTP proxy listener
            int timeoutMs = isCustomPeer ? 8000 : 25000;
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                var probeTask = WaitForProxySocketAsync(linkedCts.Token);
                var completed = await Task.WhenAny(readyTcs.Task, probeTask).ConfigureAwait(false);

                if (completed == readyTcs.Task && await readyTcs.Task.ConfigureAwait(false))
                {
                    // Ready via logs
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
                await Task.Delay(40, ct).ConfigureAwait(false);
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

        // Check for Masque Exit Node Telemetry (e.g. [+] masque exit: 104.28.214.167, IR via FRA, 175ms to cloudflare)
        var exitMatch = MasqueExitRegex().Match(line);
        if (exitMatch.Success)
        {
            string ip = exitMatch.Groups[1].Value.Trim();
            string route = exitMatch.Groups[2].Value.Trim();
            string rtt = exitMatch.Groups[3].Value.Trim();
            TelemetryUpdated?.Invoke(new ExitNodeTelemetry(ip, route, rtt));
        }

        // Check for SOCKS5 or HTTP listener readiness
        if (line.Contains("socks5 server listening", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("http proxy listening", StringComparison.OrdinalIgnoreCase))
        {
            SocksServerReady?.Invoke();
        }

        // Check for forced or detected peer IP
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
