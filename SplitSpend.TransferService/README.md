# SplitSpend — Transfer Service

**Owns the full lifecycle of every outbound bank transfer.**
Coordinates with Wallet Service (pre-debit / reversal) and Paystack Transfers API (payout).
Handles webhook verification, timeout recovery, and auto-reversal after 24 hours.

---

## What it does

| Responsibility | Detail |
|---|---|
| **Transfer initiation** | PIN verified by Gateway; verifies account name via Paystack; pre-flight balance check; creates BankTransfer (Pending) |
| **Wallet handoff** | Listens for `wallet.main.transfer.initiated`; transitions to Processing; calls Paystack Transfers API |
| **Webhook processing** | Verifies HMAC-SHA512 signature; handles `transfer.success`, `transfer.failed`, `transfer.reversed` |
| **Timeout recovery** | Hangfire job every 30 minutes; queries Paystack for real status; forces failure + reversal after 24h |
| **Manual verify** | `GET /api/transfers/verify/{id}` — re-checks status at Paystack for missed webhooks |
| **Beneficiary caching** | Saves resolved account details after first lookup — no re-entry for repeat transfers |
| **Reversal signalling** | Emits `transfer.failed` → Wallet Service reverses the Main Balance pre-debit |

---

## Project structure

```
src/TransferService/
├── Controllers/
│   └── TransferController.cs        7 endpoints + webhook handler
├── Domain/
│   ├── Entities/
│   │   ├── BankTransfer.cs          Core aggregate — full lifecycle state machine
│   │   └── BankBeneficiary.cs       Cached beneficiary + all domain exceptions
│   └── Enums/
│       └── Enums.cs                 TransferStatus (Pending→Processing→Completed/Failed/Reversed)
├── Application/
│   ├── DTOs/Dtos.cs
│   ├── Events/Events.cs             3 produced + 1 consumed event contract + topic constants
│   ├── Interfaces/Interfaces.cs     ITransferRepo, IBeneficiaryRepo, IPaystackClient, IWalletClient...
│   └── Services/
│       └── TransferApplicationService.cs  Full lifecycle orchestration
├── Infrastructure/
│   ├── Data/TransferDbContext.cs    3 tables: BankTransfers, BankBeneficiaries, IdempotencyRecords
│   ├── Repositories/Repositories.cs
│   ├── Http/
│   │   ├── PaystackClient.cs        Full Paystack API integration with HMAC verification
│   │   └── WalletServiceClient.cs   Balance pre-flight check
│   ├── Messaging/Kafka.cs           Publisher + KafkaConsumerBase<T>
│   └── ConsulRegistration.cs
├── Consumers/
│   └── WalletMainTransferInitiatedConsumer.cs
├── BackgroundJobs/
│   └── HangfireJobRegistrar.cs      30-minute timeout check job
├── Middleware/
│   └── ExceptionHandlerMiddleware.cs
├── Migrations/
│   └── 20250101000000_InitialCreate.cs
├── Program.cs
├── appsettings.json
└── appsettings.Development.json

tests/TransferService.Tests/
└── TransferDomainTests.cs           30 tests across 4 test classes
```

---

## REST endpoints

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| `POST` | `/api/transfers/initiate` | User + PIN (Gateway) | Initiate bank transfer |
| `POST` | `/api/transfers/webhook` | HMAC-SHA512 only | Receive Paystack transfer webhook |
| `GET`  | `/api/transfers/{userId}` | User | List all transfers for a user |
| `GET`  | `/api/transfers/detail/{id}?userId=` | User | Single transfer detail |
| `GET`  | `/api/transfers/banks` | User | Nigerian bank list from Paystack |
| `POST` | `/api/transfers/verify-account` | User | Resolve account name before transfer |
| `GET`  | `/api/transfers/verify/{id}?userId=` | User | Re-check status at Paystack (recovery) |
| `GET`  | `/health` | Public | Health check |

---

## Kafka events

### Produced

| Topic | When |
|-------|------|
| `transfer.created` | Transfer record opened (Pending) — Wallet pre-debits Main Balance |
| `transfer.completed` | Paystack confirmed bank delivery |
| `transfer.failed` | Paystack rejected OR 24h timeout — triggers Wallet reversal |

### Consumed

| Topic | Action |
|-------|--------|
| `wallet.main.transfer.initiated` | Pre-debit confirmed; call Paystack Transfers API |

---

## Paystack integration

**Account resolution** (`POST /api/transfers/verify-account`):
Calls `GET /bank/resolve?account_number=&bank_code=` — returns the account holder name so the user can confirm who they're paying before submitting.

**Transfer initiation** (inside `OnWalletPreDebitAsync`):
1. `POST /transferrecipient` — creates a reusable recipient record at Paystack
2. `POST /transfer` — initiates the payout using the recipient code

**Webhook** (`POST /api/transfers/webhook`):
- `X-Paystack-Signature` header verified via `HMAC-SHA512(rawPayload, secretKey)` before any processing
- Responds `200` immediately; processing is fire-and-forget with idempotency
- Handles: `transfer.success` → Completed, `transfer.failed` / `transfer.reversed` → Failed

**Amount handling**: All Paystack amounts are in kobo (₦1 = 100 kobo). `PaystackClient` multiplies by 100 when sending and divides by 100 when receiving.

---

## Resilience

| Concern | Mechanism |
|---|---|
| Paystack API flakiness | Polly retry: 3 attempts, exponential backoff (2s, 4s, 8s) |
| Paystack outage | Circuit breaker: opens after 5 failures, resets after 60s |
| Missed webhook | Hangfire timeout check every 30 minutes; manual verify endpoint |
| 24h no confirmation | Auto-fail + reversal via `RunTimeoutCheckAsync` |
| Duplicate webhook | Idempotency key: `webhook:{reference}:{event}` |
| Kafka at-least-once | All consumer handlers are idempotent |

---

## Getting started

```bash
# 1. Start dependencies
docker compose up -d

# 2. Set your Paystack test key in appsettings.Development.json
# Paystack:SecretKey = sk_test_...

# 3. Run
cd src/TransferService
dotnet run
# Swagger: http://localhost:5005/swagger

# 4. Test
cd tests/TransferService.Tests
dotnet test
```

### Testing webhooks locally
Use the [Paystack CLI](https://paystack.com/docs/payments/webhooks/#testing-webhooks) or ngrok to expose your local endpoint, then trigger test transfers from the Paystack dashboard.
