# Aspire Payments

An idempotent payment-processing reference implementation built with .NET 10,
PostgreSQL, RabbitMQ, .NET Aspire, and Angular.

The project simulates the full lifecycle of an order payment: the API persists
the order, the payment intent, and an outbox message in a single database
transaction; a background worker consumes the request from RabbitMQ, calls a
deterministic payment gateway, and writes the result back; the API streams the
status change to the browser over Server-Sent Events (SSE).

It is a learning and portfolio project. It is deliberately small enough to read
in one sitting, but it exercises the failure modes that make distributed
payments hard: duplicate requests, concurrent workers, broker redelivery,
transient gateway failures, dead-letter queues, and partial refunds.

---

## Contents

1. [Overview](#1-overview)
2. [Business context](#2-business-context)
3. [What this project demonstrates](#3-what-this-project-demonstrates)
4. [Architecture](#4-architecture)
5. [Technology stack](#5-technology-stack)
6. [Repository layout](#6-repository-layout)
7. [Prerequisites](#7-prerequisites)
8. [Getting started](#8-getting-started)
9. [Configuration](#9-configuration)
10. [Running locally](#10-running-locally)
11. [Testing](#11-testing)
12. [API reference](#12-api-reference)
13. [Usage examples](#13-usage-examples)
14. [Payment and refund lifecycle](#14-payment-and-refund-lifecycle)
15. [Reliability design](#15-reliability-design)
16. [Data model](#16-data-model)
17. [Observability](#17-observability)
18. [Security](#18-security)
19. [Limitations](#19-limitations)
20. [Future work](#20-future-work)
21. [License](#21-license)

---

## 1. Overview

`AspirePayments` is a multi-project .NET solution plus an Angular frontend.
It exposes a small HTTP API for creating orders, requesting payments, and
refunding captured payments. Payment requests are processed asynchronously.

The central guarantee is **idempotency end to end**:

- the caller can retry `POST /api/orders` with the same `Idempotency-Key` and
  receive the original result instead of a duplicate charge;
- the database enforces uniqueness, so two concurrent API instances cannot both
  create a payment for the same key;
- the worker treats broker redelivery as a duplicate and records that decision
  instead of repeating side effects.

The simulated gateway makes every scenario deterministic, so the reliability
behaviour can be demonstrated without a real payment provider.

## 2. Business context

A storefront needs to accept a payment when an order is placed, but charging a
card is a slow, unreliable external call. Doing it inside the HTTP request
creates two problems: the user waits, and a timeout leaves the system unsure
whether the charge happened.

This project models the common solution: accept the order quickly, persist the
intent to charge, and let a worker complete the charge asynchronously. The order
moves through explicit states (`PendingPayment` → `Paid` / `Cancelled`, plus
partial and full refund states) that the frontend can display.

The domain rules implemented here are:

- an order is only `Paid` after the gateway approves the payment;
- a declined payment cancels the order and records a **compensation** entry, not
  a refund, because no money was captured;
- only a captured payment can be refunded, and refunds cannot exceed the
  captured amount, even across multiple partial refunds;
- retries and duplicate deliveries never charge twice.

## 3. What this project demonstrates

- **Transactional outbox** — the business row and the message that triggers the
  next step commit together.
- **Idempotent HTTP API** — `Idempotency-Key` header or body field, unique index
  in PostgreSQL, replay detection, and `409 Conflict` when the same key is
  reused with a different payload.
- **At-least-once messaging with idempotent consumers** — manual acknowledgements,
  a conditional state transition to claim a payment, and duplicate records.
- **Retry with delay and a dead-letter queue** — transient failures are retried
  through a TTL queue; exhausted messages land in `payments.dead`.
- **Compensation vs. refund** — distinct domain concepts, with separate data.
- **Cumulative refund accounting** — partial refunds are summed; the order ends
  as `PartiallyRefunded` or `Refunded`.
- **Distributed tracing across the broker** — the W3C `traceparent` is stored in
  the outbox, published as a message header, and resumed by the worker.
- **Live status updates** — a broadcast channel feeds an SSE endpoint, so
  multiple browser tabs each receive every event.
- **Tests against real infrastructure** — PostgreSQL and RabbitMQ containers,
  including concurrency and dead-letter behaviour.

## 4. Architecture

```mermaid
flowchart LR
    subgraph Browser
        UI[Angular 22 SPA<br/>SSR enabled]
    end

    subgraph API[AspirePayments.Api]
        EP[Minimal API endpoints]
        OUT[Outbox publisher]
        SSE[SSE endpoint /api/events]
        NC[Notification consumer]
    end

    DB[(PostgreSQL)]
    MQ{{RabbitMQ}}
    WK[AspirePayments.Worker]
    GW[Simulated payment gateway]

    UI -- "POST /api/orders<br/>GET /api/orders" --> EP
    EP -- "order + payment + outbox<br/>one transaction" --> DB
    OUT -- "read unpublished" --> DB
    OUT -- "publish payment.requested" --> MQ
    MQ -- "payments.requests" --> WK
    WK -- "claim, charge, commit result" --> DB
    WK -- "call" --> GW
    WK -- "publish status + refund requests" --> MQ
    MQ -- "payments.notifications" --> NC
    NC --> SSE
    SSE -- "text/event-stream" --> UI
```

The write path is:

1. `POST /api/orders` starts a database transaction and inserts the order, the
   payment transaction, and an outbox row.
2. `OutboxPublisher` (hosted by both the API and the worker) selects unpublished
   rows with `FOR UPDATE SKIP LOCKED`, publishes them to RabbitMQ, and marks them
   as published.
3. `PaymentRequestConsumer` in the worker declares the topology, consumes
   `payments.requests`, and calls `PaymentProcessor`.
4. The processor claims the payment with a conditional `UPDATE` (`Pending` →
   `Processing`), calls the gateway, writes the final state, a processing
   record, a compensation when declined, and the status-change outbox message —
   all in one transaction — and only then acknowledges the broker message.
5. The API consumes `payments.notifications` and broadcasts the event to every
   connected SSE client.

Deployment topology is intentionally simple: two stateless HTTP processes (API
and worker) and two stateful services (PostgreSQL and RabbitMQ). The Aspire
AppHost provisions all of them for local development.

## 5. Technology stack

| Area | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 (`net10.0`) | SDK pinned by `global.json` |
| API | ASP.NET Core Minimal APIs | Typed results, endpoint filters |
| Data access | EF Core 10 + Npgsql | Migrations applied at API startup |
| Database | PostgreSQL 17 | Unique indexes enforce idempotency |
| Messaging | RabbitMQ 4 + RabbitMQ.Client v7 | Durable queues, TTL retry, DLX |
| Orchestration | .NET Aspire 13 | AppHost, dashboard, service discovery |
| Telemetry | OpenTelemetry via ServiceDefaults | Traces, metrics, structured logs |
| Frontend | Angular 22 + SSR | Signals, standalone components |
| Tests | xUnit + Testcontainers | Real PostgreSQL and RabbitMQ |
| CI | GitHub Actions | Backend build/test + frontend build |

## 6. Repository layout

```text
AspirePayments.slnx
├── AspirePayments.Api/            HTTP API, endpoints, SSE, outbox publisher
├── AspirePayments.Data/           Domain, EF Core, payments, messaging contracts
├── AspirePayments.Worker/         RabbitMQ consumers and payment/refund processors
├── AspirePayments.ServiceDefaults/  OpenTelemetry, health checks, resilience
├── AspirePayments.AppHost/        Aspire orchestration for local development
├── AspirePayments.Tests/
│   ├── Unit/                         Pure logic tests (no Docker)
│   └── Integration/                  Testcontainers-backed tests
├── AspirePayments.Web/            Angular application
├── docker-compose.yaml               Optional: infrastructure only
├── appsettings.Example.json          Manual-run configuration template
├── .env.example                      Compose credentials template
├── .github/workflows/ci.yml          Continuous integration
├── Directory.Build.props             Shared build settings
├── .editorconfig                     C# formatting and style rules
└── global.json                       Pinned .NET SDK
```

## 7. Prerequisites

- **.NET SDK 10.0.401** or a compatible 10.0.4xx patch (`dotnet --version`)
- **Docker Desktop** or another Docker engine (PostgreSQL, RabbitMQ, and the
  integration test containers)
- **Node.js 22+** and **npm 11+** for the Angular frontend
- Ports `5432` (PostgreSQL) and `5672`/`15672` (RabbitMQ) free when using
  `docker-compose.yaml`. The Aspire AppHost picks free ports automatically.

## 8. Getting started

```bash
git clone https://github.com/rmakotoinoue/aspire-payments.git
cd aspire-payments
dotnet restore AspirePayments.slnx
dotnet build AspirePayments.slnx -c Release
```

The first AppHost run needs RabbitMQ credentials stored as user secrets:

```bash
dotnet user-secrets set "Parameters:rabbitmq-user" "admin" --project AspirePayments.AppHost
dotnet user-secrets set "Parameters:rabbitmq-password" "change-me" --project AspirePayments.AppHost
```

Then:

```bash
dotnet run --project AspirePayments.AppHost
```

Open the dashboard URL printed in the console. It shows the API, worker,
PostgreSQL, pgAdmin, RabbitMQ, and the Angular dev server, with links to logs,
traces, and metrics.

## 9. Configuration

### 9.1 Aspire AppHost

| Setting | Where | Purpose |
|---|---|---|
| `Parameters:rabbitmq-user` | AppHost user secrets | RabbitMQ user |
| `Parameters:rabbitmq-password` | AppHost user secrets | RabbitMQ password |

The PostgreSQL password is generated by Aspire and injected into the API and
worker as `ConnectionStrings__postgres`. RabbitMQ is injected as
`ConnectionStrings__messaging`. Secrets are not stored in the repository.

### 9.2 Manual runs

When the services are started outside Aspire, provide the connection strings
through environment variables (double underscore maps to `:` in configuration):

```powershell
$env:ConnectionStrings__postgres = "Host=localhost;Port=5432;Database=payments;Username=postgres;Password=postgres"
$env:ConnectionStrings__messaging = "amqp://admin:admin@localhost:5672/"
$env:ASPNETCORE_ENVIRONMENT = "Development"
```

```bash
export ConnectionStrings__postgres="Host=localhost;Port=5432;Database=payments;Username=postgres;Password=postgres"
export ConnectionStrings__messaging="amqp://admin:admin@localhost:5672/"
export ASPNETCORE_ENVIRONMENT=Development
```

See [`appsettings.Example.json`](appsettings.Example.json) for every supported
option. The main sections are:

| Section | Key | Default | Meaning |
|---|---|---|---|
| `Worker` | `ConsumingEnabled` | `true` | When false, messages stay in the queue |
| `Worker` | `ProcessingDelayMilliseconds` | `0` | Delay between handling and ack |
| `Worker` | `MaxAttempts` | `3` | Attempts before dead-lettering |
| `Worker` | `PrefetchCount` | `1` | Unacknowledged messages per consumer |
| `PaymentGateway` | `SimulatedLatencyMilliseconds` | `250` | Artificial gateway latency |
| `Outbox` | `PollIntervalMilliseconds` | `500` | Outbox polling interval |
| `Outbox` | `BatchSize` | `20` | Messages published per batch |

Compose credentials live in `.env` (copy from `.env.example`). The defaults are
for local development only.

## 10. Running locally

### Option A — Aspire AppHost (recommended)

```bash
dotnet run --project AspirePayments.AppHost
```

This starts PostgreSQL, pgAdmin, RabbitMQ with the management plugin, the API,
the worker, and the Angular dev server. Use the dashboard to inspect traces and
logs, and the resource links to open the API, the frontend, and the RabbitMQ
management UI.

### Option B — Compose infrastructure + manual services

```bash
docker compose up -d
```

Set the environment variables from section 9.2, then run each service:

```bash
dotnet run --project AspirePayments.Api --urls http://localhost:5162
dotnet run --project AspirePayments.Worker --urls http://localhost:5180
```

The frontend:

```bash
cd AspirePayments.Web
npm install
npx ng serve --proxy-config proxy.conf.js
```

The Angular proxy reads `API_HTTP` or `API_HTTPS`; when running outside Aspire,
set `API_HTTP=http://localhost:5162`. The API applies EF Core migrations at
startup, so the first request is ready only after the database is available.

Useful local URLs (manual run):

| Service | URL |
|---|---|
| API | `http://localhost:5162` |
| API health | `http://localhost:5162/health` |
| OpenAPI (Development) | `http://localhost:5162/openapi/v1.json` |
| Worker dev control | `http://localhost:5180/dev/consumer/status` |
| RabbitMQ management | `http://localhost:15672` |
| pgAdmin (Aspire) | link in the dashboard |

## 11. Testing

```bash
# Everything (Docker required)
dotnet test AspirePayments.slnx -c Release

# Unit tests only (no Docker)
dotnet test AspirePayments.Tests --filter "FullyQualifiedName~Unit"
```

Current status, verified on .NET SDK 10.0.401, Windows 11, Docker Desktop:

- **53 tests passed, 0 failed** (27 unit + 26 integration), full suite in about
  15–20 seconds.
- Integration tests use Testcontainers to start real PostgreSQL 17 and RabbitMQ 4
  containers.
- `dotnet build AspirePayments.slnx -c Release` completes with 0 warnings and
  0 errors.
- `npm run build` in `AspirePayments.Web` produces the browser and SSR bundles
  (about 288 kB raw / 81 kB transfer for the initial browser bundle).
- `npx ng test --watch=false` runs the Angular/Vitest suite: 2 tests passed.

Coverage includes idempotent replay and conflicts, concurrent requests, the
failed-payment compensation path, partial and full refunds, refund caps, the
crash-after-commit scenario, retry exhaustion into the dead-letter queue,
trace propagation headers, and the SSE broadcast channel.

> Integration tests fail rather than skip when Docker is unavailable. This is
> intentional: a green suite should mean the real infrastructure was exercised.

## 12. API reference

Base URL in local development: `http://localhost:5162` (or the Aspire-assigned
port). All request and response bodies are JSON.

| Method | Route | Description | Success |
|---|---|---|---|
| `POST` | `/api/orders` | Create an order and request its payment | `202` |
| `GET` | `/api/orders` | List the 50 most recent orders | `200` |
| `GET` | `/api/orders/{orderId}` | Order with payments and refunds | `200` |
| `GET` | `/api/orders/{orderId}/payments` | Payment history of an order | `200` |
| `POST` | `/api/orders/{orderId}/payments` | Request a payment for an existing order | `202` |
| `GET` | `/api/payments/{paymentTransactionId}` | A single payment transaction | `200` |
| `POST` | `/api/orders/{orderId}/refunds` | Refund a captured payment | `202` |
| `GET` | `/api/events` | SSE stream of payment status changes | `200` |

Error semantics:

| Status | When |
|---|---|
| `400` | Validation failure (amount, currency, scenario, idempotency key length, refund above the refundable amount) |
| `404` | Unknown order or payment transaction |
| `409` | Reused idempotency key with a different payload, or an order in a state that cannot accept the operation |

Validation uses DataAnnotations at the edge plus domain checks in the services.
Problem details follow RFC 9110 problem-details shapes.

The OpenAPI document is available in Development at `/openapi/v1.json`.

## 13. Usage examples

### Create an order (asynchronous payment)

```bash
curl -X POST http://localhost:5162/api/orders \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: order-2026-0001" \
  -d '{
    "totalAmount": 149.90,
    "currency": "BRL",
    "paymentMethod": "SimulatedCard",
    "scenario": "Succeed"
  }'
```

`202 Accepted`:

```json
{
  "orderId": "53b75b3c-18b7-4f88-93d2-3133736e180d",
  "paymentTransactionId": "1a1b2631-2386-47aa-96ce-42a0e7350258",
  "idempotencyKey": "order-2026-0001",
  "paymentStatus": "Pending",
  "orderStatus": "PendingPayment",
  "isIdempotentReplay": false,
  "messageId": "2f27c1637fbf4c498e8a20c19a132882"
}
```

Repeating the exact request returns `202` with `isIdempotentReplay: true` and the
same identifiers. Reusing the key with another amount returns `409`.

The header takes precedence over `idempotencyKey` in the body. When neither is
provided, the server generates a key.

### Inspect the order after processing

```bash
curl http://localhost:5162/api/orders/53b75b3c-18b7-4f88-93d2-3133736e180d
```

The order becomes `Paid` and the payment `Succeeded`, with `processingRecords`
showing the claim and the result.

### Partial refund

```bash
curl -X POST http://localhost:5162/api/orders/53b75b3c-18b7-4f88-93d2-3133736e180d/refunds \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: refund-2026-0001" \
  -d '{"amount": 49.90, "scenario": "Succeed", "reason": "Customer request"}'
```

The refund starts as `Requested`; after the worker processes it, the order is
`PartiallyRefunded`. A refund larger than the remaining captured amount returns
`400`. Reusing `refund-2026-0001` with another amount returns `409`.

### Stream status changes

```bash
curl -N http://localhost:5162/api/events
```

```text
event: status
data: {"orderId":"...","paymentStatus":"Succeeded","orderStatus":"Paid", ...}
```

### Pause the worker and inspect a queued message

Development only:

```bash
curl -X POST http://localhost:5180/dev/consumer/pause
curl      http://localhost:5180/dev/consumer/status
```

With the worker paused, the message stays in `payments.requests`. Inspect it via
the RabbitMQ management UI (`Queues → payments.requests → Get messages`) or the
HTTP API. Resume with `/dev/consumer/resume`.

## 14. Payment and refund lifecycle

### Deterministic scenarios

| `scenario` | Gateway result | Observed outcome |
|---|---|---|
| `Succeed` | Approved | Payment `Succeeded`, order `Paid` |
| `Fail` | Declined | Payment `Failed`, order `Cancelled`, compensation recorded |
| `FailAfterCommit` | Approved, then the worker throws after commit | Payment `Succeeded`, message redelivered, a `Duplicate` record proves no side effect was repeated |
| `FailTransient` | Temporary failure on every attempt | Retried 3 times, then routed to `payments.dead`; the payment stays `Pending` |

Refunds accept `Succeed` or `Fail`.

### Order states

```text
PendingPayment ──(approved)──► Paid ──(partial refund)──► PartiallyRefunded
      │                          │                              │
      └──(declined)──► Cancelled └──(full refund)──► Refunded ◄─┘
```

### Payment states

```text
Pending ──► Processing ──► Succeeded
                      └──► Failed
```

A payment is claimed with a conditional update from `Pending` to `Processing`.
Only one worker can win that update; the others record a duplicate and move on.

### Refund states

`Requested` → `Processing` → `Succeeded` / `Failed`. `Compensated` is reserved for
declined payments and always has a null payment transaction reference.

## 15. Reliability design

**Idempotency.** The uniqueness is enforced by PostgreSQL, not by an in-memory
check. Concurrent requests that lose the race clear the EF change tracker, load
the winner, and compare the payload. A replay must match amount, currency,
payment method, and order; otherwise the response is `409`.

**Transactional outbox.** Persisting the business state and the event in one
transaction removes the "database committed but the message was lost" gap. The
publisher uses `FOR UPDATE SKIP LOCKED`, so multiple API/worker instances can
publish safely. Publication is at-least-once; the consumer is idempotent.

**Consumer acknowledgements.** Messages are acknowledged only after the database
transaction commits. A crash between commit and ack causes redelivery, which is
detected as a duplicate. Undeserialisable payloads are dead-lettered; a message
whose transaction no longer exists is logged as discarded and acknowledged.

**Retries.** Transient failures publish the original body to a TTL retry queue
with an incremented `x-attempt` header. After `MaxAttempts`, the consumer rejects
the message without requeue and RabbitMQ routes it to `payments.dead`.

**Compensation.** A declined payment never captured money, so it is not a
refund. The system records a `Compensated` row with a derived idempotency key
(`<payment-key>:compensation`) so redelivery cannot duplicate it.

**SSE broadcast.** Each subscriber owns a bounded channel. A single shared
channel would distribute events across tabs instead of broadcasting them.
Slow clients drop the oldest buffered event rather than blocking publishers.

## 16. Data model

| Table | Purpose |
|---|---|
| `orders` | Order header, total, currency, status, trace id |
| `payment_transactions` | Payment intent, status, scenario, gateway result |
| `payment_processing_records` | One row per attempt/outcome (`Processing`, `Succeeded`, `Failed`, `Duplicate`) |
| `refunds` | Refunds and compensations, with status and reason |
| `outbox_messages` | Pending/published integration events |

Database-enforced uniqueness:

- `ux_payment_transactions_idempotency_key` — one logical payment per key;
- `ux_refunds_idempotency_key` — one refund per key;
- `ux_payment_processing_records_message` — one record per message and state.

Migrations are applied by the API at startup. To create one:

```bash
dotnet ef migrations add <Name> \
  --project AspirePayments.Data \
  --output-dir Migrations \
  --context PaymentsDbContext
```

## 17. Observability

- **Traces** — the API starts the HTTP span; its W3C `traceparent` is persisted in
  the outbox, published as a message header, and resumed by the worker. The
  dashboard shows one trace from HTTP request to message consumption and database
  write.
- **Metrics** — OpenTelemetry metrics from ASP.NET Core, `HttpClient`, and the
  .NET runtime, exported through the Aspire OTLP endpoint.
- **Structured logs** — include `OrderId`, `PaymentTransactionId`, `MessageId`,
  `IdempotencyKey`, `PaymentStatus`, `ProcessingAttempt`, and `TraceId`.
- **Health checks** — `/health` (readiness) and `/alive` (liveness) in Development,
  wired by ServiceDefaults.
- **Dead-letter visibility** — `payments.dead` is a durable queue; inspect depth
  and message headers in the RabbitMQ management UI.

## 18. Security

- No credentials, connection strings, or user secrets are committed. Compose
  credentials come from `.env`; Aspire secrets live in user secrets.
- `.env`, `appsettings.*.Local.json`, build output, and `node_modules` are
  git-ignored.
- Input validation rejects invalid amounts, currencies, scenarios, and oversized
  idempotency keys before they reach the database.
- The API enables HTTPS redirection when an HTTPS endpoint is configured.
- **There is no authentication or authorization.** Every endpoint is public. Do
  not expose this application to the internet as-is.
- The worker's `/dev/consumer/*` endpoints and the API's OpenAPI/health endpoints
  are Development-only and unauthenticated.
- The payment gateway is simulated; no card data is collected, stored, or
  transmitted.

## 19. Limitations

- **Simulated gateway inside a database transaction.** The fake gateway has no
  I/O, so it runs inside the transaction for clarity. A real provider call must
  happen outside the transaction (or use a provider-side idempotency key and a
  saga/state machine).
- **At-least-once publishing.** A crash between publish and commit can publish a
  message twice; consumers are idempotent, but downstream systems must be too.
- **No authentication, rate limiting, or tenant isolation.**
- **Transient retries are not persisted as processing records.** Retry attempts
  are visible in logs and the `x-attempt` header, not in the database. The
  `ProcessingStatus.Retrying` enum value is reserved for that future change.
- **SSE is per-process.** With multiple API instances, a notification is consumed
  by one instance and reaches only the clients connected to it. Production would
  need a dedicated broadcast (for example, a fan-out exchange with per-instance
  queues or a backplane such as Redis/SignalR).
- **No pagination.** `GET /api/orders` returns at most 50 rows.
- **UI text and code comments are in Portuguese.** The public documentation is in
  English; translating the UI is a future improvement.
- **Not production-ready.** No load testing, no backup/restore procedure, no
  deployment manifests, no secrets manager integration.

## 20. Future work

- Replace the simulated gateway with a provider adapter that supports
  idempotency keys, webhooks, and reconciliation.
- Move the gateway call out of the database transaction and model the payment as
  an explicit saga.
- Add authentication/authorization, per-client rate limiting, and request size
  limits.
- Persist retry attempts and expose operational endpoints for replaying
  dead-letter messages.
- Add pagination and filtering to the order listing.
- Add frontend unit tests beyond the default smoke specs and end-to-end tests
  (Playwright), and verify accessibility.
- Add container images, health-check-based orchestration, and infrastructure as
  code for a real environment.
- Publish coverage reports and enforce a minimum threshold in CI.

## 21. License

No `LICENSE` file has been added yet because the owner has not confirmed the
licensing terms. If this repository is published as a portfolio project, the
recommendation is **MIT** — permissive, short, and appropriate for sample code.
Add a `LICENSE` file with the copyright holder's name before publishing.

---

Built as a study of idempotent payment flows with .NET Aspire. Contributions and
corrections are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md).
