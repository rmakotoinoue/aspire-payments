# Contributing

Thanks for taking the time to look at this project. It is a portfolio-scale
reference implementation, so the bar for changes is: keep it small, keep it
explainable, and keep the tests honest.

## Prerequisites

- .NET SDK 10.0.4xx (the exact version is pinned in `global.json`)
- Docker Desktop (or another Docker engine) for PostgreSQL, RabbitMQ, and the
  Testcontainers-based integration tests
- Node.js 22+ and npm 11+ to work on the Angular frontend

## Getting started

```bash
git clone https://github.com/rmakotoinoue/aspire-payments.git
cd aspire-payments
dotnet restore AspirePayments.slnx
dotnet build AspirePayments.slnx -c Release
```

The easiest way to run everything is the Aspire AppHost:

```bash
dotnet run --project AspirePayments.AppHost
```

The first run asks for the RabbitMQ credentials. They are stored as user
secrets in `AspirePayments.AppHost` and are never committed. Alternatively,
start only the infrastructure and run the services manually:

```bash
docker compose up -d
cp .env.example .env   # adjust the credentials if you want
```

See the [README](README.md#10-running-locally) for the connection strings and
environment variables used by manual runs.

## Tests

```bash
# Unit tests only (no Docker required)
dotnet test AspirePayments.Tests --filter "FullyQualifiedName~Unit"

# Full suite, including PostgreSQL and RabbitMQ test containers
dotnet test AspirePayments.slnx -c Release
```

Integration tests start real containers through Testcontainers. If Docker is not
available they fail by design instead of silently skipping, so CI and local runs
exercise the same behaviour.

## Database migrations

The API applies migrations at startup. After changing an entity or a mapping,
add a migration explicitly committed to the repository:

```bash
dotnet ef migrations add <MigrationName> \
  --project AspirePayments.Data \
  --output-dir Migrations \
  --context PaymentsDbContext
```

Keep the generated migration in the same pull request as the model change.

## Code style

- `.editorconfig` defines the C# conventions; run `dotnet format` before
  opening a pull request when you touched C# code.
- Keep comments and UI text in Portuguese to match the rest of the application.
  The public README is in English on purpose.
- Prefer async APIs and `CancellationToken` propagation in new code.
- Do not add a package unless the standard library or an existing dependency
  cannot do the job.

## Pull requests

1. Keep the change focused; unrelated refactors belong in a separate pull
   request.
2. Add or update tests for behaviour changes.
3. Make sure `dotnet build AspirePayments.slnx -c Release` and the full test
   suite pass locally.
4. Describe the trade-offs in the pull request. This repository is meant to be
   read, so the reasoning matters as much as the diff.

## Security

Please do not open a public issue for a suspected vulnerability. Report it
privately to the repository owner. Never commit credentials, `.env` files, user
secrets, or production connection strings.
