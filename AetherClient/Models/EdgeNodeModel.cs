using CommunityToolkit.Mvvm.ComponentModel;

namespace AetherClient.Models;

public enum LatencyQuality
{
    Auto,
    Pending,
    Probing,
    Optimal,   // < 180ms (Emerald #10B981)
    Moderate,  // 180ms - 280ms (Amber #F59E0B)
    Degraded,  // > 280ms (Crimson #F43F5E)
    Timeout    // Unreachable (Crimson #F43F5E)
}

public partial class EdgeNodeModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IpOnly))]
    private string _endpoint = string.Empty;

    [ObservableProperty]
    private string _regionCode = "AUTO";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyDisplay))]
    [NotifyPropertyChangedFor(nameof(QualityTier))]
    private int _latencyMs = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyDisplay))]
    [NotifyPropertyChangedFor(nameof(QualityTier))]
    private bool _isAuto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyDisplay))]
    [NotifyPropertyChangedFor(nameof(QualityTier))]
    private bool _isProbing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyDisplay))]
    [NotifyPropertyChangedFor(nameof(QualityTier))]
    private bool _hasBeenTested;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Returns the raw IPv4/IPv6 host without port for TUN routing bypass.
    /// </summary>
    public string? IpOnly
    {
        get
        {
            if (IsAuto || string.IsNullOrWhiteSpace(Endpoint))
                return null;

            int colonIndex = Endpoint.LastIndexOf(':');
            return colonIndex > 0 ? Endpoint[..colonIndex] : Endpoint;
        }
    }

    public string LatencyDisplay
    {
        get
        {
            if (IsProbing)
                return "SCANNING";

            if (IsAuto)
                return LatencyMs > 0 ? $"BEST ({LatencyMs} ms)" : "TURBO AUTO";

            if (!HasBeenTested)
                return "READY";

            return LatencyMs > 0 ? $"{LatencyMs} ms" : "Timeout";
        }
    }

    public LatencyQuality QualityTier
    {
        get
        {
            if (IsProbing)
                return LatencyQuality.Probing;

            if (IsAuto)
                return LatencyQuality.Auto;

            if (!HasBeenTested)
                return LatencyQuality.Pending;

            if (LatencyMs <= 0)
                return LatencyQuality.Timeout;

            if (LatencyMs < 180)
                return LatencyQuality.Optimal;

            if (LatencyMs <= 280)
                return LatencyQuality.Moderate;

            return LatencyQuality.Degraded;
        }
    }
}
