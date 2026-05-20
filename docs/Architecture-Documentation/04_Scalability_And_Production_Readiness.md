# 04 — Scalability and Production Readiness

> Honest answer in one sentence: **this codebase is a study project, not a production application.** It has the right *shape* to grow into something production-grade, but does not currently meet the bar. This file enumerates what's missing and the recommended additions, sized for an enterprise build.

---

## 1. Production-readiness checklist

| Category | Status | Evidence |
|----------|--------|----------|
| **Layered architecture** | ✅ Pass | Clean Architecture; dependency direction correct |
| **CQRS** | ✅ Pass | MediatR with custom `ICommand`/`IQuery` markers |
| **Thin controllers** | ✅ Pass | `WebinarsController` is 54 lines |
| **Centralized exception handling** | ⚠️ Partial | Middleware exists but leaks `exception.Message` |
| **Centralized validation** | ⚠️ Partial | Only on Commands, runs synchronously, rules incomplete |
| **Configuration management** | ❌ Fail | Plaintext secrets in repo |
| **Authentication** | ❌ Fail | None implemented; `UseAuthorization` is a no-op |
| **Authorization** | ❌ Fail | No policies, no roles, no `[Authorize]` usage |
| **HTTPS / TLS** | ⚠️ Partial | `UseHttpsRedirection` configured; cert handling is local-dev only |
| **Logging** | ⚠️ Partial | Default `Microsoft.Extensions.Logging`; no structured logging |
| **Tracing / correlation IDs** | ❌ Fail | None |
| **Metrics** | ❌ Fail | None |
| **Health checks** | ❌ Fail | No `/health` endpoint |
| **Rate limiting** | ❌ Fail | Not configured |
| **CORS** | ❌ Fail | Not configured |
| **Caching** | ❌ Fail | None |
| **Background processing** | ❌ Fail | No `IHostedService`, no Hangfire/Quartz |
| **Async / event-driven** | ❌ Fail | No domain events, no message bus, no Outbox |
| **Database migrations strategy** | ⚠️ Risk | Migrations run on every app startup |
| **Database scalability** | ❌ Fail | Single PostgreSQL, no read replicas, no pooling tuning |
| **Container image quality** | ⚠️ Partial | Multi-stage build ✓; no `HEALTHCHECK`, runs as root |
| **CI/CD** | ❌ Fail | No pipeline files |
| **Infrastructure as code** | ❌ Fail | No Terraform/Bicep/Pulumi/Helm artifacts |
| **Automated tests** | ❌ Fail | No `*.Tests.csproj` in the solution |
| **Secrets management** | ❌ Fail | `UserSecretsId` set but unused; production secrets unhandled |
| **API versioning** | ❌ Fail | Single hard-coded `"v1"` in Swagger |
| **Documentation** | ⚠️ Partial | Swagger with XML comments; no README beyond default |

**Score: 4 pass / 7 partial / 17 fail out of 28.** A typical enterprise readiness gate would want 24+ green.

---

## 2. Horizontal scalability

### What works in your favor

- **Stateless web service.** No in-memory session, no per-instance state. You can run N replicas without affinity.
- **Scoped DbContext per request.** No accidental cross-request state.
- **PostgreSQL** is a well-understood, horizontally-replicable store.

### What blocks scaling today

- **Migration-on-startup race** (`Web/Program.cs:17`). Two replicas starting simultaneously will both call `Database.MigrateAsync()`. EF has some locking but it's not deadlock-free under load. **Fix:** move migrations to a pre-deploy step (see File 03 §4).
- **No connection-pool tuning.** Npgsql defaults work but won't survive a spike. **Fix:**
  ```csharp
  builder.UseNpgsql(connStr, npgsql => npgsql
      .MinPoolSize(10).MaxPoolSize(200)
      .EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null));
  ```
- **No read replica routing.** All queries (Dapper + EF) hit the primary. Once read load matters, you'll want to route `IQuery<T>` handlers to a replica via a different `ISqlConnectionFactory` (see File 03 §5).
- **No caching layer.** Every read is a database round-trip. A `GetWebinarByIdQuery` that's called 1,000 RPS produces 1,000 RPS of database load.
- **No work-queue offloading.** Anything beyond a 200ms request will block the request thread.

### Recommended next steps for horizontal scaling

1. Adopt the read-side abstraction from File 03 §5 so the read repository can target a replica.
2. Add `Microsoft.Extensions.Caching.StackExchangeRedis` and a `CachingBehavior<TRequest, TResponse>` that caches `IQuery` results keyed by their request shape.
3. Use Polly inside Infrastructure for retry / circuit breaker on external dependencies.
4. Tune Npgsql connection pool limits explicitly.
5. Add a Hangfire (or worker-service) consumer for background work; remove anything > 200ms from the request path.

---

## 3. Database scalability

| Concern | Today | Recommendation |
|---------|-------|----------------|
| Schema versioning | Single migration, namespace `Persistence.Migrations` (legacy) | Rename, generate empty migration to clean the snapshot; adopt CI migration bundling (`dotnet ef migrations bundle`) |
| Indexes | None beyond PK | Add indexes covering common WHERE clauses *as queries are added* — don't add speculative indexes |
| Read scaling | Single primary | Add a read replica; expose via a separate connection string; route `IQuery` to it |
| Connection pooling | Defaults | Tune `MinPoolSize`/`MaxPoolSize` to match your worker thread count |
| Concurrency control | None | Add `[Timestamp] byte[] RowVersion` to entities that take writes; handle `DbUpdateConcurrencyException` in handlers |
| Soft delete | None | Either add `IsDeleted` + global query filter, or use Postgres `event sourcing`/audit triggers — pick deliberately, don't add by default |
| Bulk operations | Not handled | For large batches, use `EFCore.BulkExtensions` or raw `INSERT … FROM` via Dapper |
| Maintenance | Not addressed | `VACUUM` / `ANALYZE` schedules, automated backups, point-in-time recovery (PITR) all need ops attention |

---

## 4. Caching readiness

**Status: not implemented.**

### Why you need it

A `GetWebinarById` query that hits a hot ID is a perfect cache target — the answer is bounded in size and changes infrequently. Even a 30-second TTL eliminates the vast majority of database load on read-heavy endpoints.

### Recommended pattern

**Step 1 — Add packages** (Web only):
```xml
<PackageReference Include="Microsoft.Extensions.Caching.StackExchangeRedis" Version="8.x" />
```

**Step 2 — Marker interface** in Application:
```csharp
// Application/Abstractions/Caching/ICachedQuery.cs
public interface ICachedQuery
{
    string CacheKey { get; }
    TimeSpan? Expiration { get; }
}

public interface ICachedQuery<TResponse> : IQuery<TResponse>, ICachedQuery { }
```

**Step 3 — Caching behavior:**
```csharp
internal sealed class CachingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICachedQuery<TResponse>
{
    private readonly IDistributedCache _cache;
    public CachingBehavior(IDistributedCache cache) => _cache = cache;

    public async Task<TResponse> Handle(TRequest request, CancellationToken ct, RequestHandlerDelegate<TResponse> next)
    {
        var bytes = await _cache.GetAsync(request.CacheKey, ct);
        if (bytes is not null)
            return JsonSerializer.Deserialize<TResponse>(bytes)!;

        var response = await next();
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = request.Expiration ?? TimeSpan.FromMinutes(5) };
        await _cache.SetAsync(
            request.CacheKey,
            JsonSerializer.SerializeToUtf8Bytes(response),
            options, ct);
        return response;
    }
}
```

**Step 4 — Opt-in per query:**
```csharp
public sealed record GetWebinarByIdQuery(Guid WebinarId)
    : ICachedQuery<WebinarResponse>
{
    public string CacheKey => $"webinar:{WebinarId}";
    public TimeSpan? Expiration => TimeSpan.FromMinutes(2);
}
```

**Step 5 — Invalidation** in write handlers (or via domain event handler):
```csharp
_cache.Remove($"webinar:{webinarId}");
```

For complex invalidation needs (e.g. "purge all upcoming-webinar lists when any webinar changes"), use cache tagging — Redis supports it natively, `IDistributedCache` does not. You may need to drop down to `IConnectionMultiplexer` for those cases.

---

## 5. Async / background processing readiness

**Status: not implemented.**

### Where you'll need it

- Sending email reminders before scheduled webinars
- Generating reports
- Webhook delivery to external systems
- Outbox publication for integration events

### Recommended pattern — Hangfire on PostgreSQL

Hangfire fits because the database is already PostgreSQL and you avoid adding new infra. Pattern:

```xml
<PackageReference Include="Hangfire.AspNetCore" Version="1.8.x" />
<PackageReference Include="Hangfire.PostgreSql" Version="1.20.x" />
```

```csharp
services.AddHangfire(config => config
    .UsePostgreSqlStorage(configuration.GetConnectionString("Application")));
services.AddHangfireServer(opts => opts.WorkerCount = Environment.ProcessorCount * 2);

app.UseHangfireDashboard("/jobs", new DashboardOptions
{
    Authorization = new[] { new HangfireAdminAuthorizationFilter() } // restrict to Admin role
});

RecurringJob.AddOrUpdate<ISendUpcomingWebinarRemindersJob>(
    recurringJobId: "send-upcoming-reminders",
    methodCall: j => j.ExecuteAsync(CancellationToken.None),
    cronExpression: Cron.Hourly);
```

### Recommended pattern — Outbox for integration events

When the domain emits events that need to reach other services, **never** call them inline from `SaveChangesAsync` (you'll get partial commits — DB succeeds, message bus fails). Persist events to an `OutboxMessages` table in the same transaction, then have a worker publish them:

```csharp
// Schema sketch
CREATE TABLE "OutboxMessages" (
    "Id"           uuid PRIMARY KEY,
    "OccurredOn"   timestamptz NOT NULL,
    "Type"         text NOT NULL,
    "Content"      jsonb NOT NULL,
    "ProcessedOn"  timestamptz NULL,
    "Error"        text NULL
);
```

```csharp
// Inside SaveChangesAsync override (Infrastructure/ApplicationDbContext.cs)
var outboxMessages = ChangeTracker.Entries<AggregateRoot>()
    .SelectMany(e => { var ev = e.Entity.DomainEvents.ToList(); e.Entity.ClearDomainEvents(); return ev; })
    .Select(domainEvent => new OutboxMessage(
        Id: Guid.NewGuid(),
        OccurredOn: DateTime.UtcNow,
        Type: domainEvent.GetType().AssemblyQualifiedName!,
        Content: JsonSerializer.Serialize(domainEvent, ...)))
    .ToList();

Set<OutboxMessage>().AddRange(outboxMessages);
await base.SaveChangesAsync(cancellationToken);  // same transaction
```

A separate Hangfire recurring job (or `BackgroundService`) reads unprocessed rows and publishes them. The pattern guarantees at-least-once delivery; consumers must be idempotent.

---

## 6. Logging and monitoring readiness

**Status: minimal. Built-in console logging only.**

### Recommended stack

| Concern | Recommended package | Notes |
|---------|--------------------|---------|
| Structured logging | **Serilog** (`Serilog.AspNetCore`, `Serilog.Sinks.Console`, `Serilog.Sinks.Seq` / `Serilog.Sinks.Elasticsearch`) | Industry standard; trivial to wire |
| Request logging | `app.UseSerilogRequestLogging()` | One line, gives you method/route/status/duration |
| EF Core query logging | `LogLevel.Debug` on `Microsoft.EntityFrameworkCore.Database.Command` | Use sparingly in prod |
| Distributed tracing | **OpenTelemetry** (`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.EntityFrameworkCore`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`) | W3C TraceContext propagation out of the box |
| Metrics | OpenTelemetry meters + `Microsoft.AspNetCore.Diagnostics.HealthChecks` exporter | Or `prometheus-net.AspNetCore` |
| Backend | **Seq** (local dev), Elastic / Datadog / AppInsights (prod) | Any OTLP-compatible backend |

### Wire-up sketch

```csharp
// Program.cs
builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithCorrelationId()
    .Enrich.WithEnvironmentName()
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("clean-architecture-web"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddNpgsql()
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());

// In the pipeline:
app.UseSerilogRequestLogging();
```

### Log discipline

- **Always** use parameterized templates: `_logger.LogInformation("User {UserId} created webinar {WebinarId}", userId, webinarId);`
- **Never** interpolate: `$"User {userId} created..."` — kills the structured log story.
- **Don't** log secrets, tokens, PII without redaction.
- Set log levels per category in `appsettings.{Env}.json`.

---

## 7. Deployment readiness

| Concern | Today | Recommendation |
|---------|-------|----------------|
| Build artifact | Docker image via multi-stage `Dockerfile` | ✓ keep; harden (see §9) |
| Image registry | None configured | Push to ECR / ACR / GHCR; tag with git SHA |
| Environment matrix | Dev only | Add `appsettings.Staging.json` and `Production.json` (configs only — secrets via env/secret manager) |
| Secret delivery | Plaintext in repo | Secret store + env-var injection (see File 03 §1) |
| Database migrations at deploy | Application calls `MigrateAsync` at startup | Pipeline runs `efbundle` before applying the new app image |
| Rollback | Implicit (rerun previous Docker tag) | Document; rehearse |
| Blue/green or canary | Not configured | Required once auth + observability are in place |

---

## 8. Docker / Kubernetes readiness

### `Web/Dockerfile`

**Current** is a competent multi-stage build but missing:

```dockerfile
# Recommended additions:

# 1. Pin a digest, don't float `:6.0`
FROM mcr.microsoft.com/dotnet/aspnet:6.0@sha256:<digest> AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

# 2. Non-root user
RUN groupadd -r app && useradd -r -g app app
USER app

# 3. Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -q --spider http://localhost/health/live || exit 1
```

Plus: install only `ca-certificates` and dependencies you actually need in the runtime image; don't ship the SDK image.

### Kubernetes (no manifests today)

When you add them, the minimal set:

```yaml
# k8s/deployment.yaml (sketch)
apiVersion: apps/v1
kind: Deployment
metadata: { name: clean-architecture-web }
spec:
  replicas: 3
  selector: { matchLabels: { app: web } }
  template:
    metadata: { labels: { app: web } }
    spec:
      containers:
        - name: web
          image: registry/clean-architecture-web:GIT_SHA
          ports: [{ containerPort: 80 }]
          env:
            - name: ASPNETCORE_ENVIRONMENT
              value: Production
            - name: ConnectionStrings__Application
              valueFrom: { secretKeyRef: { name: db, key: connection-string } }
          resources:
            requests: { cpu: "100m", memory: "256Mi" }
            limits:   { cpu: "1",    memory: "512Mi" }
          livenessProbe:
            httpGet: { path: /health/live,  port: 80 }
            initialDelaySeconds: 10
          readinessProbe:
            httpGet: { path: /health/ready, port: 80 }
            initialDelaySeconds: 5
```

Plus: `Service`, `Ingress`, `NetworkPolicy`, `HorizontalPodAutoscaler`, and a separate `Job` resource for migrations.

---

## 9. CI/CD readiness

**Status: no pipeline files.** No `.github/workflows`, no `azure-pipelines.yml`, no `Jenkinsfile`.

### Minimal pipeline (GitHub Actions example)

```yaml
# .github/workflows/ci.yml
name: ci
on: [push, pull_request]
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 8.0.x }     # see File 06 — upgrade from net6.0
      - run: dotnet restore
      - run: dotnet build --configuration Release --no-restore
      - run: dotnet test --configuration Release --no-build --logger trx
      - uses: dorny/test-reporter@v1
        if: success() || failure()
        with: { name: tests, path: '**/*.trx', reporter: dotnet-trx }
      - run: dotnet ef migrations bundle --project Infrastructure --startup-project Web --self-contained --runtime linux-x64 --output ./efbundle
      - uses: actions/upload-artifact@v4
        with: { name: efbundle, path: efbundle }
      - name: docker build
        run: docker build -t $REGISTRY/clean-architecture-web:${{ github.sha }} -f Web/Dockerfile .
      - name: docker push
        if: github.ref == 'refs/heads/main'
        run: docker push $REGISTRY/clean-architecture-web:${{ github.sha }}
```

Add a separate `deploy` workflow that:
1. Runs `./efbundle` against the target environment.
2. Updates the Kubernetes deployment image tag.

### Quality gates worth automating

- `dotnet build` with `/warnaserror` (after the codebase warning-cleanup pass)
- `dotnet test` (once tests exist)
- `dotnet format --verify-no-changes` for style consistency
- `dotnet list package --vulnerable --include-transitive` for CVE alerts
- A SAST scanner (CodeQL / Snyk Code) on every PR

---

## 10. Observability readiness

Already covered in §6. Three pillars:

1. **Logs** — Serilog with JSON output, correlation IDs, environment + service name enrichment.
2. **Traces** — OpenTelemetry for ASP.NET Core + EF Core + Npgsql, exported via OTLP.
3. **Metrics** — OpenTelemetry meters + the standard `Microsoft.AspNetCore.Hosting`, `System.Runtime`, and custom domain meters (e.g. `webinars.created.total`).

Endpoints to expose:
- `/health/live` — process is alive
- `/health/ready` — dependencies (DB) are reachable
- `/metrics` (optional) — if you use Prometheus scraping rather than push-based OTLP

---

## 11. Security hardening recommendations

| Layer | Recommendation |
|-------|----------------|
| Transport | TLS only; HSTS in production (`app.UseHsts()`); disable HTTP/1.0; review TLS 1.2/1.3 cipher list |
| AuthN | JWT bearer (or OIDC code flow for browser clients); short-lived access tokens; refresh-token rotation |
| AuthZ | Resource-based authorization for any "owner of X" pattern; policy + requirement + handler pattern |
| Input | FluentValidation on every Command **and** any Query that takes complex input; max-length / range / regex; reject unknown JSON fields (`JsonSerializerOptions.UnmappedMemberHandling = Disallow` once moved to .NET 8) |
| Output | `ProblemDetails` only; no raw exception messages in non-development environments |
| Secrets | Vault / Secrets Manager / Key Vault; rotate quarterly minimum; never in source control |
| Headers | `app.UseSecurityHeaders(...)` (`NWebsec` or `OwaspHeaders.Core`) — adds CSP, X-Content-Type-Options, X-Frame-Options, Referrer-Policy |
| Rate limiting | `services.AddRateLimiter(...)` (built-in on .NET 7+); per-IP and per-user buckets |
| Audit log | Add `IAuditLogger`; log every command with actor + timestamp + payload (redacted as needed) |
| DB | Least-privilege user for runtime; separate user for migrations; row-level security if multi-tenant |
| Dependencies | `dotnet list package --vulnerable`; renovate-bot or dependabot on every push |
| Threat model | Document expected actors, abuse cases, controls — even a one-pager is better than nothing |

OWASP API Security Top 10 mapping (current state):
- **API1: Broken Object Level Authorization** — ❌ no authorization at all
- **API2: Broken Authentication** — ❌ no authentication at all
- **API3: Excessive Data Exposure** — ⚠️ `SELECT *` query (File 03 §13) returns whatever's in the table
- **API4: Lack of Resources & Rate Limiting** — ❌ no rate limiting
- **API5: Broken Function Level Authorization** — ❌ N/A (no auth)
- **API6: Mass Assignment** — ✅ command records are explicit
- **API7: Security Misconfiguration** — ❌ secrets in repo, dead auth middleware, `AllowedHosts: "*"`
- **API8: Injection** — ✅ Parameterized SQL throughout (the only raw SQL uses `@WebinarId` parameter)
- **API9: Improper Assets Management** — ⚠️ no API versioning, no inventory
- **API10: Insufficient Logging & Monitoring** — ❌ no structured logs, no central log store

---

## 12. Recommended future architecture path

If this codebase is the seed for a real product, the realistic 12-month evolution looks like this:

### Phase 0 — stabilize (weeks 1-2)
- Remove secrets, add auth, fix migration-on-startup, fix `ProblemDetails`, extract DI extension methods, minimal hosting.
- Add Serilog + health checks + correlation IDs.
- Outcome: production-deployable single instance.

### Phase 1 — enrich the domain (weeks 3-6)
- `AggregateRoot` + `IDomainEvent` + dispatcher.
- Convert `Webinar` to a real aggregate; add `Speaker`, `Registration`.
- Full read-side abstraction (`ISqlConnectionFactory`, `IWebinarReadRepository`).
- Add MediatR Logging + Performance behaviors.
- Add `Domain.UnitTests` + `Application.UnitTests`.
- Outcome: feature velocity becomes about adding *use cases*, not plumbing.

### Phase 2 — modular monolith (months 2-4)
- Group features into bounded contexts: `Modules/Webinars`, `Modules/Identity`, `Modules/Registrations`, etc.
- Each module gets its own folder structure mirroring the current layering.
- Inter-module communication via domain events (in-process MediatR) or a shared `IPublicApi` per module.
- Outcome: clear module boundaries; new team members onboard one module at a time; future microservice extraction has well-defined seams.

### Phase 3 — async + integration events (months 4-6)
- Outbox table + worker for cross-module / cross-service events.
- Hangfire (or worker service) for scheduled and ad-hoc jobs.
- Move email, reporting, webhook delivery off the request path.
- Outcome: response times stop being bound by external-system latency.

### Phase 4 — observability hardening (month 6 onward)
- OpenTelemetry traces + metrics in production.
- SLO / SLI definitions per endpoint.
- Alerting rules in your monitoring backend.
- Load tests in CI (`k6`, `NBomber`).
- Outcome: you find regressions before customers do.

### Phase 5 — selective microservice extraction (month 9+)
- *Only* extract a module if it has a different scaling profile, ownership, or release cadence.
- The Outbox / domain-event boundary from Phase 3 makes extraction cheap when it's actually justified.
- Outcome: a microservice architecture motivated by real constraints, not architecture-astronaut tendencies.

**Do not** start at Phase 5. Most "microservice-first" projects collapse under the operational complexity before they ship Phase 1.

---

## 13. Production-readiness sign-off matrix

When you are ready to ship, every row below should be **yes**:

| # | Question | Yes/No |
|---|----------|--------|
| 1 | No secrets in source control? | |
| 2 | All write endpoints behind authentication? | |
| 3 | Resource-level authorization in place? | |
| 4 | Validation on every Command and complex Query? | |
| 5 | Centralized exception handling returning `ProblemDetails`? | |
| 6 | Structured logging with correlation IDs? | |
| 7 | OpenTelemetry traces exported to a viewable backend? | |
| 8 | Health-check endpoints (`/live`, `/ready`)? | |
| 9 | Migrations executed by deploy pipeline (not app startup)? | |
| 10 | Runtime DB user is DML-only? | |
| 11 | At least 70% unit test coverage on Domain + Application? | |
| 12 | Integration tests covering the critical write paths? | |
| 13 | Load test demonstrating SLO compliance at expected peak? | |
| 14 | CI pipeline gates on build, test, format, vulnerability scan? | |
| 15 | Container image runs as non-root with a `HEALTHCHECK`? | |
| 16 | Disaster-recovery procedure documented and rehearsed? | |
| 17 | Backup + point-in-time-recovery configured? | |
| 18 | Rate limiting on public endpoints? | |
| 19 | TLS-only with HSTS in production? | |
| 20 | Configuration validation at startup (fail fast on missing settings)? | |

Current state of this codebase: **0 of 20.** That number isn't an insult — it's a roadmap.
