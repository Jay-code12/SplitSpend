# SplitSpend User Service

Manages user profiles, vendor profiles, and role assignment for SplitSpend.
Owns the authoritative `UserId` that is shared across the entire platform.

Runs on **port 5002** and registers itself with Consul as `user-service`.

---

## Architecture

```
API Gateway :5000
      │  JWT + X-User-Id / X-User-Role headers stamped by gateway
      ▼
User Service :5002
      │
      ├── SQL Server (SplitSpend_User DB)
      │     Users / UserProfiles / VendorProfiles
      │
      ├── Kafka (via MassTransit)
      │     Consumes → user.registered  (from Auth Service)
      │     Produces → user.created     (→ Auth Service)
      │     Produces → user.updated     (→ Notification Service)
      │     Produces → user.deleted     (→ Notification Service)
      │
      └── Consul :8500
            Registers as "user-service" on startup
            Deregisters gracefully on shutdown
```

### Registration Handshake

```
Auth Service                    User Service
     │                               │
     │── user.registered ──────────► │  CreateFromRegistrationAsync()
     │                               │  Creates User + UserProfile
     │                               │  (idempotent — safe to retry)
     │◄─────────────── user.created ─│  Publishes UserId back
     │                               │
     │  SetUserId() on Credential    │
```

---

## Endpoints

| Method | Route                  | Auth         | Description                                    |
|--------|------------------------|--------------|------------------------------------------------|
| GET    | `/api/users/{id}`      | Bearer JWT   | Get user profile (owner or Admin)              |
| PUT    | `/api/users/{id}`      | Bearer JWT   | Update profile, bio, avatar, vendor details    |
| DELETE | `/api/users/{id}`      | Bearer JWT   | Soft-delete account (owner or Admin)           |
| POST   | `/api/users/{id}/role` | Bearer + Admin | Assign role — auto-creates VendorProfile     |
| GET    | `/health`              | Public       | Liveness check                                 |
| GET    | `/health/ready`        | Public       | Readiness — SQL Server reachable               |

### Ownership Rules
- `GET`, `PUT`, `DELETE` — the caller's `X-User-Id` (stamped by gateway) must match the `{id}` in the route, OR the caller must have the `Admin` role
- `POST /{id}/role` — Admin only

---

## Kafka Events

### Produces
| Topic          | When                               | Consumed By          |
|----------------|------------------------------------|----------------------|
| `user.created` | Profile created from registration  | Auth Service         |
| `user.updated` | Profile or role changed            | Notification Service |
| `user.deleted` | Account soft-deleted               | Notification Service |

### Consumes
| Topic             | From         | Action                                            |
|-------------------|--------------|---------------------------------------------------|
| `user.registered` | Auth Service | Create User + UserProfile, publish user.created   |

---

## Data Model

### User
| Column        | Type     | Notes                                            |
|---------------|----------|--------------------------------------------------|
| Id            | Guid PK  | Authoritative UserId shared across the platform  |
| CredentialId  | Guid     | Links back to Auth Service UserCredential         |
| FirstName     | string   | Empty until user completes their profile          |
| LastName      | string   | Empty until user completes their profile          |
| Email         | string   | Unique, normalised to lowercase                  |
| Phone         | string?  | Optional                                          |
| Role          | enum     | User / Vendor / Admin (stored as string)         |
| Status        | enum     | Active / Suspended / Deleted (soft-delete)       |

### UserProfile (1:1 with User)
| Column      | Type      | Notes                                  |
|-------------|-----------|----------------------------------------|
| AvatarUrl   | string?   | CDN URL                                |
| Bio         | string?   | Max 500 chars                          |
| DateOfBirth | DateTime? | Must be 13+ years old                 |
| KycStatus   | enum      | NotSubmitted / Pending / Verified / Rejected |

### VendorProfile (1:1 with User, only when Role = Vendor)
| Column          | Type    | Notes                                       |
|-----------------|---------|---------------------------------------------|
| BusinessName    | string  | Required when role = Vendor                  |
| BusinessType    | string? | e.g. "Food", "Retail"                       |
| BusinessAddress | string? | Physical or registered address              |
| IsVerified      | bool    | Set by Admin verification process           |

---

## Domain Rules

- **Soft delete only** — User records are never physically deleted; a global EF Core query filter hides deleted users from all regular queries
- **VendorProfile auto-creation** — Promoting a user to the Vendor role automatically creates an empty VendorProfile
- **Idempotent consumer** — `UserRegisteredConsumer` checks for an existing profile by `CredentialId` before creating — safe to replay Kafka messages
- **EF Core global filter** — `Status != Deleted` is applied at the DbContext level; admin operations use `IgnoreQueryFilters()` explicitly
- **Role stored as string** — `User`, `Vendor`, `Admin` — readable in the DB without lookup tables

---

## Running with Docker

### Prerequisites
- Docker Desktop (or Docker Engine + Compose)

### Quick start
```bash
# 1. Copy the example env file
cp .env.example .env

# 2. Fill in your JWT secret (must match Auth Service and Gateway)
# Edit .env — set JWT_SECRET_KEY to a 256-bit base64 string

# 3. Build and start everything
docker-compose up --build

# User Service:    http://localhost:5002
# Consul UI:       http://localhost:8500
# Seq UI:          http://localhost:5341
# SQL Server:      localhost:1434  (sa / YourStrong!Password123)
# Kafka:           localhost:9093  (external listener for local tools)
```

### Useful commands
```bash
# View live logs
docker-compose logs -f user-service

# Rebuild only the service (not infrastructure)
docker-compose up --build user-service

# Stop everything and remove volumes (clean slate)
docker-compose down -v

# Check health
curl http://localhost:5002/health
curl http://localhost:5002/health/ready
```

### Multi-service setup
If you are running multiple SplitSpend services at the same time, the
`splitspend-network` Docker network is declared with an explicit name so
other services can join it:

```yaml
# In another service's docker-compose.yml:
networks:
  splitspend-network:
    external: true
```

---

## Running locally without Docker

```bash
# Start infrastructure via Docker
docker-compose up sqlserver kafka zookeeper consul seq -d

# Set secrets as environment variables
export JWT_SECRET_KEY="your-256-bit-secret"
export ConnectionStrings__UserDb="Server=localhost,1434;Database=SplitSpend_User;User Id=sa;Password=YourStrong!Password123;TrustServerCertificate=True;"

# Run the service
cd src
dotnet run
# Starts on http://localhost:5002
```

### Create EF Core migrations (first time)
```bash
cd src
dotnet ef migrations add InitialCreate --output-dir Data/Migrations
```
Migrations are applied automatically on startup — no need to run `dotnet ef database update` manually.

---

## OpenTelemetry

Every operation creates a trace span:
- **ASP.NET Core** — each inbound HTTP request
- **EF Core** — each database query (SQL text included in dev)
- **HttpClient** — outbound HTTP calls
- **MassTransit** — Kafka producer and consumer spans

`X-Correlation-Id` is stamped on every span as a tag and pushed into Serilog `LogContext` — every log line for a request carries it without explicit passing.

### Seq queries
```
# All errors from User Service
@Level = "Error" and ServiceName = "SplitSpend.UserService"

# Trace the full registration handshake for a specific user
CorrelationId = "abc123" and ServiceName in ["SplitSpend.AuthService", "SplitSpend.UserService"]

# All role-assignment operations
@Message like "%Role assigned%"

# Consumer events
EventType = "user.registered"
```

---

## Project Structure

```
SplitSpend.UserService/
├── Dockerfile                          # Multi-stage, non-root user, HEALTHCHECK
├── .dockerignore                       # Excludes bin/, obj/, logs/, secrets
├── docker-compose.yml                  # Full local dev stack
├── .env.example                        # Template — copy to .env, never commit .env
├── README.md
└── src/
    ├── Program.cs                      # Bootstrap + pipeline
    ├── SplitSpend.UserService.csproj
    ├── appsettings.json
    ├── Common/
    │   └── Settings.cs                 # All settings classes + UserException
    ├── Domain/
    │   ├── Entities/
    │   │   └── UserEntities.cs         # User, UserProfile, VendorProfile
    │   ├── Enums/
    │   │   └── UserEnums.cs            # UserRole, UserStatus, KycStatus
    │   └── Events/
    │       └── UserEvents.cs           # Kafka contracts (produced + consumed)
    ├── Application/
    │   ├── DTOs/
    │   │   └── UserDtos.cs             # Request/response shapes
    │   ├── Interfaces/
    │   │   └── IUserInterfaces.cs      # IUserService, IUserEventPublisher
    │   ├── Services/
    │   │   └── UserService.cs          # All business logic
    │   └── Validators/
    │       └── UserValidators.cs       # FluentValidation for update + role requests
    ├── Data/
    │   ├── UserDbContext.cs            # EF Core — global soft-delete filter
    │   ├── Repositories/
    │   │   └── UserRepository.cs       # IUserRepository + implementation
    │   └── Migrations/                 # Generated by: dotnet ef migrations add
    ├── Infrastructure/
    │   ├── Messaging/
    │   │   └── UserMessaging.cs        # KafkaUserEventPublisher + UserRegisteredConsumer
    │   └── Consul/
    │       └── ConsulRegistrationService.cs
    ├── Controllers/
    │   └── UsersController.cs          # GET, PUT, DELETE, POST /role
    ├── Middleware/
    │   └── UserMiddleware.cs           # CorrelationId + GlobalException
    └── Extensions/
        └── ServiceExtensions.cs        # All DI + pipeline helpers
```
