# SplitSpend — Wallet Service

**The single source of financial truth.** Every balance change in SplitSpend goes through here.

---

## What it does

| Responsibility | Detail |
|---|---|
| **Balance ownership** | Sole owner of `MainBalance` and `BudgetBalance` per user |
| **Budget-first debit** | Uses BudgetBalance first; falls back to MainBalance automatically |
| **Atomic in-platform pay** | Debits payer + credits recipient in one DB transaction — no Paystack, instant |
| **Internal transfers** | Main ↔ Budget moves for budget creation, daily expiry, cancellations |
| **External transfer pre-debit** | Pre-debits MainBalance; emits `wallet.main.transfer.initiated` for Transfer Service |
| **Reversal** | Credits MainBalance back when an external transfer fails |
| **Idempotency** | Every write checks a unique key before touching the ledger — no double-processing |
| **Audit ledger** | Immutable before/after snapshots for every money movement |

---

## Project structure

```
src/WalletService/
├── Controllers/
│   └── WalletController.cs          REST API (5 endpoints)
├── Domain/
│   ├── Entities/
│   │   ├── Wallet.cs                Core aggregate — all balance mutation logic lives here
│   │   ├── WalletLedger.cs          Immutable audit record per balance change
│   │   └── Exceptions.cs            Domain exceptions
│   └── Enums/
│       └── Enums.cs
├── Application/
│   ├── DTOs/Dtos.cs                 Request/response contracts
│   ├── Events/
│   │   ├── Events.cs                Inbound + outbound Kafka message contracts
│   │   └── KafkaTopics.cs           All topic name constants
│   ├── Interfaces/                  Repository + publisher contracts
│   └── Services/
│       └── WalletApplicationService.cs   Core orchestration logic
├── Infrastructure/
│   ├── Data/WalletDbContext.cs      EF Core context + entity config
│   ├── Repositories/Repositories.cs
│   ├── Messaging/
│   │   ├── KafkaPublisher.cs        Idempotent Kafka producer
│   │   └── KafkaConsumerBase.cs     Retry + manual-commit consumer base
│   └── ConsulRegistration.cs        Startup + shutdown Consul registration
├── Consumers/
│   └── Consumers.cs                 7 background Kafka consumers
├── Middleware/
│   └── ExceptionHandlerMiddleware.cs
├── Migrations/
│   └── 20250101000000_InitialCreate.cs
├── Program.cs                       DI wiring, Serilog, OTel, health checks
├── appsettings.json
└── appsettings.Development.json

tests/WalletService.Tests/
└── WalletDomainTests.cs             16 unit tests for domain logic
```

---

## REST endpoints

| Method | Route | Description |
|--------|-------|-------------|
| `GET`  | `/api/wallets/{userId}` | Get MainBalance + BudgetBalance |
| `POST` | `/api/wallets/credit` | Credit a balance (idempotency enforced) |
| `POST` | `/api/wallets/debit` | Budget-first debit; emits typed events |
| `POST` | `/api/wallets/pay` | Atomic in-platform payment |
| `POST` | `/api/wallets/internal-transfer` | Main ↔ Budget internal move |
| `GET`  | `/api/wallets/{userId}/ledger` | Cursor-paginated ledger history |
| `GET`  | `/health` | Consul + load balancer health check |

---

## Kafka events

### Produced

| Event | When |
|-------|------|
| `wallet.credited` | Any credit (deposit, payment received, refund, reversal) |
| `wallet.budget.debited` | Budget balance used in a spend |
| `wallet.main.debited` | Main balance used (fallback or external transfer pre-debit) |
| `wallet.budget.transfer.completed` | Main→Budget move succeeded (activates budget) |
| `wallet.budget.transfer.failed` | Main→Budget move failed (marks budget Failed) |
| `wallet.main.transfer.initiated` | External transfer pre-debit done; Transfer Service may proceed |
| `wallet.insufficient_funds` | All balances exhausted |

### Consumed

| Event | Action |
|-------|--------|
| `vendor.payment.approved` | Atomic pay: debit payer + credit recipient |
| `payment.successful` | Credit MainBalance (Paystack deposit landed) |
| `budget.created` | Transfer Main → Budget to fund the new budget |
| `budget.daily.expired` | Return unused daily budget to Main |
| `gift.sent` | Debit sender Main, credit receiver Budget |
| `budget.cancelled` | Return remaining budget balance to Main |
| `transfer.failed` | Reverse the external transfer pre-debit |

---

## Getting started

### 1. Start dependencies

```bash
docker compose up -d
```

Starts: SQL Server · Kafka · Seq (logs at http://localhost:5341) · Consul (UI at http://localhost:8500)

### 2. Run

```bash
cd src/WalletService
dotnet run
```

Swagger UI: http://localhost:5003/swagger

### 3. Run tests

```bash
cd tests/WalletService.Tests
dotnet test
```

---

## Key design decisions

**Why `Serializable` isolation on debits?**
Prevents two concurrent requests from both reading a sufficient balance and both succeeding, leaving the wallet negative. The pessimistic read lock ensures balance is re-checked inside the lock before commit.

**Why split `wallet.budget.debited` from `wallet.main.debited`?**
Budget Service subscribes only to `wallet.budget.debited` to update daily spend tracking. If a single `wallet.debited` event were used, Budget Service would also react to external bank transfer pre-debits — corrupting spend records. Separate events mean zero filtering logic inside consumers.

**Why are in-platform payments handled here, not via Paystack?**
Both payer and recipient are already SplitSpend users. Routing through Paystack adds latency, fees, and failure points. A single atomic DB transaction (debit + credit) settles instantly with no external dependency.

**Why idempotency keys on every write?**
Kafka at-least-once delivery means a consumer can process the same message twice. The `IdempotencyRecords` table with a unique constraint on the key ensures a duplicate message returns the original result instead of double-processing a financial operation.
