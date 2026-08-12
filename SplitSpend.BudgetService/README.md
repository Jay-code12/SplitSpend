# SplitSpend — Budget Service

**Core business logic for daily spending discipline.**
Defines spending rules, tracks daily allocations, runs CRON jobs, and coordinates gift budgets.
Does not move money directly — orchestrates via Kafka events that Wallet Service acts on.

---

## What it does

| Responsibility | Detail |
|---|---|
| **Budget creation** | Validates balance via sync REST call to Wallet Service, creates Budget (Pending), emits `budget.created` |
| **Budget activation** | Listens for `wallet.budget.transfer.completed`, activates the budget, emits `budget.activated` |
| **Spend tracking (FIFO)** | Listens for `wallet.budget.debited`, distributes spend across active budgets oldest-first |
| **Daily release CRON** | 00:01 UTC: allocates each active budget's daily amount, emits `budget.daily.released` |
| **Daily expiry CRON** | 23:55 UTC: returns unused daily funds to Main Balance via `budget.daily.expired` |
| **Gift budgets** | Sender-funded budget for another user — cannot be cancelled by receiver |
| **Cancellation** | Returns remaining funds to Main Balance via `budget.cancelled` |
| **Idempotency** | Every write and CRON run is keyed — safe to retry, Kafka at-least-once safe |

---

## Project structure

```
src/BudgetService/
├── Controllers/
│   └── BudgetController.cs          6 endpoints (create, daily summary, get, cancel, gift, cron trigger)
├── Domain/
│   ├── Entities/
│   │   ├── Budget.cs                Core aggregate — all state transitions + FIFO deduction
│   │   ├── DailyBudget.cs           UserTotalDailyBudget + DailyBudgetRecord per-budget per-day
│   │   ├── GiftBudget.cs            Gift tracking entity
│   │   └── Exceptions.cs            Domain exceptions
│   └── Enums/
│       └── Enums.cs                 BudgetStatus, BudgetSource, GiftStatus
├── Application/
│   ├── DTOs/Dtos.cs                 Request/response contracts
│   ├── Events/Events.cs             All inbound + outbound Kafka contracts + topic constants
│   ├── Interfaces/                  Repository + publisher + wallet client contracts
│   └── Services/
│       ├── BudgetApplicationService.cs   Core orchestration (create, cancel, gift, spend tracking)
│       └── DailyCronService.cs           Daily release + expiry CRON logic
├── Infrastructure/
│   ├── Data/BudgetDbContext.cs      EF Core — 5 tables
│   ├── Repositories/Repositories.cs 4 repositories (Budget, Daily, Gift, Idempotency)
│   ├── Messaging/Kafka.cs           Publisher + KafkaConsumerBase<T>
│   ├── WalletServiceClient.cs       Typed HTTP client with Polly retry + circuit breaker
│   └── ConsulRegistration.cs
├── Consumers/
│   └── Consumers.cs                 3 Kafka consumers
├── BackgroundJobs/
│   └── HangfireJobRegistrar.cs      Recurring job registration (00:01 + 23:55 UTC)
├── Middleware/
│   └── ExceptionHandlerMiddleware.cs
├── Migrations/
│   └── 20250101000000_InitialCreate.cs
├── Program.cs
├── appsettings.json
└── appsettings.Development.json

tests/BudgetService.Tests/
└── BudgetDomainTests.cs             30 domain tests across 5 test classes
```

---

## REST endpoints

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| `POST`  | `/api/budgets/{userId}` | User | Create new budget |
| `GET`   | `/api/budgets/{userId}/daily` | User | Today's spend summary + per-budget breakdown |
| `GET`   | `/api/budgets/{id}?userId=` | User | Single budget detail |
| `POST`  | `/api/budgets/{id}/cancel` | User | Cancel budget; triggers refund to Main |
| `POST`  | `/api/budgets/gift` | User | Send gift budget to another user |
| `GET`   | `/api/budgets/daily-end-start` | Admin | Manually trigger CRON cycle (recovery) |
| `GET`   | `/health` | Public | Health check |
| `GET`   | `/hangfire` | Admin | Hangfire dashboard |

---

## Kafka events

### Produced

| Topic | When |
|-------|------|
| `budget.created` | Budget record created (Pending) — triggers Wallet fund transfer |
| `budget.activated` | Wallet confirmed transfer; budget is live |
| `budget.daily.released` | CRON 00:01 UTC — daily funds allocated |
| `budget.daily.expired` | CRON 23:55 UTC — unused daily funds returned to Main |
| `budget.completed` | All funds consumed OR end date passed |
| `budget.cancelled` | User cancelled; remaining returned to Main |
| `gift.sent` | Gift dispatched — triggers Wallet debit sender + credit receiver |

### Consumed

| Topic | Consumer | Action |
|-------|----------|--------|
| `wallet.budget.transfer.completed` | `WalletBudgetTransferCompletedConsumer` | Activate pending budget |
| `wallet.budget.transfer.failed` | `WalletBudgetTransferFailedConsumer` | Mark budget Failed |
| `wallet.budget.debited` | `WalletBudgetDebitedConsumer` | FIFO spend distribution |

> **Why only `wallet.budget.debited` and never `wallet.main.debited`?**
> Budget spend tracking must only react to budget balance deductions — not external bank transfers
> or other Main Balance debits. Subscribing to the correct typed event means zero filtering logic
> inside the consumer. This is the entire reason the two debit events exist as separate Kafka topics.

---

## CRON schedule

| Job | Cron | Time (UTC) | What it does |
|-----|------|-----------|--------------|
| `daily-budget-release` | `1 0 * * *` | 00:01 | Creates daily records, emits `budget.daily.released` per user |
| `daily-budget-expiry` | `55 23 * * *` | 23:55 | Calculates unused amounts, emits `budget.daily.expired`, auto-completes ended budgets |

**MVP Alert:** If `daily-budget-release` does not fire by 06:10 AM UTC, Application Insights fires
a Critical alert. The `/api/budgets/daily-end-start` endpoint (Admin role) exists for manual recovery.

---

## Getting started

### 1. Start dependencies

```bash
docker compose up -d
```

Starts: SQL Server · Kafka · Seq (http://localhost:5341) · Consul (http://localhost:8500) · Wallet Service

### 2. Run

```bash
cd src/BudgetService
dotnet run
```

- Swagger UI: http://localhost:5004/swagger
- Hangfire dashboard: http://localhost:5004/hangfire (Admin role required in prod, open in dev)

### 3. Test

```bash
cd tests/BudgetService.Tests
dotnet test
```

---

## Key design decisions

**Why Pending → Active two-step creation?**
Budget creation requires a fund transfer from Main to Budget balance. That transfer is async
(via Kafka). Creating the budget immediately as Active would mean it's live before money has moved.
Pending state ensures the budget only activates after the wallet confirms funds are in place.

**Why FIFO spend distribution?**
Users may have overlapping budgets (e.g. a month-long budget started Jan 1 and a 2-week budget
started Jan 8). When a spend arrives, it should consume the oldest budget first — the one
closest to expiry — to maximise the chance that newer budgets survive to their intended end date.

**Why Hangfire over a simple `IHostedService` timer?**
Hangfire persists job state to SQL Server. If the service restarts mid-job, Hangfire retries
it. A raw timer silently drops the job on restart. Hangfire also provides the dashboard for
monitoring job history — critical for the "CRON did not fire" alert in the MVP spec.

**Why a sync REST call to Wallet Service before creation?**
The budget creation endpoint must give the user immediate feedback if they don't have enough
balance — they shouldn't submit a budget and find out minutes later via notification that it
failed. The sync check provides instant validation. Idempotency on the Wallet transfer covers
the rare case where the user's balance changes between check and transfer execution.
