# SplitSpend Auth Service

Identity and session management microservice for SplitSpend. Owns registration, login, JWT issuance, refresh token rotation, OTP-based email verification, password reset, and PIN management.

Runs on **port 5001** and registers itself with Consul as `auth-service`.

---

## Architecture

```
Client
  │
  ▼
API Gateway :5000  ── JWT validation ──► Auth Service :5001
                                               │
                          ┌────────────────────┼──────────────────────┐
                          │                    │                      │
                     SQL Server           Kafka Topics          Consul :8500
                    (AuthDb)        user.registered (→ User Svc)
                                    user.verified   (→ Notif Svc)
                                    user.loggedin   (→ Notif Svc)
                                    user.created    (← User Svc)
```

---

## Endpoints

| Method | Route                       | Auth    | Rate Limit   | Description                                   |
|--------|-----------------------------|---------|--------------|-----------------------------------------------|
| POST   | `/api/auth/register`        | Public  | 5/60s per IP | Register, send OTP, emit user.registered      |
| POST   | `/api/auth/login`           | Public  | 5/60s per IP | Authenticate, return JWT + refresh token      |
| POST   | `/api/auth/verify`          | Public  | —            | Verify email OTP                              |
| POST   | `/api/auth/refresh-token`   | Public  | —            | Rotate refresh token, return new pair         |
| POST   | `/api/auth/forgot-password` | Public  | 3/60s per IP | Send password reset OTP                       |
| POST   | `/api/auth/reset-password`  | Public  | —            | Reset password with OTP, revoke all tokens    |
| POST   | `/api/auth/set-pin`         | Bearer  | —            | Set/update 4-digit transaction PIN            |
| POST   | `/api/auth/logout`          | Public  | —            | Revoke refresh token                          |
| GET    | `/health`                   | Public  | —            | Liveness check                                |
| GET    | `/health/ready`             | Public  | —            | Readiness — SQL Server reachable              |

---

## Kafka Events

### Produces
| Topic            | When                        | Consumed By          |
|------------------|-----------------------------|----------------------|
| `user.registered`| Registration complete       | User Service         |
| `user.verified`  | OTP confirmed               | Notification Service |
| `user.loggedin`  | Successful login            | Notification Service |

### Consumes
| Topic          | From         | Action                                  |
|----------------|--------------|-----------------------------------------|
| `user.created` | User Service | Sync UserId back into UserCredential    |

---

## Domain Rules

### Registration
- Idempotency key enforced — supply `X-Idempotency-Key` header to safely retry
- Duplicate email → 409 Conflict
- Password: 8+ chars, uppercase, lowercase, digit, special character
- Status starts as `PendingVerification` until OTP confirmed

### Login
- 5 failed attempts → account locked for 15 minutes
- Unverified email → 403 with message to verify first
- Successful login rotates nothing; separate logout/refresh-token endpoints handle token lifecycle

### Refresh Token Rotation
- Each refresh produces a **new** access token + **new** refresh token
- Old refresh token is immediately revoked
- Prevents replay attacks — a stolen refresh token can only be used once

### Password Reset
- `forgot-password` always returns 200 — prevents email enumeration
- OTP valid for 15 minutes, single-use
- Password reset revokes **all** refresh tokens for the account

### PIN
- 4 digits, stored as BCrypt hash
- Set/update requires current password (or a valid password-reset OTP)
- The API Gateway checks `X-Pin-Hash` header on `/api/transfers/*` routes before calling Transfer Service

---

## Security
| Control                | Detail                                                              |
|------------------------|---------------------------------------------------------------------|
| Password hashing       | BCrypt with work factor 12                                          |
| PIN hashing            | BCrypt with work factor 12                                          |
| Refresh token storage  | SHA-256 hash stored — raw token never persisted                     |
| Account lockout        | 5 failures → 15 min lock, tracked in `FailedLoginAttempts` column  |
| Token rotation         | Every refresh call invalidates previous refresh token              |
| Email enumeration      | `forgot-password` always 200 regardless of email existence         |
| Idempotency            | Registration keyed — safe to retry without duplicate accounts       |

---

## Data Model

### UserCredential
| Column               | Type     | Notes                                          |
|----------------------|----------|------------------------------------------------|
| Id                   | Guid PK  | Internal credential ID                         |
| UserId               | Guid?    | Set after User Service replies with user.created|
| Email                | string   | Unique, normalised to lowercase                |
| PasswordHash         | string   | BCrypt hash                                    |
| PinHash              | string?  | BCrypt hash, null until PIN is set             |
| Role                 | enum     | User / Vendor / Admin                          |
| Status               | enum     | PendingVerification / Active / Suspended / Deleted|
| FailedLoginAttempts  | int      | Resets to 0 on successful login                |
| LockedUntil          | DateTime?| Null when not locked                           |
| IdempotencyKey       | string   | Unique — prevents duplicate registration       |

### RefreshToken
| Column            | Type    | Notes                               |
|-------------------|---------|-------------------------------------|
| TokenHash         | string  | SHA-256 hash of raw token           |
| DeviceInfo        | string  | User-Agent or client-supplied label |
| IpAddress         | string  | Caller IP at issuance               |
| ExpiresAt         | DateTime| 30 days from issuance               |
| IsRevoked         | bool    | Rotated or explicit logout          |

### OtpRecord
| Column    | Type    | Notes                                     |
|-----------|---------|-------------------------------------------|
| Code      | string  | 6-digit cryptographically random number   |
| Purpose   | enum    | EmailVerification / PasswordReset         |
| IsUsed    | bool    | Single-use — marked true on consumption   |
| ExpiresAt | DateTime| 15 minutes from generation                |

---

## Quick Start

### Prerequisites
- .NET 8 SDK
- SQL Server (local or Docker)
- Kafka (local or Docker)
- Consul (Docker)
- Seq (Docker)

---

## OpenTelemetry

Every operation is traced end-to-end:
- **ASP.NET Core** → inbound HTTP request spans
- **EF Core** → database query spans with SQL text (dev only)
- **HttpClient** → outbound HTTP spans
- **MassTransit** → Kafka producer and consumer spans

The `X-Correlation-Id` is stamped on every span as a tag and Serilog `LogContext` property, so all logs and traces for a single operation share the same identifier across the Auth Service and every downstream service it touches.

### Seq queries
```
# All errors during registration
@Level = "Error" and ServiceName = "SplitSpend.AuthService"

# Trace a specific user's login history
EventType = "user.loggedin" and Email = "user@example.com"

# Find all lockout events
@Message like "%Account is temporarily locked%"

# Correlate with gateway
CorrelationId = "abc123def456"
```

---

## Project Structure

```
SplitSpend.AuthService/
└── src/
    ├── Program.cs
    ├── SplitSpend.AuthService.csproj
    ├── appsettings.json
    ├── Common/
    │   └── Settings.cs               # JwtSettings, ConsulSettings, KafkaSettings, AuthException
    ├── Domain/
    │   ├── Entities/
    │   │   └── AuthEntities.cs       # UserCredential, RefreshToken, OtpRecord
    │   ├── Enums/
    │   │   └── AuthEnums.cs          # UserRole, AccountStatus, OtpPurpose
    │   └── Events/
    │       └── AuthEvents.cs         # Kafka event contracts (produced + consumed)
    ├── Application/
    │   ├── DTOs/
    │   │   └── AuthDtos.cs           # Request/response shapes
    │   ├── Interfaces/
    │   │   └── IAuthInterfaces.cs    # IAuthService, ITokenService, IOtpService, IEventPublisher
    │   ├── Services/
    │   │   ├── AuthService.cs        # Core business logic — all 8 operations
    │   │   ├── TokenService.cs       # JWT generation, refresh token, hashing
    │   │   └── OtpService.cs        # OTP generation and persistence
    │   └── Validators/
    │       └── AuthValidators.cs     # FluentValidation for all request DTOs
    ├── Data/
    │   ├── AuthDbContext.cs          # EF Core DbContext with full entity config
    │   ├── Repositories/
    │   │   └── AuthRepositories.cs  # UserCredential, RefreshToken, OTP repos
    │   └── Migrations/               # EF Core migration files (generated)
    ├── Infrastructure/
    │   ├── Messaging/
    │   │   ├── KafkaEventPublisher.cs  # Produces user.registered/verified/loggedin
    │   │   └── UserCreatedConsumer.cs  # Consumes user.created, syncs UserId
    │   └── Consul/
    │       └── ConsulRegistrationService.cs  # Self-registration on startup
    ├── Controllers/
    │   └── AuthController.cs         # All 8 endpoints with rate limit + auth attributes
    ├── Middleware/
    │   └── AuthMiddleware.cs         # CorrelationIdMiddleware + GlobalExceptionMiddleware
    └── Extensions/
        └── ServiceExtensions.cs      # All DI registrations + pipeline helpers
```
