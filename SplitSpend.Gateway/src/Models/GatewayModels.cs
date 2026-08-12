namespace SplitSpend.Gateway.Models;

// ── Request context propagated through the pipeline ──────────────────────────
public sealed class GatewayRequestContext
{
    public string TraceId        { get; init; } = string.Empty;
    public string CorrelationId  { get; init; } = string.Empty;
    public string? UserId        { get; set;  }
    public string? UserRole      { get; set;  }
    public string RequestPath    { get; init; } = string.Empty;
    public DateTime RequestedAt  { get; init; } = DateTime.UtcNow;
}

// ── Aggregator response models ────────────────────────────────────────────────
public sealed class DashboardResponse
{
    public WalletSummary   Wallet      { get; set; } = new();
    public BudgetSummary   Budget      { get; set; } = new();
    public List<object>    Transactions { get; set; } = new();
    public string          TraceId     { get; set; } = string.Empty;
}

public sealed class WalletSummary
{
    public decimal MainBalance   { get; set; }
    public decimal BudgetBalance { get; set; }
    public string  Currency      { get; set; } = "NGN";
}

public sealed class BudgetSummary
{
    public decimal DailyLimit     { get; set; }
    public decimal DailyRemaining { get; set; }
    public decimal DailySpent     { get; set; }
    public bool    HasActiveBudget { get; set; }
}

public sealed class VendorPayDetailResponse
{
    public object? PaymentRequest  { get; set; }
    public object? VendorProfile   { get; set; }
    public object? BuyerBalance    { get; set; }
    public string  TraceId         { get; set; } = string.Empty;
}

// ── Standard error envelope ───────────────────────────────────────────────────
public sealed class GatewayErrorResponse
{
    public string  TraceId  { get; init; } = string.Empty;
    public int     Status   { get; init; }
    public string  Error    { get; init; } = string.Empty;
    public string  Message  { get; init; } = string.Empty;
    public DateTime At      { get; init; } = DateTime.UtcNow;
}

// ── Consul service entry ──────────────────────────────────────────────────────
public sealed class ConsulServiceEntry
{
    public string ServiceName { get; init; } = string.Empty;
    public string Address     { get; init; } = string.Empty;
    public int    Port        { get; init; }
    public string HealthUrl   => $"http://{Address}:{Port}/health";
    public string BaseUrl     => $"http://{Address}:{Port}";
}

// ── Rate limit policy names ───────────────────────────────────────────────────
public static class RateLimitPolicies
{
    public const string GlobalIp          = "global-ip";
    public const string AuthenticatedUser = "authenticated-user";
    public const string AuthEndpoint      = "auth-endpoint";
    public const string PaymentEndpoint   = "payment-endpoint";
    public const string TransferEndpoint  = "transfer-endpoint";
    public const string VendorPayEndpoint = "vendor-pay-endpoint";
}

// ── Header names ──────────────────────────────────────────────────────────────
public static class GatewayHeaders
{
    public const string CorrelationId = "X-Correlation-Id";
    public const string TraceId       = "X-Trace-Id";
    public const string UserId        = "X-User-Id";
    public const string UserRole      = "X-User-Role";
    public const string RequestedAt   = "X-Requested-At";
    public const string GatewayVersion = "X-Gateway-Version";
    public const string PinHash       = "X-Pin-Hash";
}
