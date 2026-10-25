using System;
using System.Diagnostics;
using System.IO;
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
        private const string ReadyTrigger = "socks5 server listening";

        [GeneratedRegex(@"masque exit:\s*([^,]+),\s*([^,]+),\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex MasqueExitRegex();

        private readonly JobObjectManager _jobManager;
        private readonly object _syncLock = new();
        private Process? _process;
        private CancellationTokenSource? _cts;
        private bool _isStopping;

        public event Action<string>? LogReceived;
        public event Action? SocksServerReady;
        public event Action<ExitNodeTelemetry>? TelemetryUpdated;
        public event Action<int>? ProcessExitedUnexpectedly;

        public bool IsRunning
        {
            get
            {
                lock (_syncLock)
                {
                    return _process is { HasExited: false };
                }
            }
        }

        public AetherProcessService(JobObjectManager jobManager)
        {
            _jobManager = jobManager;
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

        public Task StartAsync(bool forceFreshScan = false)
        {
            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ExecutableName);
            if (!File.Exists(exePath))
                throw new FileNotFoundException($"Backend core binary '{ExecutableName}' was not found in {AppDomain.CurrentDomain.BaseDirectory}.", exePath);

            lock (_syncLock)
            {
                if (_process is { HasExited: false })
                    return Task.CompletedTask;

                _isStopping = false;
                _cts = new CancellationTokenSource();

                string scanFlag = forceFreshScan ? "--no-quick-reconnect" : "--quick-reconnect";
                string arguments = $"--masque --h2 -4 --turbo {scanFlag} --http-proxy 127.0.0.1:1820";

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

                var process = new Process
                {
                    StartInfo = psi,
                    EnableRaisingEvents = true
                };

                process.Exited += OnProcessExited;

                if (!process.Start())
                    throw new InvalidOperationException("Failed to start aether.exe process.");

                _jobManager.AssignProcess(process);
                _process = process;

                // Close stdin immediately so aether.exe never blocks on interactive prompts
                try { process.StandardInput.Close(); } catch { }

                _ = ConsumeStreamAsync(process.StandardOutput, _cts.Token);
                _ = ConsumeStreamAsync(process.StandardError, _cts.Token);

                EmitLog($"[SYS] Launched {ExecutableName} {arguments} (PID: {process.Id})");
            }

            return Task.CompletedTask;
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
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                EmitLog($"[SYS-WARN] Stream reader error: {ex.Message}");
            }
        }

        private void ParseAndDispatchLine(string rawLine)
        {
            string line = rawLine.Trim();
            EmitLog(line);

            // Check for Masque Exit Node Telemetry
            var match = MasqueExitRegex().Match(line);
            if (match.Success)
            {
                string ip = match.Groups[1].Value.Trim();
                string route = match.Groups[2].Value.Trim();
                string rtt = match.Groups[3].Value.Trim();
                TelemetryUpdated?.Invoke(new ExitNodeTelemetry(ip, route, rtt));
            }

            // Check for SOCKS5 Ready state
            if (line.Contains(ReadyTrigger, StringComparison.OrdinalIgnoreCase))
            {
                SocksServerReady?.Invoke();
            }
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            int exitCode = -1;
            if (sender is Process proc)
            {
                try { exitCode = proc.ExitCode; } catch { }
            }

            bool wasUnexpected;
            lock (_syncLock)
            {
                wasUnexpected = !_isStopping;
            }

            if (wasUnexpected)
            {
                EmitLog($"[SYS-ERR] aether.exe terminated unexpectedly (Exit Code: {exitCode}).");
                ProcessExitedUnexpectedly?.Invoke(exitCode);
            }
        }

        public void Stop()
        {
            Process? procToKill;
            CancellationTokenSource? ctsToCancel;

            lock (_syncLock)
            {
                _isStopping = true;
                procToKill = _process;
                _process = null;
                ctsToCancel = _cts;
                _cts = null;
            }

            try { ctsToCancel?.Cancel(); } catch { }
            try { ctsToCancel?.Dispose(); } catch { }

            if (procToKill is not null)
            {
                try
                {
                    procToKill.Exited -= OnProcessExited;
                    if (!procToKill.HasExited)
                    {
                        procToKill.Kill(entireProcessTree: true);
                        procToKill.WaitForExit(2000);
                        EmitLog("[SYS] aether.exe process tree terminated cleanly.");
                    }
                }
                catch (Exception ex)
                {
                    EmitLog($"[SYS-WARN] Error stopping aether.exe: {ex.Message}");
                }
                finally
                {
                    procToKill.Dispose();
                }
            }
        }

        private void EmitLog(string message)
        {
            LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
