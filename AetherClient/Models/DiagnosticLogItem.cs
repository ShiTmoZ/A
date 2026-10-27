using System;

namespace AetherClient.Models
{
    public sealed class DiagnosticLogItem
    {
        public string Timestamp { get; init; } = "";
        public string Tag { get; init; } = "";
        public string TagColor { get; init; } = "#8DB4F5";
        public string Message { get; init; } = "";

        public override string ToString()
        {
            if (string.IsNullOrEmpty(Tag))
                return $"[{Timestamp}] {Message}";
            return $"[{Timestamp}] [{Tag}] {Message}";
        }
    }
}
