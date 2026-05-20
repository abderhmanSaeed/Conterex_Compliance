# 01 — Security Refactor (Secrets out of source control)

> **Status:** ✅ Completed. **Critical Issue #1** from the audit.

## Why the old implementation was dangerous

Two files in the repository carried plaintext database credentials:

```jsonc
// Web/appsettings.Development.json (BEFORE)
"ConnectionStrings": {
    "Application": "Host=clean_architecture.db;Port=5432;Database=webinar;User Id=postgres;Password=postgres"
}
```

```yaml
# docker-compose.yml (BEFORE)
environment:
  - POSTGRES_DB=webinar
  - POSTGRES_USER=postgres
  - POSTGRES_PASSWORD=postgres
```

Anyone who cloned the repository inherited working DB credentials. SAST scanners flag this immediately. Secret rotation becomes a social process (DM everyone) rather than a technical one (rotate the vault entry). The `<UserSecretsId>` declared in `src/Conterex.Compliance.Web.csproj` had been set up but **never used** — User Secrets was effectively dead infrastructure.

## What changed

### `src/Conterex.Compliance.Web/appsettings.Development.json` — credentials removed

**Before:**
```jsonc
{
  "Logging": { /* ... */ },
  "ConnectionStrings": {
    "Application": "Host=clean_architecture.db;...;Password=postgres"
  }
}
```

**After:**
```jsonc
{
  "Logging": { /* unchanged */ }
}
```

Connection strings, JWT keys, and dev user creds are now sourced from **user-secrets** (locally) or **environment variables** (in containers / deployed environments).

### `src/Conterex.Compliance.Web/appsettings.json` — structured placeholders

```jsonc
{
  "Logging": { /* ... */ },
  "AllowedHosts": "*",
  "ConnectionStrings": { "Application": "" },
  "Jwt": { "Issuer": "", "Audience": "", "SigningKey": "", "AccessTokenLifetimeMinutes": 60 },
  "Dev": { "Email": "", "Password": "" }
}
```

The keys exist (so config schema is discoverable) but the values are empty. Empty values **cannot pass** the fail-fast guards added below.

### `.env.example` — committed template, no values

```bash
POSTGRES_DB=conterex_compliance
POSTGRES_USER=
POSTGRES_PASSWORD=

DB_CONNECTION_STRING=Host=conterex_compliance.db;Port=5432;Database=conterex_compliance;User Id=<user>;Password=<password>

JWT_ISSUER=https://localhost
JWT_AUDIENCE=src/Conterex.Compliance.Web
JWT_SIGNING_KEY=
JWT_ACCESS_TOKEN_LIFETIME_MINUTES=60

DEV_USER_EMAIL=dev@example.com
DEV_USER_PASSWORD=
```

Developers copy this to `.env` (gitignored) and fill in values. The same `.env` file feeds Docker Compose.

### `.gitignore` — `.env` ignored, `.env.example` kept

```gitignore
# Local environment variables (Foundation Hardening: no secrets in repo)
.env
!.env.example
```

### `docker-compose.yml` — env-driven, no inline secrets

**Before:**
```yaml
environment:
  - POSTGRES_DB=webinar
  - POSTGRES_USER=postgres
  - POSTGRES_PASSWORD=postgres
```

**After:**
```yaml
services:
  conterex_compliance.web:
    env_file: .env
    environment:
      - ConnectionStrings__Application=${DB_CONNECTION_STRING}
      - Jwt__Issuer=${JWT_ISSUER}
      - Jwt__Audience=${JWT_AUDIENCE}
      - Jwt__SigningKey=${JWT_SIGNING_KEY}
      - Jwt__AccessTokenLifetimeMinutes=${JWT_ACCESS_TOKEN_LIFETIME_MINUTES}
      - Dev__Email=${DEV_USER_EMAIL}
      - Dev__Password=${DEV_USER_PASSWORD}
    # ...
  conterex_compliance.db:
    env_file: .env
    environment:
      - POSTGRES_DB=${POSTGRES_DB}
      - POSTGRES_USER=${POSTGRES_USER}
      - POSTGRES_PASSWORD=${POSTGRES_PASSWORD}
```

Docker Compose interpolates the `${VAR}` references from the local `.env`. No values survive in the repo.

### `src/Conterex.Compliance.Web/Startup.cs` — fail-fast guard

```csharp
var connectionString = Configuration.GetConnectionString("Application");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Application is not configured. " +
        "Set it via dotnet user-secrets (local development) or environment variables " +
        "(Docker / deployed environments). See README.md for details.");
}
services.AddDbContext<ApplicationDbContext>(b => b.UseNpgsql(connectionString));
```

Plus the `AuthenticationServiceCollectionExtensions.AddConterexAuthentication` method (see file 02) enforces a `Jwt:SigningKey` of at least 32 characters via DataAnnotations + `ValidateOnStart()`.

### `src/Conterex.Compliance.Web.csproj` — preserved UserSecretsId

The `<UserSecretsId>540db5be-b2a8-4c4d-ace3-5761b60b3c97</UserSecretsId>` declaration was already present and is preserved. It binds the project to a local secret store on each developer's machine.

## Files modified / created

| Change | File |
|--------|------|
| MODIFIED | `src/Conterex.Compliance.Web/appsettings.Development.json` (secrets removed) |
| MODIFIED | `src/Conterex.Compliance.Web/appsettings.json` (structured placeholders) |
| MODIFIED | `src/Conterex.Compliance.Web/Startup.cs` (fail-fast guard) |
| MODIFIED | `docker-compose.yml` (env-driven) |
| MODIFIED | `.gitignore` (ignore `.env`) |
| NEW      | `.env.example` (template) |
| NEW      | `README.md` (local setup instructions) |

## Migration steps for existing developers

1. Pull the latest branch.
2. Run `dotnet user-secrets set "ConnectionStrings:Application" "<your-conn-string>" --project src/Conterex.Compliance.Web`.
3. Run `dotnet user-secrets set "Jwt:Issuer" "https://localhost" --project src/Conterex.Compliance.Web` and the matching keys for `Jwt:Audience`, `Jwt:SigningKey`, `Dev:Email`, `Dev:Password`.
4. For Docker workflows: `cp .env.example .env` and fill in values.
5. **Rotate** the previously-leaked credentials (the old `postgres/postgres` pair was in version control).

## Validation steps

- ✅ `dotnet build` succeeds with no errors.
- ✅ `dotnet run --project src/Conterex.Compliance.Web` **without** user-secrets configured throws `InvalidOperationException: ConnectionStrings:Application is not configured...` at boot — fail-fast confirmed.
- ✅ With user-secrets populated, the host starts normally.
- ✅ `grep -rn "Password=postgres" Conterex.Compliance.* docker-compose*.yml appsettings*` returns no matches.
- ✅ `.env` does not appear in `git status` after a `cp .env.example .env` (gitignored).

## Security impact

| Concern | Before | After |
|---------|--------|-------|
| Plaintext credentials in source | YES | NO |
| Same credentials across all developers | YES | NO (each developer's user-secrets is independent) |
| Credentials leak if repo is cloned | YES | NO |
| Rotation requires social coordination | YES | NO (rotate vault / user-secrets, code unchanged) |
| Production secret delivery path | undefined | environment variables → `Configuration.GetConnectionString` |
| Boot-time failure on misconfiguration | unhelpful (`UseNpgsql(null)`) | clear `InvalidOperationException` with remediation hint |

## Production impact

For production deployments:
- Secrets must be injected as environment variables by the orchestration layer (Kubernetes secrets, Azure Key Vault references, AWS Secrets Manager).
- `Configuration.GetConnectionString("Application")` reads from `ConnectionStrings__Application` (note double underscore — .NET's env-var mapping convention).
- The fail-fast guard means a misconfigured pod fails its readiness probe rather than silently serving 500s.

## Not addressed in this phase (deferred)

- Rotating the previously-leaked credentials must be done **outside the repo**. This document cannot rotate them for you.
- Migrating from User Secrets to a production secret manager (Azure Key Vault, etc.) is out of scope; only the local-development path is implemented here.
- `AllowedHosts: "*"` is still permissive (see Audit File 03 §14) — fix in a future phase.
