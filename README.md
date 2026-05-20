# Conterex.Compliance

A .NET 6 Clean Architecture backend for the Conterex Compliance Management System. Currently exposes a single aggregate (`Webinar`) as a worked example; future modules will build on the same foundation.

## Architecture overview

Clean Architecture / Onion-style layering, with dependencies pointing inward:

```
Web (composition root)
  ├── Presentation  ──▶  Application  ──▶  Domain
  └── Infrastructure ─▶  Application  ──▶  Domain
```

- **Domain** — entities, aggregate roots, value objects, domain events, repository contracts. Zero NuGet dependencies.
- **Application** — CQRS commands/queries with MediatR, FluentValidation pipeline behavior, application abstractions (`IDateTimeProvider`, `ICurrentUserService`, `IJwtTokenGenerator`, `IUserStore`).
- **Infrastructure** — EF Core DbContext (PostgreSQL via Npgsql), entity configurations, repositories, migrations, system services (`SystemDateTimeProvider`), MediatR domain-event dispatch via overridden `SaveChangesAsync`.
- **Presentation** — thin ASP.NET MVC controllers (`WebinarsController`, `AuthController`) that delegate to MediatR via the `Sender` lazy property.
- **Web** — ASP.NET host: DI composition, middleware pipeline, Swagger, JWT bearer authentication, exception handling.

Domain events are pure markers (`IDomainEvent`) wrapped in `DomainEventNotification<T>` for MediatR dispatch — Domain stays MediatR-free. The repository pattern keeps Application off the ORM. The Unit of Work is the DbContext itself, aliased via DI.

## Technologies

| Concern | Technology |
|---------|------------|
| Runtime | .NET 6 (EOL — upgrade to .NET 8 LTS planned) |
| Web framework | ASP.NET Core MVC |
| ORM (writes) | Entity Framework Core 6 + Npgsql |
| Query library (reads) | Dapper |
| Database | PostgreSQL 13+ |
| Mediator / CQRS | MediatR 10 |
| Validation | FluentValidation 11 |
| Object mapping | Mapster 7 |
| Authentication | JWT Bearer (HS256) |
| API docs | Swashbuckle (Swagger UI) |
| Container | Docker (multi-stage) |
| Migrations tool | `dotnet-ef` 6.0.36 (pinned in `dotnet-tools.json`) |

## Prerequisites

- [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0) (a later phase will upgrade to .NET 8 LTS)
- [PostgreSQL 13+](https://www.postgresql.org/download/) — local or via Docker
- [Docker](https://docs.docker.com/get-docker/) (optional, for the containerised flow)

## Local setup (without Docker)

> All secrets are sourced from **User Secrets** locally. Nothing is committed to the repo.

```powershell
# 1. Restore the locally-pinned dotnet-ef tool
dotnet tool restore

# 2. Configure User Secrets (one-time per developer, replace placeholders with real values)
dotnet user-secrets set "ConnectionStrings:Application" "Host=localhost;Port=5432;Database=conterex_compliance;User Id=postgres;Password=<your-local-pwd>" --project src/Conterex.Compliance.Web

dotnet user-secrets set "Jwt:Issuer"     "https://localhost"                       --project src/Conterex.Compliance.Web
dotnet user-secrets set "Jwt:Audience"   "Conterex.Compliance.Web"                  --project src/Conterex.Compliance.Web
dotnet user-secrets set "Jwt:SigningKey" "<at-least-32-chars-of-high-entropy-data>" --project src/Conterex.Compliance.Web
dotnet user-secrets set "Jwt:AccessTokenLifetimeMinutes" "60"                        --project src/Conterex.Compliance.Web

dotnet user-secrets set "Dev:Email"    "dev@example.com"     --project src/Conterex.Compliance.Web
dotnet user-secrets set "Dev:Password" "<choose-a-dev-password>" --project src/Conterex.Compliance.Web

# 3. Apply migrations to a clean DB
dotnet dotnet-ef database update --project src/Conterex.Compliance.Infrastructure --startup-project src/Conterex.Compliance.Web

# 4. Run the API
dotnet run --project src/Conterex.Compliance.Web
```

Swagger is at `http://localhost:5000/swagger`.

If the connection string is missing the app fails fast at boot:
```
Unhandled exception. System.InvalidOperationException:
ConnectionStrings:Application is not configured. Set it via dotnet user-secrets ...
```
That's the expected behaviour — see [`docs/Architecture-Documentation/Foundation-Hardening/01_Security_Refactor.md`](docs/Architecture-Documentation/Foundation-Hardening/01_Security_Refactor.md).

## Local setup (with Docker Compose)

```powershell
# 1. Copy the env template and fill in real values
cp .env.example .env
# Edit .env in your editor of choice. Required keys:
#   POSTGRES_USER, POSTGRES_PASSWORD, DB_CONNECTION_STRING
#   JWT_SIGNING_KEY (>= 32 chars), JWT_ISSUER, JWT_AUDIENCE
#   DEV_USER_EMAIL, DEV_USER_PASSWORD

# 2. Start the stack
docker compose up --build

# 3. (one-time, in a separate shell) apply migrations against the running container
dotnet dotnet-ef database update --project src/Conterex.Compliance.Infrastructure --startup-project src/Conterex.Compliance.Web
```

`.env` is gitignored. `.env.example` is committed (placeholders only) so new contributors know what to fill in.

## Getting a dev access token

```bash
curl -X POST http://localhost:5000/api/auth/login \
    -H "Content-Type: application/json" \
    -d '{"email":"dev@example.com","password":"<your-dev-password>"}'
# → 200 OK { "accessToken": "<jwt>", "expiresAtUtc": "..." }
```

Use the returned `accessToken` as `Authorization: Bearer <jwt>` against protected endpoints:

```bash
TOKEN="<paste-jwt-here>"
curl -X POST http://localhost:5000/api/webinars \
    -H "Content-Type: application/json" \
    -H "Authorization: Bearer $TOKEN" \
    -d '{"name":"Compliance 101","scheduledOn":"2099-01-01T10:00:00Z"}'
# → 201 Created (Location header: /api/webinars/<id>)

curl http://localhost:5000/api/webinars/<id>
# → 200 OK { "id": "...", "name": "Compliance 101", "scheduledOn": "2099-..." }
```

`GET /api/webinars/{id}` is intentionally `[AllowAnonymous]` to demonstrate mixing protected and public endpoints. `POST /api/webinars` requires a valid token.

> The login flow consults a DEV-only `IUserStore` that accepts exactly one hardcoded user from configuration. Replace before any production deployment. See [`docs/Architecture-Documentation/Foundation-Hardening/02_Authentication_Implementation.md`](docs/Architecture-Documentation/Foundation-Hardening/02_Authentication_Implementation.md).

## EF Core migrations

```powershell
# Add a new migration
dotnet dotnet-ef migrations add <Descriptive_Name> --project src/Conterex.Compliance.Infrastructure --startup-project src/Conterex.Compliance.Web

# Apply pending migrations locally
dotnet dotnet-ef database update --project src/Conterex.Compliance.Infrastructure --startup-project src/Conterex.Compliance.Web

# Remove the latest migration (only if not yet applied)
dotnet dotnet-ef migrations remove --project src/Conterex.Compliance.Infrastructure --startup-project src/Conterex.Compliance.Web

# Produce a self-contained bundle for CI/CD
dotnet dotnet-ef migrations bundle --self-contained --target-runtime linux-x64 --project src/Conterex.Compliance.Infrastructure --startup-project src/Conterex.Compliance.Web --output ./efbundle
```

**Migrations no longer run on startup.** Default behaviour is *no auto-migration*. For local convenience you can opt in with either `--migrate` argument or `APPLY_MIGRATIONS_ON_STARTUP=true`. Production deployments apply migrations from the CI/CD pipeline. See [`docs/Architecture-Documentation/Foundation-Hardening/04_Migration_Strategy.md`](docs/Architecture-Documentation/Foundation-Hardening/04_Migration_Strategy.md).

## Repository layout

```
Conterex.Compliance.sln                       ← solution file
docker-compose.yml / .override.yml / .dcproj  ← container orchestration
.env.example                                  ← required env vars (placeholders only)
README.md                                     ← this file
dotnet-tools.json                             ← pinned dotnet-ef tool

src/
  Conterex.Compliance.Domain/         ← entities, aggregates, domain events, abstractions
  Conterex.Compliance.Application/    ← CQRS commands/queries, validators, app contracts
  Conterex.Compliance.Infrastructure/ ← EF Core DbContext, repos, migrations, integrations
  Conterex.Compliance.Presentation/   ← thin controllers (Webinars, Auth)
  Conterex.Compliance.Web/            ← composition root (DI, middleware, hosting, Dockerfile)

tests/                                ← test projects land here (none yet)

docs/
  Architecture-Documentation/         ← architecture audit + Foundation-Hardening change log
```

Detailed architecture documentation lives in [`docs/Architecture-Documentation/`](docs/Architecture-Documentation/). The deep-dive audit and the Foundation Hardening change log are both there.

## Project commands quick reference

```powershell
dotnet tool restore                          # restores the pinned dotnet-ef tool
dotnet build Conterex.Compliance.sln          # builds the full solution
dotnet run --project src/Conterex.Compliance.Web   # runs the API
```

## Contributing

Conventions are documented in [`docs/Architecture-Documentation/02_Project_Structure_Guide.md`](docs/Architecture-Documentation/02_Project_Structure_Guide.md). The short version:
- `sealed record` for DTOs, commands, queries; `internal sealed class` for handlers.
- Aggregate roots use the `Webinar.Create` pattern (private constructor + static factory + raised domain event).
- Authentication is `[Authorize]` by default at the controller level; opt out with `[AllowAnonymous]` on individual actions.
- Secrets never enter source control.
