# 04 — Migration Strategy

> **Status:** ✅ Completed. **Critical Issue #4** from the audit.

## Why the old implementation was dangerous

```csharp
// Web/Program.cs (BEFORE)
public static async Task Main(string[] args)
{
    var webHost = CreateHostBuilder(args).Build();
    await ApplyMigrations(webHost.Services);   // ⚠ runs on every startup
    await webHost.RunAsync();
}
```

`MigrateAsync` was called unconditionally during application boot. In a real deployment this is unsafe:

- **Race conditions with multiple replicas.** Kubernetes/ECS will start `N` pods in parallel. All `N` of them race to apply the same migration. EF Core's table-level locking helps but isn't airtight under load — deadlocks and partial migrations have been documented in the wild.
- **Long-running migrations block readiness.** A 20-minute column backfill in a migration means 20 minutes of pods refusing to become ready, ramping straight into "service degraded" alerts.
- **Permissions sprawl.** The runtime DB user needed DDL rights (`CREATE`, `ALTER`, `DROP`) just so the app could migrate. Principle of least privilege says runtime should be DML-only.
- **No rollback path.** A failed migration leaves the schema half-applied with no easy recovery.

## What changed

### `src/Conterex.Compliance.Web/Program.cs` — guarded, off by default

```csharp
public class Program
{
    private const string ApplyMigrationsEnvVar = "APPLY_MIGRATIONS_ON_STARTUP";

    public static async Task Main(string[] args)
    {
        var webHost = CreateHostBuilder(args).Build();

        if (ShouldApplyMigrations(args))
        {
            await ApplyMigrationsAsync(webHost.Services);
        }

        await webHost.RunAsync();
    }

    private static bool ShouldApplyMigrations(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--migrate", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        var envValue = Environment.GetEnvironmentVariable(ApplyMigrationsEnvVar);
        return string.Equals(envValue, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ApplyMigrationsAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await dbContext.Database.MigrateAsync();
    }
}
```

**Default behaviour: migrations do NOT run on startup.** Two opt-in mechanisms remain for local convenience:

- Pass `--migrate` as a command-line argument: `dotnet run --project src/Conterex.Compliance.Web -- --migrate`
- Set the environment variable: `$env:APPLY_MIGRATIONS_ON_STARTUP="true"; dotnet run ...`

Production deployments leave both off. Migrations run from the CI/CD pipeline (see below).

### `src/Conterex.Compliance.Infrastructure/DesignTimeDbContextFactory.cs` — new

The DbContext now requires `IPublisher` (MediatR) for domain-event dispatch. EF Core design-time tooling (`dotnet ef migrations add`, `database update`, `migrations bundle`) cannot inject services from the live host because the host throws on missing connection strings. A design-time factory bypasses host DI entirely:

```csharp
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    private const string DesignTimeConnectionString =
        "Host=localhost;Port=5432;Database=ef_design_time_only;Username=ef_tool;Password=ef_tool_placeholder_not_a_real_secret";

    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(DesignTimeConnectionString)
            .Options;
        return new ApplicationDbContext(options, new NoOpPublisher());
    }

    private sealed class NoOpPublisher : IPublisher
    {
        public Task Publish(object n, CancellationToken c = default) => Task.CompletedTask;
        public Task Publish<T>(T n, CancellationToken c = default) where T : INotification => Task.CompletedTask;
    }
}
```

The placeholder connection string is **never** used to issue real SQL — it's needed only for the EF model graph during scaffolding. The factory is `internal sealed` because nothing outside Infrastructure should construct DbContexts this way.

### `dotnet-tools.json` — local EF tool pinned to .NET 6 compatible version

```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "dotnet-ef": {
      "version": "6.0.36",
      "commands": ["dotnet-ef"]
    }
  }
}
```

The system-wide `dotnet ef` may be a newer version that can't load net6 assemblies. Local tooling guarantees the right version is used regardless of which machine runs the command. Install with `dotnet tool restore` after cloning.

## Daily developer workflow

### Add a new migration

```powershell
dotnet tool restore   # once per clone
dotnet dotnet-ef migrations add <Descriptive_Name> `
    --project src/Conterex.Compliance.Infrastructure `
    --startup-project src/Conterex.Compliance.Web
```

The migration appears under `src/Conterex.Compliance.Infrastructure/Migrations/<timestamp>_<Name>.cs`. Review the generated `Up` / `Down` methods before committing. Edit if necessary — EF's scaffolder is convenient, not omniscient.

### Apply migrations locally

```powershell
dotnet dotnet-ef database update `
    --project src/Conterex.Compliance.Infrastructure `
    --startup-project src/Conterex.Compliance.Web
```

This reads your User Secrets / `.env` connection string and applies pending migrations.

For quick iteration only, you may use the env-flag fallback:
```powershell
$env:APPLY_MIGRATIONS_ON_STARTUP = "true"
dotnet run --project src/Conterex.Compliance.Web
```

…but treat this as a development convenience, never a production pattern.

### Remove the latest migration (before commit)

```powershell
dotnet dotnet-ef migrations remove `
    --project src/Conterex.Compliance.Infrastructure `
    --startup-project src/Conterex.Compliance.Web
```

Only works if the migration has not been applied to the database yet. If it has been applied, revert first with `database update <PreviousMigration>` then remove.

### Generate a self-contained migration bundle

The recommended deployment artifact:

```powershell
dotnet dotnet-ef migrations bundle `
    --self-contained `
    --target-runtime linux-x64 `
    --project src/Conterex.Compliance.Infrastructure `
    --startup-project src/Conterex.Compliance.Web `
    --output ./efbundle
```

`efbundle` is a single executable that contains all migrations and an embedded EF runtime. To apply:

```bash
./efbundle --connection "$DATABASE_CONNECTION_STRING"
```

## CI/CD pipeline placement (recommended)

GitHub Actions sketch:

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 6.0.x }
      - run: dotnet tool restore
      - run: dotnet restore Conterex.Compliance.sln
      - run: dotnet build Conterex.Compliance.sln --no-restore -c Release
      - run: |
          dotnet dotnet-ef migrations bundle \
            --self-contained --target-runtime linux-x64 \
            --project src/Conterex.Compliance.Infrastructure \
            --startup-project src/Conterex.Compliance.Web \
            --output ./efbundle
      - uses: actions/upload-artifact@v4
        with: { name: efbundle, path: efbundle }

  migrate:
    needs: build
    runs-on: ubuntu-latest
    environment: production           # protect with required reviewers
    steps:
      - uses: actions/download-artifact@v4
        with: { name: efbundle }
      - run: chmod +x ./efbundle
      - run: ./efbundle --connection "${{ secrets.DATABASE_MIGRATION_CONNECTION_STRING }}"

  deploy:
    needs: migrate
    runs-on: ubuntu-latest
    steps:
      - # ... deploy the new app image to k8s / ECS / App Service ...
```

Key properties:
- Migrations run **before** the new app image is deployed.
- Migrations run **once**, in a single dedicated job — no race conditions.
- The migration job uses a **separate, DDL-privileged DB user**. The runtime app uses a DML-only user.
- The job is gated by environment-required reviewers for production.

## Two-DB-user pattern (recommended)

PostgreSQL setup:

```sql
-- Owner / DDL user, used only by CI migrations
CREATE USER conterex_migrator WITH PASSWORD '<rotated-secret>';
GRANT ALL PRIVILEGES ON DATABASE conterex_compliance TO conterex_migrator;

-- Runtime user, used only by the app
CREATE USER conterex_app WITH PASSWORD '<rotated-secret>';
GRANT CONNECT ON DATABASE conterex_compliance TO conterex_app;
GRANT USAGE ON SCHEMA public TO conterex_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO conterex_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO conterex_app;
```

Two different connection strings flow through two different secrets:
- `DATABASE_MIGRATION_CONNECTION_STRING` — used only by CI migration job
- `DATABASE_RUNTIME_CONNECTION_STRING` (a.k.a. `ConnectionStrings__Application`) — used by the application pods

If the app is ever compromised, the attacker has DML — not DDL.

## Current migration list (after this phase)

```
20210728191856_InitialCreate                 (existing, namespace updated to src/Conterex.Compliance.Infrastructure.Migrations)
20260520073233_Enrich_Webinar_With_Status    (new — adds Status + CancellationReason columns)
```

Listed by:
```powershell
dotnet dotnet-ef migrations list `
    --project src/Conterex.Compliance.Infrastructure `
    --startup-project src/Conterex.Compliance.Web
```

## Files modified / created

| Change | File |
|--------|------|
| MODIFIED | `src/Conterex.Compliance.Web/Program.cs` (guarded, opt-in) |
| NEW | `src/Conterex.Compliance.Infrastructure/DesignTimeDbContextFactory.cs` |
| NEW | `dotnet-tools.json` (pinned EF tool) |
| NEW | `src/Conterex.Compliance.Infrastructure/Migrations/20260520073233_Enrich_Webinar_With_Status.cs` |

## Validation steps

- ✅ `dotnet build` succeeds with no errors.
- ✅ `dotnet run --project src/Conterex.Compliance.Web` with `APPLY_MIGRATIONS_ON_STARTUP` unset does NOT call `MigrateAsync` (verified by code review of `Program.cs`).
- ✅ `dotnet dotnet-ef migrations list ...` returns both migrations.
- ✅ `dotnet dotnet-ef database update ...` (with a real connection string) applies them successfully.
- ✅ `dotnet dotnet-ef migrations bundle ...` produces an executable that runs against a target database.

## Security impact

| Concern | Before | After |
|---------|--------|-------|
| Runtime DB user needs DDL | YES | NO (can be granted DML-only) |
| Race conditions during pod startup | YES | NO |
| Failed migration leaves schema half-applied with running pods | YES | NO (migrations finish before pods come up) |
| Long migrations block readiness | YES | NO |

## Production impact

- The first deploy after this change must include a migration step in the pipeline. Without it, the existing app pods will fail at runtime when accessing the `Status` / `CancellationReason` columns.
- Document **rollback** procedure: `efbundle --connection "..."` accepts an explicit target migration (`--migration <Name>`) to roll back to a known good state.
- Update DB user permissions per the two-user pattern above.

## Not addressed in this phase (deferred)

- The pipeline itself is not committed — the YAML above is a recommendation. Add `.github/workflows/migrate.yml` when the project moves to CI/CD.
- Concurrent-migration locking (e.g. Postgres advisory locks via `pg_try_advisory_lock`) is not implemented — the recommended approach is the dedicated migration job in CI, not in-process locking.
- A `dotnet ef migrations script` snapshot for DBAs to review before applying to production is a useful artifact but not generated by this phase.
