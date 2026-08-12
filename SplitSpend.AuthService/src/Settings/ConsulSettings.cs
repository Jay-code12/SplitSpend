namespace SplitSpend.AuthService.Settings
{
    public sealed class ConsulSettings
    {
        public required string Host { get; set; }
        public required string ServiceName { get; set; }
        public int ServicePort { get; set; }
        public required TimeSpan HealthCheckInterval { get; set; }
        public required TimeSpan HealthCheckTimeout { get; set; }
        public required TimeSpan DeregisterCriticalServiceAfter { get; set; }
    }
}
