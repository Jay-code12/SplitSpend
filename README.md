# SplitSpend

**Forced financial discipline through daily spending budgets.**

SplitSpend is a fintech platform that helps university students, NYSC members, and corporate workers control overspending by locking deposited funds into auto-calculated daily spending limits. Parents can remotely fund and cap a student's daily spend, users pay each other and vendors instantly with zero fees, and any Main Balance funds can be transferred out to a Nigerian bank account whenever needed.

> 📌 **Status:** MVP in development — Version 4.0 specification

---

## Table of Contents

- [Problem & Solution](#problem--solution)
- [Key Features](#key-features)
- [Architecture](#architecture)
- [Tech Stack](#tech-stack)
- [Microservices](#microservices)
- [Event-Driven Design](#event-driven-design)
- [Getting Started](#getting-started)
- [Documentation](#documentation)
- [Security](#security)
- [Observability](#observability)
- [MVP Scope](#mvp-scope)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [License](#license)

---

## Problem & Solution

University students, corporate workers, and NYSC members frequently struggle with spending discipline — overspending early in the month with no structured daily control.

**SplitSpend solves this by:**

- 🔒 Locking deposited funds into auto-calculated **daily spending budgets**
- 👨‍👩‍👧 Letting **parents remotely fund and cap** a student's daily spending
- ⚡ Enabling **zero-fee** vendor and user-to-user payments — pure internal wallet moves, no external processor round-trip
- 💳 Providing a **Main Balance fallback** so users are never hard-blocked from paying
- 🏦 Allowing **transfers to any Nigerian bank account** from Main Balance

## Key Features

| Feature | Description |
|---|---|
| **Daily Budgets** | Auto-split a total budget into daily allowances that release each morning and expire (unused) back to Main Balance each night |
| **Parent Controls** | Parents fund and cap a student's daily spend remotely |
| **In-Platform Payments** | Vendor QR / UserID payments and direct user-to-user sends — instant, free, internal wallet transfers |
| **External Transfers** | Send Main Balance funds to any Nigerian bank account via Paystack |
| **Gift Budgets** | Send a budget gift to another user (non-cancellable) |
| **Full Ledger** | Every credit/debit recorded with before/after balances for full auditability |
| **Real-Time Notifications** | Push, SMS, and email for every key wallet, budget, payment, and transfer event |

## Architecture

SplitSpend is built as **9 independently deployable microservices** behind a single API Gateway, communicating synchronously via REST for time-critical operations and asynchronously via **Kafka** for everything else.

```
                        ┌──────────────────┐
                        │   API Gateway     │  (Ocelot + Consul + Polly)
                        │  Auth · Rate      │
                        │  Limit · Routing  │
                        └─────────┬─────────┘
                                  │
        ┌─────────────┬──────────┼──────────┬─────────────┐
        │             │          │          │             │
   Auth Service  User Service  Wallet   Budget Service  Transfer
                              Service ★                 Service
                                  │
        ┌─────────────┬──────────┼──────────┬─────────────┐
        │             │          │          │
  Transaction    Payment    Vendor Pay   Notification
   Service       Service     Service       Service

              ▲ all async coordination via Apache Kafka ▲
```

**Core principle:** the **Wallet Service** is the single source of financial truth. No other service moves money directly. Vendor and user-to-user payments are purely internal wallet operations — Paystack is only ever involved for deposits (Payment Service) and external payouts (Transfer Service).

## Tech Stack

| Layer | Technology |
|---|---|
| Backend | ASP.NET Core Web API (C#) |
| Web Frontend | Angular |
| Mobile | .NET MAUI (Android & iOS) |
| Database | SQL Server — database-per-service |
| Messaging | Apache Kafka |
| Payments | Paystack API (deposits + transfers) |
| Background Jobs | Hangfire / Quartz.NET |
| Auth | JWT + Refresh Tokens + PIN |
| Service Discovery | HashiCorp Consul |
| API Gateway | ASP.NET Core + Ocelot |
| Resilience | Polly |
| Logging | Serilog → Seq |
| Tracing | OpenTelemetry |
| Monitoring | Azure Application Insights |

## Microservices

| # | Service | Responsibility | Criticality |
|---|---|---|---|
| 1 | **Auth Service** | Registration, login, JWT, PIN management | High |
| 2 | **User Service** | Profiles, KYC status, roles | High |
| 3 | **Wallet Service** ★ | Sole owner of Main + Budget balances; all debits/credits | Critical |
| 4 | **Budget Service** | Budget rules, daily splits, gifts, CRON events | Critical |
| 5 | **Transfer Service** | External bank transfers via Paystack | High |
| 6 | **Transaction Service** | Cross-service transaction lifecycle coordinator | High |
| 7 | **Payment Service** | Paystack deposit webhooks only | High |
| 8 | **Vendor Pay Service** | In-platform payment requests (QR / user-to-user) | Medium |
| 9 | **Notification Service** | Push, SMS, email across all events | Medium |

## Event-Driven Design

All events follow the pattern **`<domain>.<subdomain>.<verb_past_tense>`**, e.g. `wallet.budget.debited`, `transfer.completed`, `budget.daily.expired`.

Wallet debits are split into `wallet.budget.debited` and `wallet.main.debited` so downstream consumers (like Budget Service) only react to the events relevant to them — a plain Main Balance transfer should never pollute budget tracking.

See [`SplitSpend_3_API_Specification.pdf`](#documentation) for the full event catalogue and per-service event contracts.

## Getting Started

> Setup instructions are being finalized alongside the service implementations. This section will be updated with local dev environment steps (Docker Compose for Kafka/SQL Server/Consul, per-service run instructions, and Postman/OpenAPI collections) as the codebase comes online.

```bash
# Clone the repo
git clone https://github.com/<org>/splitspend.git
cd splitspend

# Coming soon: docker-compose up for local infrastructure
```

## Documentation

Full specifications are maintained as versioned PDFs:

| Document | Covers |
|---|---|
| **Product Overview, Security & Compliance** | Problem/solution, value proposition, security controls, key risks, MVP scope |
| **Software Architecture Design** | Event naming convention, observability strategy, tech stack, service architecture, service discovery, API gateway |
| **API Specification** | Full REST endpoints and Kafka event contracts for all 9 services |
| **Database Design** | Per-service data ownership and key entities |
| **User Stories Backlog** | Feature backlog organized by epic with acceptance criteria |

## Security

- JWT access + refresh token rotation
- PIN required for all transfers and payment approvals
- Idempotency keys on every financial operation
- Role-based authorization (User / Vendor / Admin)
- HMAC-verified Paystack webhooks
- Full audit trail via the Wallet ledger (before/after balances on every entry)
- Account lockout after repeated failed logins

## Observability

- **Serilog + Seq** — centralized structured logging across all services
- **OpenTelemetry** — distributed tracing end-to-end across every request
- **Azure Application Insights** — live metrics, dependency tracking, alerting

## MVP Scope

**In scope:** registration/login/PIN, wallet deposits & ledger, budget creation & daily release/expiry, in-platform vendor/user payments, external bank transfers, transaction history, push/email/SMS notifications.

**Post-MVP:** full KYC, fraud detection & ML anomaly scoring, analytics dashboard, multi-currency support, scheduled/recurring transfers.

## Roadmap

- [ ] Core service scaffolding (9 microservices)
- [ ] Kafka event bus wiring
- [ ] API Gateway (Ocelot) + Consul service discovery
- [ ] Paystack deposit + transfer integration
- [ ] Mobile app (.NET MAUI)
- [ ] Web app (Angular)
- [ ] Observability stack (Seq, OpenTelemetry, App Insights)
- [ ] Post-MVP: KYC, fraud detection, analytics

## Contributing

This project is currently in active MVP development. Contribution guidelines will be added as the codebase stabilizes.

## License

_License to be determined._

---

<p align="center">Built for people who need their money to say no when they can't. 💸</p>
