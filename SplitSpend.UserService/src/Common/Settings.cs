namespace SplitSpend.UserService.Common;

public sealed class ConsulSettings
{
    public string Host                           { get; init; } = "http://localhost:8500";
    public string ServiceName                    { get; init; } = "user-service";
    public int    ServicePort                    { get; init; } = 5002;
    public string HealthCheckInterval            { get; init; } = "10s";
    public string HealthCheckTimeout             { get; init; } = "5s";
    public string DeregisterCriticalServiceAfter { get; init; } = "30s";
}

public sealed class KafkaSettings
{
    public string BootstrapServers    { get; init; } = "localhost:9092";
    public string GroupId             { get; init; } = "user-service-group";
    public string UserRegisteredTopic { get; init; } = "user.registered";  // consumed
    public string UserCreatedTopic    { get; init; } = "user.created";      // produced
    public string UserUpdatedTopic    { get; init; } = "user.updated";      // produced
    public string UserDeletedTopic    { get; init; } = "user.deleted";      // produced
}

public sealed class OpenTelemetrySettings
{
    public string ServiceName                  { get; init; } = "SplitSpend.UserService";
    public string ServiceVersion               { get; init; } = "1.0.0";
    public string OtlpEndpoint                 { get; init; } = "http://localhost:4317";
    public string AzureMonitorConnectionString { get; init; } = string.Empty;
}

public sealed class SeqSettings
{
    public string ServerUrl { get; init; } = "http://localhost:5341";
}

public sealed class JwtSettings
{
    public string Issuer    { get; init; } = string.Empty;
    public string Audience  { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
}

public sealed class UserException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
