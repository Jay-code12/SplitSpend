# SplitSpend API Gateway

ASP.NET Core 8 API Gateway using **Ocelot** for request proxying, **Consul** for service discovery and load balancing, **OpenTelemetry** for distributed tracing, **Serilog** for structured logging, and **Polly** for resilience.

---

## Architecture

```
Client (Angular / MAUI)
        │
        ▼
┌─────────────────────────────────────────────────────────────┐
│                    API Gateway :5000                         │
│                                                             │
│  Pipeline (in order):                                       │
│  1. GlobalExceptionMiddleware   — structured error envelope │
│  2. RequestTimingMiddleware      — measures elapsed ms      │
│  3. CorrelationIdMiddleware      — X-Correlation-Id + OTel  │
│  4. RateLimiter                  — 6 policies (per MVP doc) │
│  5. Authentication/Authorization — JWT validation           │
│  6. JwtAuthMiddleware            — extracts UserId + Role   │
│  7. PinGuardMiddleware           — /api/transfers/* guard   │
│  8. WalletOwnershipMiddleware    — /api/wallets/{id} guard  │
│  9. Health checks                — /health /health/ready    │
│  10. AggregationController       — /api/dashboard, /detail  │
│  11. Ocelot                      — proxy all other routes   │
└─────────────────────────────────────────────────────────────┘
        │  Consul service discovery (LeastConnection LB)
        ▼
┌──────────────────────────────────────────────────────────┐
│  auth-service      :5001   wallet-service      :5003     │
│  user-service      :5002   budget-service      :5004     │
│  transfer-service  :5005   transaction-service :5006     │
│  payment-service   :5007   vendor-pay-service  :5008     │
│  notification-service :5009                              │
└──────────────────────────────────────────────────────────┘
```

---

## Prerequisites

| Tool            | Version  | Purpose                    |
|-----------------|----------|----------------------------|
| .NET SDK        | 8.0+     | Build and run              |
| Consul          | 1.17+    | Service discovery          |
| Seq             | Latest   | Centralised log server     |
| OTLP collector  | Any      | Trace collection (optional)|

---

## Quick Start

### 1. Start Consul (Docker)
```bash
docker run -d --name consul \
  -p 8500:8500 \
  hashicorp/consul:latest agent -dev -client=0.0.0.0
```

### 2. Start Seq (Docker)
```bash
docker run -d --name seq \
  -p 5341:80 \
  -e ACCEPT_EULA=Y \
  datalust/seq:latest
```

### 3. Set secrets (never commit real values)
```bash
# PowerShell
$env:Jwt__SecretKey = "your-256-bit-secret-key-here"
$env:OpenTelemetry__AzureMonitorConnectionString = "InstrumentationKey=..."

# Bash
export Jwt__SecretKey="your-256-bit-secret-key-here"
export OpenTelemetry__AzureMonitorConnectionString="InstrumentationKey=..."
```

### 4. Run the gateway
```bash
cd src
dotnet run
```

Gateway starts on **http://localhost:5000**

---

## Route Map

| Client Route Pattern         | Proxied To              | Auth        | Rate Limit    |
|------------------------------|-------------------------|-------------|---------------|
| `POST /api/auth/**`          | auth-service :5001      | Public      | 5/60s per IP  |
| `GET /api/users/**`          | user-service :5002      | Bearer JWT  | 300/60s       |
| `* /api/wallets/**`          | wallet-service :5003    | Bearer JWT  | 300/60s       |
| `* /api/budgets/**`          | budget-service :5004    | Bearer JWT  | 300/60s       |
| `POST /api/transfers/webhook`| transfer-service :5005  | HMAC only   | —             |
| `* /api/transfers/**`        | transfer-service :5005  | JWT + PIN   | 5/60s         |
| `* /api/transactions/**`     | transaction-service:5006| Bearer JWT  | 300/60s       |
| `POST /api/payments/webhook` | payment-service :5007   | HMAC only   | —             |
| `* /api/payments/**`         | payment-service :5007   | Bearer JWT  | 10/60s        |
| `* /api/vendor-pay/**`       | vendor-pay-service:5008 | Bearer JWT  | 20/60s        |
| `GET /api/notifications/**`  | notification-service:5009| Bearer JWT | 300/60s       |
| `GET /api/dashboard/{userId}`| **Aggregated** (gateway-owned) | Bearer JWT | 300/60s |
| `GET /api/vendor-pay/{id}/detail` | **Aggregated** (gateway-owned) | Bearer JWT | 20/60s |

---

## Aggregated Endpoints

### `GET /api/dashboard/{userId}`
Fans out 3 parallel calls (Wallet + Budget + Transaction) and merges into one response.

```json
{
  "wallet":       { "mainBalance": 25000, "budgetBalance": 8000, "currency": "NGN" },
  "budget":       { "dailyLimit": 2000, "dailyRemaining": 1200, "dailySpent": 800, "hasActiveBudget": true },
  "transactions": [ ...last 5 transactions... ],
  "traceId":      "abc123"
}
```

### `GET /api/vendor-pay/{id}/detail`
Fans out 3 calls (VendorPay + User profile + Wallet balance) for the payment approval screen.

---

## Headers

| Header              | Direction  | Purpose                                      |
|---------------------|------------|----------------------------------------------|
| `X-Correlation-Id`  | In + Out   | Client-supplied or auto-generated trace ID   |
| `X-Trace-Id`        | Out        | OTel TraceId (W3C format)                    |
| `X-User-Id`         | Downstream | Authenticated UserId stamped by gateway      |
| `X-User-Role`       | Downstream | Authenticated Role stamped by gateway        |
| `X-Pin-Hash`        | In         | Required on all /api/transfers/* requests    |
| `X-Gateway-Version` | Out        | Gateway version for debugging                |

---

## OpenTelemetry & Distributed Tracing

Every request creates a root span in the gateway. Child spans are created for:
- Each inbound ASP.NET Core request
- Each outbound HttpClient call (aggregator fan-outs + downstream proxied calls)

The **W3C `traceparent` header** is automatically injected by `OpenTelemetry.Instrumentation.Http` on every outbound call. Any downstream service that is also OpenTelemetry-instrumented will continue the same trace — no manual work needed.

The **CorrelationId** is stamped as a span tag (`correlation.id`) and as OTel baggage so it appears alongside the TraceId in every downstream service's spans and logs.

### Exporters
| Exporter       | Endpoint                  | Used For                         |
|----------------|---------------------------|----------------------------------|
| OTLP           | `http://localhost:4317`   | Local dev (Jaeger / Grafana Tempo)|
| Azure Monitor  | Application Insights conn string | Production dashboards    |

### Seq Queries
```
# All errors for a specific trace
@Level = "Error" and CorrelationId = "abc123"

# Slow requests (> 2s)
ElapsedMs > 2000 and ServiceName = "SplitSpend.Gateway"

# All transfer route activity for a user
UserId = "user-uuid" and RequestPath like "/api/transfers/%"

# Circuit breaker trips
@Level = "Warning" and @Message like "%circuit%"
```

---

## Resilience

All aggregator HttpClient calls are protected by a Polly pipeline (configured in `appsettings.json`):

| Layer           | Default Config                                      |
|-----------------|-----------------------------------------------------|
| Timeout         | 30 s per attempt                                    |
| Retry           | 3 attempts, 200 ms exponential back-off + jitter    |
| Circuit Breaker | Opens after 5 failures in 30 s; breaks for 15 s    |

Ocelot route-level QoS (`QoSOptions` in `ocelot.json`) also applies circuit breaking per upstream route independently.

---

## Health Endpoints

| Endpoint         | What it checks                          |
|------------------|-----------------------------------------|
| `GET /health`    | Liveness — gateway process is alive     |
| `GET /health/ready` | Readiness — Consul is reachable      |

---

## Security

| Control               | Implementation                                                       |
|-----------------------|----------------------------------------------------------------------|
| JWT validation        | `JwtAuthMiddleware` + Ocelot `AuthenticationProviderKey = "Bearer"` |
| PIN guard             | `PinGuardMiddleware` — rejects `/api/transfers/*` without `X-Pin-Hash` |
| Wallet ownership      | `WalletOwnershipMiddleware` — blocks cross-user wallet access       |
| Webhook auth          | Routes to `/webhook` have empty `AuthenticationProviderKey` — HMAC verified in the downstream service |
| Rate limiting         | 6 fixed-window policies — brute force + abuse protection           |

---

## Environment Variables (Production)

| Variable                                        | Description                          |
|-------------------------------------------------|--------------------------------------|
| `Jwt__SecretKey`                                | 256-bit JWT signing key              |
| `Consul__Host`                                  | Consul URL (e.g. http://consul:8500) |
| `OpenTelemetry__AzureMonitorConnectionString`   | Application Insights connection string|
| `Seq__ServerUrl`                                | Seq server URL                       |
| `ASPNETCORE_ENVIRONMENT`                        | Production / Staging / Development   |

---

## Project Structure

```
SplitSpend.Gateway/
└── src/
    ├── Program.cs                          # Bootstrap + full pipeline
    ├── SplitSpend.Gateway.csproj
    ├── appsettings.json                    # All configuration
    ├── ocelot.json                         # All route definitions
    ├── Configuration/
    │   └── GatewaySettings.cs             # Strongly-typed settings
    ├── Models/
    │   └── GatewayModels.cs               # Shared DTOs, constants, envelopes
    ├── Middleware/
    │   ├── CorrelationIdMiddleware.cs      # X-Correlation-Id + OTel enrichment
    │   ├── JwtAuthMiddleware.cs           # JWT validation + header stamping
    │   ├── PinGuardMiddleware.cs          # Transfer PIN enforcement
    │   ├── WalletOwnershipMiddleware.cs   # Wallet route ownership check
    │   ├── GlobalExceptionMiddleware.cs   # Structured error envelopes
    │   └── RequestTimingMiddleware.cs     # Elapsed time measurement
    ├── Services/
    │   └── ConsulService.cs               # Consul client, registration, resolution
    ├── Aggregators/
    │   ├── DashboardAggregator.cs         # Wallet + Budget + Transaction fan-out
    │   └── VendorPayDetailAggregator.cs   # VendorPay + User + Wallet fan-out
    ├── Controllers/
    │   └── AggregationController.cs       # /api/dashboard + /api/vendor-pay/detail
    └── Extensions/
        ├── OpenTelemetryExtensions.cs     # OTel setup (ASP.NET Core + HTTP + exporters)
        ├── SerilogExtensions.cs           # Serilog setup (file + Seq + enrichers)
        ├── RateLimitingExtensions.cs      # 6 rate limit policies
        ├── HttpClientExtensions.cs        # Aggregator HttpClient + Polly pipeline
        ├── ConsulExtensions.cs            # Consul DI registration
        └── HealthCheckExtensions.cs       # /health + /health/ready
```
