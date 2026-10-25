namespace AetherClient.Models
{
    public sealed record ExitNodeTelemetry(
        string IpAddress,
        string Route,
        string Rtt
    )
    {
        public static ExitNodeTelemetry Empty => new("—", "—", "—");
    }
}
