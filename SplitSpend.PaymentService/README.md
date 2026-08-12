# SplitSpend — Payment Service

**The sole entry point for all external money arriving on the platform.**
One job: verify Paystack deposit webhooks and emit `payment.successful` so Wallet Service can credit the user.

---

## Scope (from MVP spec)

Payment Service is deliberately narrow:

| ✅ Handles | ❌ Does NOT handle |
|---|---|
| `charge.success` Paystack webhooks | Vendor payouts (Wallet Service) |
| Virtual account provisioning | User-to-user payments (Wallet Service) |
| Manual deposit re-verification | External bank transfers (Transfer Service) |
| Deposit history per user | Any Kafka consumption |

It is the only service that ever touches `payment.successful` and `payment.failed`. Every other service that needs to know about deposits listens to those events.

---

## Project structure

```
src/PaymentService/
├── Controllers/
│   └── PaymentController.cs         5 endpoints: webhook, verify, history, provision VA, get VA
├── Domain/
│   ├── Entities/
│   │   ├── Entities.cs              PaymentLog (immutable audit) + VirtualAccount
│   │   └── Exceptions.cs            5 typed domain exceptions
│   └── Enums/
│       └── Enums.cs                 PaymentStatus, PaymentType
├── Application/
│   ├── DTOs/Dtos.cs                 Paystack webhook shapes + all request/response records
│   ├── Events/Events.cs             PaymentSuccessfulEvent, PaymentFailedEvent + topic constants
│   ├── Interfaces/
│   │   ├── Interfaces.cs            IPaymentLogRepo, IVirtualAccountRepo, IPaystackClient...
│   │   └── IVirtualAccountRepository.cs  GetByCustomerCodeAsync extension
│   └── Services/
│       └── PaymentApplicationService.cs  All business logic
├── Infrastructure/
│   ├── Data/PaymentDbContext.cs     3 tables: PaymentLogs, VirtualAccounts, IdempotencyRecords
│   ├── Repositories/Repositories.cs
│   ├── Http/PaystackClient.cs       HMAC verification + charge verify + DVA provisioning
│   ├── Messaging/KafkaPublisher.cs  Publish only — no consumers
│   └── ConsulRegistration.cs
├── Middleware/
│   └── ExceptionHandlerMiddleware.cs
├── Migrations/
│   └── 20250101000000_InitialCreate.cs
├── Program.cs
├── appsettings.json
└── appsettings.Development.json

tests/PaymentService.Tests/
└── PaymentDomainTests.cs            27 tests across 5 classes
```

---

## REST endpoints

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| `POST` | `/api/payments/webhook` | HMAC-SHA512 only | Receive Paystack charge.success webhook |
| `GET`  | `/api/payments/verify/{reference}` | User | Manual re-verify and recover missed deposit |
| `GET`  | `/api/payments/{userId}/history` | User | Deposit history for a user |
| `POST` | `/api/payments/virtual-account` | User | Provision dedicated virtual account for new user |
| `GET`  | `/api/payments/{userId}/virtual-account` | User | Get user's virtual account details |
| `GET`  | `/health` | Public | Health check |

---

## Kafka events

### Produced (only)

| Topic | When |
|-------|------|
| `payment.successful` | Deposit webhook verified and processed — triggers Wallet credit + Transaction create |
| `payment.failed` | Webhook received but processing failed (user resolution, invalid data) |

Payment Service **never consumes** any Kafka topics. It is the upstream source for deposit events.

---

## Deposit flow (end to end)

```
User bank transfer
      ↓
Paystack virtual account
      ↓
POST /api/payments/webhook  (Paystack → Payment Service)
      ↓  HMAC-SHA512 verified
      ↓  Idempotency check (deposit:{reference})
      ↓  Resolve userId from PaystackCustomerCode → VirtualAccount
      ↓  Create PaymentLog (Success)
      ↓  Emit payment.successful
      ↓
Wallet Service (consumes payment.successful)
      → Credits user MainBalance
      → Emits wallet.credited
      ↓
Transaction Service (consumes payment.successful)
      → Opens Deposit transaction (Pending → Completed)
      ↓
Notification Service (consumes wallet.credited)
      → Sends push + email to user
```

---

## Webhook security

The `POST /api/payments/webhook` endpoint verifies `X-Paystack-Signature` **before doing anything else**:

```
signature = HMAC-SHA512(rawRequestBody, paystackSecretKey)
```

- Computed in `PaystackClient.VerifyWebhookSignature()` using `System.Security.Cryptography.HMACSHA512`
- Raw body is read with `Request.EnableBuffering()` before model binding — ensures the exact bytes that Paystack signed are what we verify
- If signature is invalid: `400 Bad Request` immediately, nothing is logged or saved
- Processing is fire-and-forget after the `200 OK` response — Paystack has a short response timeout and retries if it doesn't get `200` quickly
- Idempotency key `deposit:{reference}` ensures duplicate deliveries are no-ops

---

## Virtual account provisioning

Each SplitSpend user gets a unique Nigerian bank account number (Dedicated Virtual Account via Paystack/WEMA Bank):

1. `POST /api/payments/virtual-account` called during user registration
2. Payment Service calls `POST /customer` on Paystack → gets `customer_code`
3. Payment Service calls `POST /dedicated_account` → gets account number + bank details
4. Account stored in `VirtualAccounts` table, indexed by `UserId`, `AccountNumber`, and `PaystackCustomerCode`
5. When a deposit arrives, the webhook's `customer.customer_code` is used to look up the `UserId`

---

## Getting started

```bash
# 1. Start dependencies
docker compose up -d

# 2. Set your Paystack test key
# Edit appsettings.Development.json: Paystack:SecretKey = sk_test_...

# 3. Run
cd src/PaymentService
dotnet run
# Swagger: http://localhost:5007/swagger

# 4. Test
cd tests/PaymentService.Tests
dotnet test

# 5. Test webhooks locally
# Use ngrok to expose localhost:5007 then configure in Paystack dashboard
# ngrok http 5007
```

### Simulating a webhook in dev

```bash
# Compute signature locally (replace SECRET and BODY as needed)
echo -n '{"event":"charge.success","data":{"reference":"TEST_001","amount":500000,"currency":"NGN","channel":"bank_transfer","customer":{"customer_code":"CUS_xxx"}}}' \
  | openssl dgst -sha512 -hmac "sk_test_your_key" | awk '{print $2}'

# Then POST with that signature
curl -X POST http://localhost:5007/api/payments/webhook \
  -H "Content-Type: application/json" \
  -H "X-Paystack-Signature: <computed_hex>" \
  -d '{"event":"charge.success","data":{...}}'
```
