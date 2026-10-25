using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AetherClient.Models;

namespace AetherClient.Services;

public sealed class EdgePingService
{
    private const int DefaultTimeoutMs = 1200;
    private const int MaxConcurrentPings = 32;

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
    /// Measures raw TCP SYN-ACK handshake latency to an Anycast endpoint with a strict 1200ms timeout.
    /// </summary>
    public async Task<int> MeasureTcpLatencyAsync(
        string endpoint,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (!IPEndPoint.TryParse(endpoint, out IPEndPoint? ipEndPoint))
            return -1;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        using var socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            long startTick = Stopwatch.GetTimestamp();
            await socket.ConnectAsync(ipEndPoint, cts.Token).ConfigureAwait(false);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startTick);

            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // Ignore socket shutdown exceptions after handshake
            }

            return Math.Max(1, (int)Math.Round(elapsed.TotalMilliseconds));
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Pings all static and custom nodes in parallel via Task.WhenAll and returns the sorted list
    /// (Auto node first, then lowest latency reachable nodes ascending, then unreachable nodes).
    /// </summary>
    public async Task<List<EdgeNodeModel>> PingAllNodesAsync(
        IEnumerable<EdgeNodeModel> nodes,
        CancellationToken cancellationToken = default)
    {
        List<EdgeNodeModel> snapshot = nodes.ToList();
        EdgeNodeModel? autoNode = snapshot.FirstOrDefault(n => n.IsAuto);
        List<EdgeNodeModel> targetNodes = snapshot.Where(n => !n.IsAuto).ToList();

        await RunOnUiThreadAsync(() =>
        {
            if (autoNode is not null)
                autoNode.IsProbing = true;

            foreach (EdgeNodeModel node in targetNodes)
            {
                node.IsProbing = true;
            }
        });

        using var throttler = new SemaphoreSlim(MaxConcurrentPings);

        IEnumerable<Task> probeTasks = targetNodes.Select(async node =>
        {
            await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int rtt = await MeasureTcpLatencyAsync(node.Endpoint, DefaultTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);

                await RunOnUiThreadAsync(() =>
                {
                    node.LatencyMs = rtt;
                    node.HasBeenTested = true;
                    node.IsProbing = false;
                });
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(probeTasks).ConfigureAwait(false);

        List<EdgeNodeModel> sortedReachable = targetNodes
            .Where(n => n.LatencyMs > 0)
            .OrderBy(n => n.LatencyMs)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<EdgeNodeModel> unreachable = targetNodes
            .Where(n => n.LatencyMs <= 0)
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await RunOnUiThreadAsync(() =>
        {
            if (autoNode is not null)
            {
                autoNode.LatencyMs = sortedReachable.Count > 0 ? sortedReachable[0].LatencyMs : -1;
                autoNode.HasBeenTested = true;
                autoNode.IsProbing = false;
            }
        });

        var finalOrdered = new List<EdgeNodeModel>(snapshot.Count);
        if (autoNode is not null)
            finalOrdered.Add(autoNode);

        finalOrdered.AddRange(sortedReachable);
        finalOrdered.AddRange(unreachable);

        return finalOrdered;
    }

    private static Task RunOnUiThreadAsync(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
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
}
