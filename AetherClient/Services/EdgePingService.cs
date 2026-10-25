using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AetherClient.Models;

namespace AetherClient.Services;

public sealed class EdgePingService
{
    private const int DefaultTimeoutMs = 1800;
    private const int MaxConcurrentPings = 16;

    /// <summary>
    /// Builds the initial Clean IP pool from embedded Cloudflare Anycast edges
    /// and appends any custom user endpoints from clean_ips.txt beside the executable.
    /// </summary>
    public List<EdgeNodeModel> LoadInitialNodePool()
    {
        var nodes = new List<EdgeNodeModel>
        {
            new()
            {
                Name = "Auto Best (Turbo)",
                Endpoint = "Auto",
                RegionCode = "AUTO",
                IsAuto = true,
                IsSelected = true
            },
            // 162.159.198.x Range (Verified low latency on Iran TCI/MCI)
            new() { Name = "Frankfurt Alpha",   Endpoint = "162.159.198.4:443",   RegionCode = "DE" },
            new() { Name = "Frankfurt Bravo",   Endpoint = "162.159.198.65:443",  RegionCode = "DE" },
            new() { Name = "Frankfurt Core",    Endpoint = "162.159.198.72:443",  RegionCode = "DE" },
            new() { Name = "Amsterdam Direct",  Endpoint = "162.159.198.198:443", RegionCode = "NL" },
            new() { Name = "Amsterdam Edge",    Endpoint = "162.159.198.245:443", RegionCode = "NL" },

            // 162.159.192.x - 195.x Range
            new() { Name = "Stockholm Edge",    Endpoint = "162.159.192.1:443",   RegionCode = "SE" },
            new() { Name = "Stockholm North",   Endpoint = "162.159.192.10:443",  RegionCode = "SE" },
            new() { Name = "London Docklands",  Endpoint = "162.159.193.5:443",   RegionCode = "UK" },
            new() { Name = "London Prime",      Endpoint = "162.159.195.2:443",   RegionCode = "UK" },

            // 188.114.96.x - 99.x Range (Cloudflare Anycast Clean Subnets)
            new() { Name = "Frankfurt Mesh I",  Endpoint = "188.114.96.3:443",    RegionCode = "DE" },
            new() { Name = "Amsterdam Mesh II", Endpoint = "188.114.97.3:443",    RegionCode = "NL" },
            new() { Name = "Stockholm Mesh III",Endpoint = "188.114.98.2:443",    RegionCode = "SE" },
            new() { Name = "London Mesh IV",    Endpoint = "188.114.99.1:443",    RegionCode = "UK" }
        };

        LoadCustomUserIps(nodes);
        return nodes;
    }

    /// <summary>
    /// Checks for clean_ips.txt beside the executable.
    /// Supported line formats:
    ///   162.159.198.4:443
    ///   162.159.198.4
    ///   162.159.198.4:443|Custom Label|DE
    /// </summary>
    private static void LoadCustomUserIps(List<EdgeNodeModel> nodes)
    {
        try
        {
            string customFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clean_ips.txt");
            if (!File.Exists(customFilePath))
                return;

            var existingEndpoints = new HashSet<string>(
                nodes.Select(n => n.Endpoint),
                StringComparer.OrdinalIgnoreCase);

            string[] lines = File.ReadAllLines(customFilePath);
            int customIndex = 1;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//"))
                    continue;

                string[] parts = line.Split('|', StringSplitOptions.TrimEntries);
                string rawEndpoint = parts[0];

                if (!rawEndpoint.Contains(':'))
                    rawEndpoint = $"{rawEndpoint}:443";

                if (!IPEndPoint.TryParse(rawEndpoint, out _))
                    continue;

                if (!existingEndpoints.Add(rawEndpoint))
                    continue;

                string customName = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                    ? parts[1]
                    : $"Custom Clean #{customIndex++}";

                string region = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2])
                    ? parts[2].ToUpperInvariant()
                    : "USR";

                nodes.Add(new EdgeNodeModel
                {
                    Name = customName,
                    Endpoint = rawEndpoint,
                    RegionCode = region,
                    IsAuto = false
                });
            }
        }
        catch
        {
            // Ignore malformed external file errors to keep startup resilient
        }
    }

    /// <summary>
    /// Performs an authentic L7 TLS 1.3 handshake against the Anycast edge with SNI 'www.cloudflare.com'.
    /// Measures true handshake latency through DPI. If Iran DPI drops or resets the ClientHello,
    /// or if the node fails to negotiate, returns -1.
    /// </summary>
    public async Task<int> MeasureTlsConnectabilityAsync(
        string endpoint,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (!IPEndPoint.TryParse(endpoint, out IPEndPoint? ipEndPoint))
            return -1;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        using var tcpClient = new TcpClient
        {
            NoDelay = true
        };

        try
        {
            long startTick = Stopwatch.GetTimestamp();

            // 1. TCP 3-Way Handshake
            await tcpClient.ConnectAsync(ipEndPoint.Address, ipEndPoint.Port, cts.Token).ConfigureAwait(false);

            // 2. L7 TLS 1.3 Handshake (Authentic ClientHello probe against Iran DPI)
            using var sslStream = new SslStream(
                tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true);

            var sslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "www.cloudflare.com",
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    SslApplicationProtocol.Http2
                },
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12
            };

            await sslStream.AuthenticateAsClientAsync(sslOptions, cts.Token).ConfigureAwait(false);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startTick);

            return Math.Max(1, (int)Math.Round(elapsed.TotalMilliseconds));
        }
        catch
        {
            // DPI drop, RST, or handshake timeout
            return -1;
        }
    }

    /// <summary>
    /// Probes all nodes in the pool using real L7 TLS 1.3 connectability tests.
    /// Only nodes passing the TLS handshake through DPI are marked optimal.
    /// </summary>
    public async Task PingAllNodesAsync(
        IEnumerable<EdgeNodeModel> nodes,
        Action<EdgeNodeModel>? onNodeTested = null,
        CancellationToken cancellationToken = default)
    {
        var nodeArray = nodes.Where(n => !n.IsAuto).ToArray();
        var autoNode = nodes.FirstOrDefault(n => n.IsAuto);

        foreach (var node in nodeArray)
        {
            node.IsProbing = true;
        }

        using var throttler = new SemaphoreSlim(MaxConcurrentPings, MaxConcurrentPings);
        var tasks = nodeArray.Select(async node =>
        {
            await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                int latency = await MeasureTlsConnectabilityAsync(
                    node.Endpoint,
                    DefaultTimeoutMs,
                    cancellationToken).ConfigureAwait(false);

                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    node.LatencyMs = latency;
                    node.HasBeenTested = true;
                    node.IsProbing = false;
                    onNodeTested?.Invoke(node);
                });
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Update Auto node to best measured latency
        if (autoNode != null)
        {
            var bestTested = nodeArray.Where(n => n.HasBeenTested && n.LatencyMs > 0).ToList();
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                autoNode.LatencyMs = bestTested.Count > 0 ? bestTested.Min(n => n.LatencyMs) : -1;
                autoNode.HasBeenTested = true;
                autoNode.IsProbing = false;
                onNodeTested?.Invoke(autoNode);
            });
        }
    }

    /// <summary>
    /// Finds the best connected node with the lowest positive TLS latency.
    /// Returns the Auto node if no direct candidate is verified.
    /// </summary>
    public EdgeNodeModel? GetOptimalNode(IEnumerable<EdgeNodeModel> nodes)
    {
        var testedCandidates = nodes
            .Where(n => !n.IsAuto && n.HasBeenTested && n.LatencyMs > 0)
            .OrderBy(n => n.LatencyMs)
            .ToList();

        return testedCandidates.FirstOrDefault() ?? nodes.FirstOrDefault(n => n.IsAuto);
    }
}
