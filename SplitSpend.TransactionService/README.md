# SplitSpend — Transaction Service

**Lifecycle coordinator for every money movement on the platform.**
Observes Kafka events from Wallet, Payment, Vendor Pay, and Transfer services
and advances a unified transaction state machine. Never moves money itself.

---

## What it does

| Responsibility | Detail |
|---|---|
| **Deposit tracking** | `payment.successful` → Pending; `wallet.credited` → Completed; `payment.failed` → Failed |
| **In-platform payment tracking** | `vendor.payment.approved` → Pending; debit events → Processing; `wallet.credited` (recipient) → Completed |
| **External transfer tracking** | `transfer.created` → Pending; `transfer.completed` → Completed; `transfer.failed` → Failed |
| **Debit attribution** | Records whether Budget or Main balance was used, and how much of each |
| **Idempotency** | Every handler is keyed — safe for Kafka at-least-once redelivery |
| **History API** | Cursor-paginated, filterable by type, status, and date range |
| **Outbound events** | Emits `transaction.created/completed/failed` for Notification and Vendor Pay Services |

---

## State machines

### Deposit
```
payment.successful → [Pending] → wallet.credited → [Completed]
                                → payment.failed  → [Failed]
```

### InPlatformPayment
```
vendor.payment.approved   → [Pending]
wallet.budget.debited
  and/or wallet.main.debited → [Processing]  ← records DebitSource, amounts
wallet.credited (recipient) → [Completed]
wallet.insufficient_funds  → [Failed]
```

### ExternalTransfer
```
transfer.created   → [Pending]
transfer.completed → [Completed]
transfer.failed    → [Failed]
```

---

## Project structure

```
src/TransactionService/
├── Controllers/
│   └── TransactionController.cs     2 endpoints (list + detail)
├── Domain/
│   ├── Entities/
│   │   ├── Transaction.cs           Core aggregate — 3 factory methods, state machine
│   │   └── Exceptions.cs            Domain exceptions
│   └── Enums/
│       └── Enums.cs                 TransactionStatus, TransactionType, DebitSource
├── Application/
│   ├── DTOs/Dtos.cs                 TransactionResponse, PagedTransactionResponse, TransactionQuery
│   ├── Events/Events.cs             13 inbound + 3 outbound event contracts + topic constants
│   ├── Interfaces/Interfaces.cs     ITransactionRepository, IIdempotencyRepository, IKafkaPublisher
│   └── Services/
│       └── TransactionApplicationService.cs   All lifecycle handlers
├── Infrastructure/
│   ├── Data/TransactionDbContext.cs  2 tables: Transactions, IdempotencyRecords
│   ├── Repositories/Repositories.cs
│   └── Messaging/Kafka.cs           Publisher + KafkaConsumerBase<T>
├── Consumers/
│   └── Consumers.cs                 10 Kafka consumers (one per inbound topic)
├── Middleware/
│   └── Middleware.cs                ExceptionHandlerMiddleware + ConsulRegistration
├── Migrations/
│   └── 20250101000000_InitialCreate.cs
├── Program.cs
├── appsettings.json
└── appsettings.Development.json

tests/TransactionService.Tests/
└── TransactionDomainTests.cs        35 tests across 2 test classes
```

---

## REST endpoints

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| `GET` | `/api/transactions/{userId}` | User | Paginated transaction history |
| `GET` | `/api/transactions/detail/{id}?userId=` | User | Single transaction detail |
| `GET` | `/health` | Public | Health check |

### Query parameters for `GET /api/transactions/{userId}`

| Param | Values | Description |
|-------|--------|-------------|
| `type` | `Deposit`, `InPlatformPayment`, `ExternalTransfer` | Filter by type |
| `status` | `Pending`, `Processing`, `Completed`, `Failed` | Filter by status |
| `from` | ISO date | Start of date range |
| `to` | ISO date | End of date range |
| `cursor` | Transaction GUID | Last seen ID for next page |
| `pageSize` | 1–100 (default 20) | Items per page |

---

## Kafka

### Produced

| Topic | When |
|-------|------|
| `transaction.created` | New transaction record opened (Pending) |
| `transaction.completed` | Full lifecycle success confirmed |
| `transaction.failed` | Any step in the chain failed |

### Consumed (10 topics)

| Topic | From | Action |
|-------|------|--------|
| `vendor.payment.approved` | Vendor Pay Service | Open InPlatformPayment (Pending) |
| `wallet.budget.debited` | Wallet Service | Record budget debit → Processing |
| `wallet.main.debited` | Wallet Service | Record main debit → Processing |
| `wallet.credited` | Wallet Service | Close Deposit or InPlatformPayment → Completed |
| `wallet.insufficient_funds` | Wallet Service | Fail InPlatformPayment |
| `payment.successful` | Payment Service | Open Deposit (Pending) |
| `payment.failed` | Payment Service | Fail Deposit |
| `transfer.created` | Transfer Service | Open ExternalTransfer (Pending) |
| `transfer.completed` | Transfer Service | Complete ExternalTransfer |
| `transfer.failed` | Transfer Service | Fail ExternalTransfer |

---

## Idempotency key design

Every handler derives a deterministic idempotency key by appending a suffix to the
incoming event's own key. This makes each state transition individually idempotent:

| Action | Key pattern |
|--------|-------------|
| Open Deposit | `{paymentKey}:txn:deposit:create` |
| Fail Deposit | `{paymentKey}:txn:deposit:fail` |
| Open InPlatformPayment | `{vendorKey}:txn:inplatform:create` |
| Record budget debit | `{walletKey}:txn:processing` |
| Record main debit | `{walletKey}:txn:processing:main` |
| Complete via wallet.credited | `{creditKey}:txn:complete` |
| Open ExternalTransfer | `{transferKey}:txn:transfer:create` |
| Complete ExternalTransfer | `{transferKey}:txn:transfer:complete` |
| Fail ExternalTransfer | `{transferKey}:txn:transfer:fail` |

### In-platform payment correlation

Wallet Service appends suffixes to the original `vendor.payment.approved` key:
- `:payer:budget` when debiting budget
- `:payer:main` when debiting main
- `:recipient` when crediting recipient

`TransactionApplicationService.StripPayerSuffix()` removes these suffixes to find
the original open transaction by its base creation key.

---

## Getting started

```bash
docker compose up -d
cd src/TransactionService
dotnet run
# Swagger: http://localhost:5006/swagger

cd tests/TransactionService.Tests
dotnet test
```

---

## Key design decisions

**Why does wallet.credited close both Deposits AND InPlatformPayments?**
Both flows end with a credit to a wallet. For deposits the credit event key matches
the original payment key exactly. For in-platform payments, Wallet appends `:recipient`
to the original vendor payment key. The service inspects the suffix to route correctly.

**Why is DebitSource recorded on the transaction?**
The MVP spec requires accurate user reporting showing whether a spend came from their
Budget or Main balance. The `DebitSource`, `BudgetDebited`, and `MainDebited` fields
give the frontend everything needed to display "Paid ₦500 (₦300 from daily budget,
₦200 from main balance)".

**Why 10 separate consumer background services instead of one multi-topic consumer?**
Each consumer has its own consumer group ID and offset tracking. This means:
- Each topic's lag is independently visible in Application Insights
- A slow `wallet.credited` handler doesn't block `transfer.created` processing
- Scaling individual consumers is possible without scaling all of them
