namespace SplitSpend.AuthService.Settings
{
    public sealed class OpenTelemetrySettings
    {
        public required string ServiceName { get; set; }
        public required string ServiceVersion { get; set; }
        public required string OtlpEndpoint { get; set; }
        public string? AzureMonitorConnectionString { get; set; } 
    }
}
