namespace SplitSpend.Gateway.Configuration;

public sealed class JwtSettings
{
    public string Issuer    { get; init; } = string.Empty;
    public string Audience  { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
}

public sealed class ConsulSettings
{
    public string Host                          { get; init; } = "http://localhost:8500";
    public string ServiceName                   { get; init; } = "api-gateway";
    public int    ServicePort                   { get; init; } = 5000;
    public string HealthCheckPath               { get; init; } = "/health";
    public string HealthCheckInterval           { get; init; } = "10s";
    public string HealthCheckTimeout            { get; init; } = "5s";
    public string DeregisterCriticalServiceAfter { get; init; } = "30s";
}

public sealed class OpenTelemetrySettings
{
    public string ServiceName                   { get; init; } = "SplitSpend.Gateway";
    public string ServiceVersion                { get; init; } = "1.0.0";
    public string OtlpEndpoint                  { get; init; } = "http://localhost:4317";
    public string AzureMonitorConnectionString  { get; init; } = string.Empty;
}

public sealed class SeqSettings
{
    public string ServerUrl { get; init; } = "http://localhost:5341";
}

public sealed class ResilienceSettings
{
    public CircuitBreakerSettings CircuitBreaker { get; init; } = new();
    public RetrySettings          Retry          { get; init; } = new();
    public int                    TimeoutSeconds  { get; init; } = 30;
}

public sealed class CircuitBreakerSettings
{
    public int FailureThreshold      { get; init; } = 5;
    public int SamplingDurationSeconds { get; init; } = 30;
    public int MinimumThroughput     { get; init; } = 10;
    public int BreakDurationSeconds  { get; init; } = 15;
}

public sealed class RetrySettings
{
    public int MaxAttempts { get; init; } = 3;
    public int DelayMs     { get; init; } = 200;
}

public sealed class RateLimitingSettings
{
    public RateLimitPolicy GlobalIpLimit          { get; init; } = new(100,  60);
    public RateLimitPolicy AuthenticatedUserLimit { get; init; } = new(300,  60);
    public RateLimitPolicy AuthEndpointLimit      { get; init; } = new(5,    60);
    public RateLimitPolicy PaymentEndpointLimit   { get; init; } = new(10,   60);
    public RateLimitPolicy TransferEndpointLimit  { get; init; } = new(5,    60);
    public RateLimitPolicy VendorPayEndpointLimit { get; init; } = new(20,   60);
}

public sealed record RateLimitPolicy(int PermitLimit, int WindowSeconds);
