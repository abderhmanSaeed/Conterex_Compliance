# 03 — Architecture Problems & Recommendations

> A complete, evidence-based audit. Every finding is anchored to actual files and line numbers. Severity legend: 🚨 **Critical** (block production), ⚠️ **High** (fix before scaling), 🟡 **Medium** (fix before second major feature), 🔵 **Low** (polish).

**Total findings: 28** — 4 Critical, 5 High, 9 Medium, 10 Low.

---

## Table of Contents

- [Critical findings](#critical-findings)
  1. 🚨 Plaintext database credentials in version control
  2. 🚨 Authorization registered without authentication
  3. 🚨 Anemic domain model
  4. 🚨 Database migrations executed on application startup
- [High findings](#high-findings)
  5. ⚠️ Infrastructure leakage: `IDbConnection` and raw SQL in Application layer
  6. ⚠️ Repository is write-only and synchronous
  7. ⚠️ Validation rules don't match persistence contract
  8. ⚠️ `ExceptionHandlingMiddleware` leaks `exception.Message` to clients
  9. ⚠️ Only a single MediatR pipeline behavior
- [Medium findings](#medium-findings)
  10. 🟡 Namespace mismatch in EF migrations
  11. 🟡 DI registration concentrated in `Startup.cs`
  12. 🟡 Duplicate `CreateWebinarRequest` vs `CreateWebinarCommand`
  13. 🟡 `SELECT *` in Dapper read query
  14. 🟡 `AllowedHosts: "*"`
  15. 🟡 Mapster dependency declared in `Application` but unused
  16. 🟡 Legacy `Startup.cs` pattern on .NET 6
  17. 🟡 `ValidationBehavior` runs validators synchronously
  18. 🟡 `ValidationBehavior` only applies to Commands (not Queries)
- [Low findings](#low-findings)
  19. 🔵 No correlation-id middleware
  20. 🔵 No structured logging
  21. 🔵 No health checks
  22. 🔵 No CORS configuration
  23. 🔵 Docker image runs as root with no `HEALTHCHECK`
  24. 🔵 Floating `postgres:13.2` image tag
  25. 🔵 Typo in launchSettings.json profile (`OnionArchitecutre.Web`)
  26. 🔵 No nullable reference type enforcement
  27. 🔵 No global usings / file-scoped namespaces inconsistency
  28. 🔵 No automated tests

---

## Critical findings

### 1. 🚨 Plaintext database credentials in version control

**Location:** `Web/appsettings.Development.json:10` and `docker-compose.yml:21-22`

**Issue:** Database credentials are committed in plaintext.

```jsonc
// Web/appsettings.Development.json
"ConnectionStrings": {
    "Application": "Host=clean_architecture.db;Port=5432;Database=webinar;User Id=postgres;Password=postgres"
}
```

```yaml
# docker-compose.yml
environment:
  - POSTGRES_DB=webinar
  - POSTGRES_USER=postgres
  - POSTGRES_PASSWORD=postgres
```

The `Web.csproj` even declares `<UserSecretsId>540db5be-b2a8-4c4d-ace3-5761b60b3c97</UserSecretsId>` — the User Secrets mechanism is *enabled but unused*.

**Impact:** Any clone of the repo grants direct PostgreSQL access. Auditors and SAST scanners will flag this. In a multi-developer org, secret rotation becomes social rather than technical.

**Recommended fix — User Secrets for local dev, environment variables in deployed environments:**

Remove the connection string from `appsettings.Development.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  }
}
```

Store dev credentials in User Secrets (per developer, never committed):
```powershell
dotnet user-secrets set --project Web "ConnectionStrings:Application" `
    "Host=localhost;Port=5432;Database=webinar;User Id=postgres;Password=<your-local-pwd>"
```

Replace `docker-compose.yml` env vars with a `.env` file (gitignored) or Docker secrets:
```yaml
environment:
  - POSTGRES_DB=${POSTGRES_DB}
  - POSTGRES_USER=${POSTGRES_USER}
  - POSTGRES_PASSWORD=${POSTGRES_PASSWORD}
```
…and add `.env` to `.gitignore` with a committed `.env.example` showing required keys.

In production, source the connection string from a secrets manager (Azure Key Vault, AWS Secrets Manager, HashiCorp Vault), bound through environment variables on the container:
```yaml
# k8s/deployment.yaml fragment
env:
  - name: ConnectionStrings__Application
    valueFrom:
      secretKeyRef:
        name: clean-architecture-db
        key: connection-string
```

**Refactoring steps:**
1. Rotate the current `postgres/postgres` credentials immediately (they're now public to anyone who has cloned the repo).
2. `dotnet user-secrets init --project Web` (already done — the UserSecretsId exists).
3. Move dev secrets to user-secrets.
4. Add `.env` + `.env.example`; update `docker-compose.yml` to reference the `.env` keys.
5. Add a startup check that fails loudly if `ConnectionStrings:Application` is missing — never silently fall back.

---

### 2. 🚨 Authorization registered without authentication

**Location:** `Web/Startup.cs:85`

**Issue:** `app.UseAuthorization()` is in the pipeline, but the codebase has **no** `services.AddAuthentication(...)`, **no** `services.AddAuthorization(...)`, **no** `app.UseAuthentication()`, and **no** `[Authorize]` attributes anywhere.

```csharp
// Web/Startup.cs:79-87 (current)
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();          // ⚠ no-op — nothing to authorize against
app.UseEndpoints(endpoints => endpoints.MapControllers());
```

**Impact:** Every endpoint is anonymous. The middleware misleads readers into thinking the API is protected. The codebase advertises a security posture it does not have.

**Recommended fix — implement JWT bearer authentication, or remove the middleware line:**

If authentication is genuinely needed (the realistic case for a webinar platform):
```csharp
// Web/Extensions/AuthenticationServiceCollectionExtensions.cs (new)
public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var jwt = configuration.GetSection("Jwt").Get<JwtOptions>()
            ?? throw new InvalidOperationException("Missing Jwt configuration section.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        services.AddAuthorization(o =>
        {
            o.AddPolicy("Admin", p => p.RequireRole("Admin"));
            o.AddPolicy("Organizer", p => p.RequireClaim("permission", "webinars.manage"));
        });

        return services;
    }
}

public sealed record JwtOptions(string Issuer, string Audience, string SigningKey);
```

Wire into `Startup.ConfigureServices`:
```csharp
services.AddJwtAuthentication(Configuration);
```

Update the pipeline:
```csharp
app.UseRouting();
app.UseAuthentication();   // BEFORE UseAuthorization
app.UseAuthorization();
app.UseEndpoints(endpoints => endpoints.MapControllers());
```

Decorate controllers:
```csharp
[Authorize]                                       // default: any authenticated user
public sealed class WebinarsController : ApiController { ... }

[HttpPost, Authorize(Policy = "Organizer")]      // privileged endpoints
public async Task<IActionResult> CreateWebinar(...) { ... }
```

If authentication is **not** needed (e.g. you're behind an API gateway that handles auth) — at minimum delete the misleading line:
```diff
- app.UseAuthorization();
```

**Refactoring steps:**
1. Decide: gateway-handled auth, or app-level JWT.
2. Add `Microsoft.AspNetCore.Authentication.JwtBearer` 6.0.x NuGet to `Web.csproj`.
3. Implement the extension method above.
4. Add `Jwt` section to configuration (issuer, audience, signing key — from secrets, not committed).
5. Add `ICurrentUserService` in Application abstractions, implement in Infrastructure reading `IHttpContextAccessor`.
6. Decorate controllers; default to `[Authorize]` and use `[AllowAnonymous]` for explicit public endpoints.

---

### 3. 🚨 Anemic domain model

**Location:** `Domain/Entities/Webinar.cs:1-22`

**Issue:** The only aggregate is a data container.

```csharp
// Current code — Domain/Entities/Webinar.cs
public sealed class Webinar : Entity
{
    public Webinar(Guid id, string name, DateTime scheduledOn) : base(id)
    {
        Name = name;
        ScheduledOn = scheduledOn;
    }

    private Webinar() { }

    public string Name { get; private set; }
    public DateTime ScheduledOn { get; private set; }
}
```

There is no factory, no behavior, no validation, no domain events, no value objects, no aggregate-root marker, no audit/version fields. The constructor accepts a `null` name and a past `ScheduledOn` without complaint. All business logic — when it exists — will live in handlers.

**Impact:**
- Business rules end up scattered across handlers and validators. The domain doesn't own its invariants.
- Refactoring a rule (e.g. "webinars must be scheduled at least 24h in advance") requires touching multiple files in Application instead of one in Domain.
- Testing requires spinning up MediatR, validators, and handlers when a pure domain unit test would suffice.
- Domain events become impossible because there's no entity behavior to raise them from.

**Recommended fix — enrich the domain with factory + behavior + events:**

```csharp
// Domain/Entities/Webinar.cs (refactored)
using Domain.Events;
using Domain.Exceptions;
using Domain.Primitives;

namespace Domain.Entities;

public sealed class Webinar : AggregateRoot
{
    private Webinar(Guid id, string name, DateTime scheduledOn) : base(id)
    {
        Name = name;
        ScheduledOn = scheduledOn;
        Status = WebinarStatus.Scheduled;
    }

    private Webinar() { } // EF

    public string Name { get; private set; } = default!;
    public DateTime ScheduledOn { get; private set; }
    public WebinarStatus Status { get; private set; }
    public string? CancellationReason { get; private set; }

    public static Webinar Create(string name, DateTime scheduledOn, IDateTimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new WebinarNameRequiredException();
        if (name.Length > 100)
            throw new WebinarNameTooLongException(name.Length);
        if (scheduledOn <= clock.UtcNow)
            throw new WebinarScheduledInPastException(scheduledOn);

        var webinar = new Webinar(Guid.NewGuid(), name.Trim(), scheduledOn);
        webinar.RaiseDomainEvent(new WebinarCreatedDomainEvent(webinar.Id, webinar.ScheduledOn));
        return webinar;
    }

    public void Reschedule(DateTime newScheduledOn, IDateTimeProvider clock)
    {
        if (Status != WebinarStatus.Scheduled)
            throw new WebinarNotReschedulableException(Id, Status);
        if (newScheduledOn <= clock.UtcNow)
            throw new WebinarScheduledInPastException(newScheduledOn);

        var previous = ScheduledOn;
        ScheduledOn = newScheduledOn;
        RaiseDomainEvent(new WebinarRescheduledDomainEvent(Id, previous, newScheduledOn));
    }

    public void Cancel(string reason)
    {
        if (Status == WebinarStatus.Cancelled)
            return; // idempotent
        if (string.IsNullOrWhiteSpace(reason))
            throw new WebinarCancellationReasonRequiredException();

        Status = WebinarStatus.Cancelled;
        CancellationReason = reason.Trim();
        RaiseDomainEvent(new WebinarCancelledDomainEvent(Id, reason));
    }
}

public enum WebinarStatus { Scheduled, Cancelled, Completed }
```

Supporting primitives:

```csharp
// Domain/Primitives/AggregateRoot.cs
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = new();

    protected AggregateRoot(Guid id) : base(id) { }
    protected AggregateRoot() { }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();
    public void ClearDomainEvents() => _domainEvents.Clear();
    protected void RaiseDomainEvent(IDomainEvent @event) => _domainEvents.Add(@event);
}

// Domain/Primitives/IDomainEvent.cs
public interface IDomainEvent : MediatR.INotification { }

// Domain/Events/WebinarCreatedDomainEvent.cs
public sealed record WebinarCreatedDomainEvent(Guid WebinarId, DateTime ScheduledOn) : IDomainEvent;
```

The handler becomes a coordinator instead of a place where rules live:
```csharp
// Application/Webinars/Commands/CreateWebinar/CreateWebinarCommandHandler.cs (refactored)
internal sealed class CreateWebinarCommandHandler : ICommandHandler<CreateWebinarCommand, Guid>
{
    private readonly IWebinarRepository _webinarRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDateTimeProvider _clock;

    public CreateWebinarCommandHandler(
        IWebinarRepository webinarRepository,
        IUnitOfWork unitOfWork,
        IDateTimeProvider clock)
    {
        _webinarRepository = webinarRepository;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<Guid> Handle(CreateWebinarCommand request, CancellationToken ct)
    {
        var webinar = Webinar.Create(request.Name, request.ScheduledOn, _clock);
        _webinarRepository.Insert(webinar);
        await _unitOfWork.SaveChangesAsync(ct);
        return webinar.Id;
    }
}
```

Dispatch domain events after `SaveChanges`:
```csharp
// Infrastructure/ApplicationDbContext.cs (refactored)
public sealed class ApplicationDbContext : DbContext, IUnitOfWork
{
    private readonly IPublisher _publisher;
    public ApplicationDbContext(DbContextOptions options, IPublisher publisher) : base(options)
        => _publisher = publisher;

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var events = ChangeTracker.Entries<AggregateRoot>()
            .Select(e => e.Entity)
            .SelectMany(a => { var evts = a.DomainEvents.ToList(); a.ClearDomainEvents(); return evts; })
            .ToList();

        var result = await base.SaveChangesAsync(cancellationToken);
        foreach (var e in events)
            await _publisher.Publish(e, cancellationToken);
        return result;
    }
}
```

**Refactoring steps:**
1. Add `AggregateRoot`, `IDomainEvent` to `Domain/Primitives/`.
2. Convert `Webinar` to an aggregate root with factory `Create` and behavior methods.
3. Add new domain exceptions for each invariant.
4. Add `IDateTimeProvider` contract to `Application/Abstractions/` and system implementation to `Infrastructure/Services/`.
5. Update the command handler to use `Webinar.Create(...)`.
6. Override `SaveChangesAsync` in `ApplicationDbContext` to publish domain events.
7. Add MediatR `INotificationHandler<TEvent>` implementations as needed.

---

### 4. 🚨 Database migrations executed on application startup

**Location:** `Web/Program.cs:17, 28-32`

**Issue:**

```csharp
// Web/Program.cs (current)
public static async Task Main(string[] args)
{
    var webHost = CreateHostBuilder(args).Build();
    await ApplyMigrations(webHost.Services);     // ⚠ runs migrations at every startup
    await webHost.RunAsync();
}

private static async Task ApplyMigrations(IServiceProvider serviceProvider)
{
    using var scope = serviceProvider.CreateScope();
    await using var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.MigrateAsync();
}
```

**Impact in production:**
- **Multiple instances race.** In a horizontally scaled deployment (Kubernetes replica > 1), every pod will call `MigrateAsync` simultaneously. EF Core has *some* locking, but you'll see deadlocks, partial migrations, and 5xx storms during rollouts.
- **No rollback strategy.** If a migration fails halfway, the app is dead but the DB is in a half-migrated state. Recovery requires manual SQL.
- **Long-running migrations block startup.** A 30-minute column backfill in a migration means 30 minutes of "503 Service Unavailable" while pods refuse to become ready.
- **Permissions sprawl.** The runtime DB user now needs DDL rights (CREATE/ALTER/DROP). Principle of least privilege says it shouldn't.

**Recommended fix — extract migrations into a deployment-pipeline step:**

```csharp
// Web/Program.cs (refactored)
public static async Task Main(string[] args)
{
    var host = CreateHostBuilder(args).Build();
    await host.RunAsync();
}
```

Run migrations during deployment using an EF Core migration bundle (self-contained executable):

```powershell
dotnet ef migrations bundle `
    --project Infrastructure `
    --startup-project Web `
    --self-contained `
    --runtime linux-x64 `
    --output ./efbundle
```

…then in your CI/CD pipeline:
```bash
# Pre-deploy step (runs once, before pods come up)
./efbundle --connection "$DATABASE_CONNECTION_STRING"

# Then deploy the application
kubectl apply -f k8s/deployment.yaml
```

For local development convenience, keep the option behind a flag:
```csharp
public static async Task Main(string[] args)
{
    var host = CreateHostBuilder(args).Build();

    if (args.Contains("--migrate") ||
        Environment.GetEnvironmentVariable("APPLY_MIGRATIONS_ON_STARTUP") == "true")
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
    }

    await host.RunAsync();
}
```

**Refactoring steps:**
1. Remove the unconditional `ApplyMigrations` call.
2. Add the env-flag fallback for local development.
3. Add a CI/CD step that runs `efbundle` (or `dotnet ef database update`) against the target database before deploying the app.
4. In Kubernetes deployments, run migrations as an init container or a one-off Job, not as part of the application pod.
5. Restrict the runtime DB user to DML only (SELECT/INSERT/UPDATE/DELETE); have a separate migration user with DDL rights, used only at deploy time.

---

## High findings

### 5. ⚠️ Infrastructure leakage: `IDbConnection` and raw SQL in Application layer

**Location:** `Application/Webinars/Queries/GetWebinarById/GetWebinarQueryHandler.cs:1-33`

**Issue:**

```csharp
// Current — Application layer
using System.Data;
using Dapper;

internal sealed class GetWebinarQueryHandler : IQueryHandler<GetWebinarByIdQuery, WebinarResponse>
{
    private readonly IDbConnection _dbConnection;
    public GetWebinarQueryHandler(IDbConnection dbConnection) => _dbConnection = dbConnection;

    public async Task<WebinarResponse> Handle(GetWebinarByIdQuery request, CancellationToken cancellationToken)
    {
        const string sql = @"SELECT * FROM ""Webinars"" WHERE ""Id"" = @WebinarId";
        var webinar = await _dbConnection.QueryFirstOrDefaultAsync<WebinarResponse>(
            sql, new { request.WebinarId });
        if (webinar is null) throw new WebinarNotFoundException(request.WebinarId);
        return webinar;
    }
}
```

`IDbConnection` is from `System.Data`. The SQL string knows the physical schema (`"Webinars"` table, PostgreSQL identifier quoting). Application code now depends on infrastructure details. If the storage layer is ever swapped or the table is renamed, this handler breaks silently at runtime.

**Impact:**
- The Application layer is no longer storage-agnostic, contrary to Clean Architecture's central promise.
- Read-side schema changes can't be detected at compile time.
- Testing the handler requires either a real database or a complex `IDbConnection` mock.

**Recommended fix — abstract Dapper behind an Application-defined contract:**

```csharp
// Application/Abstractions/Data/ISqlConnectionFactory.cs (new)
public interface ISqlConnectionFactory
{
    IDbConnection CreateOpenConnection();
}
```

```csharp
// Infrastructure/Data/NpgsqlConnectionFactory.cs (new)
internal sealed class NpgsqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;
    public NpgsqlConnectionFactory(IConfiguration configuration)
        => _connectionString = configuration.GetConnectionString("Application")!;

    public IDbConnection CreateOpenConnection()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
```

**Better still — introduce a read-model repository per aggregate**, keeping SQL out of handlers entirely:

```csharp
// Application/Abstractions/Data/IWebinarReadRepository.cs (new)
public interface IWebinarReadRepository
{
    Task<WebinarResponse?> GetByIdAsync(Guid webinarId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<WebinarSummaryResponse>> ListUpcomingAsync(
        int pageNumber, int pageSize, CancellationToken cancellationToken);
}
```

```csharp
// Infrastructure/Repositories/WebinarReadRepository.cs (new)
internal sealed class WebinarReadRepository : IWebinarReadRepository
{
    private readonly ISqlConnectionFactory _factory;
    public WebinarReadRepository(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<WebinarResponse?> GetByIdAsync(Guid webinarId, CancellationToken ct)
    {
        const string sql = """
            SELECT "Id", "Name", "ScheduledOn"
            FROM "Webinars"
            WHERE "Id" = @WebinarId;
            """;
        using var conn = _factory.CreateOpenConnection();
        return await conn.QueryFirstOrDefaultAsync<WebinarResponse>(
            new CommandDefinition(sql, new { webinarId }, cancellationToken: ct));
    }
    // ListUpcomingAsync omitted for brevity
}
```

```csharp
// Application/Webinars/Queries/GetWebinarById/GetWebinarQueryHandler.cs (refactored)
internal sealed class GetWebinarQueryHandler : IQueryHandler<GetWebinarByIdQuery, WebinarResponse>
{
    private readonly IWebinarReadRepository _reads;
    public GetWebinarQueryHandler(IWebinarReadRepository reads) => _reads = reads;

    public async Task<WebinarResponse> Handle(GetWebinarByIdQuery request, CancellationToken ct)
        => await _reads.GetByIdAsync(request.WebinarId, ct)
           ?? throw new WebinarNotFoundException(request.WebinarId);
}
```

**Refactoring steps:**
1. Remove the `Dapper` package from `Application.csproj` (keep it only in `Infrastructure.csproj`).
2. Add `ISqlConnectionFactory` to Application abstractions; remove the `IDbConnection` factory line from `Startup.cs:62-63`.
3. Add `IWebinarReadRepository` (one per aggregate that needs queries).
4. Move the SQL to the implementation in Infrastructure.
5. Refactor query handlers to depend on read repositories only.

---

### 6. ⚠️ Repository is write-only and synchronous

**Location:** `Domain/Abstractions/IWebinarRepository.cs:1-8`, `Infrastructure/Repositories/WebinarRepository.cs:1-13`

**Issue:**

```csharp
// Domain
public interface IWebinarRepository
{
    void Insert(Webinar webinar);
}

// Infrastructure
public sealed class WebinarRepository : IWebinarRepository
{
    private readonly ApplicationDbContext _dbContext;
    public WebinarRepository(ApplicationDbContext dbContext) => _dbContext = dbContext;
    public void Insert(Webinar webinar) => _dbContext.Set<Webinar>().Add(webinar);
}
```

Reads bypass the repository entirely (Finding #5). Commands that need to load an aggregate (`Cancel`, `Reschedule`, etc.) have nowhere to go. `Insert` is synchronous but every consumer is async.

**Impact:**
- Cannot implement any command that operates on an existing aggregate without changing this interface.
- The async/sync inconsistency in handler code is misleading.

**Recommended fix — complete the contract and align signatures:**

```csharp
// Domain/Abstractions/IWebinarRepository.cs (refactored)
public interface IWebinarRepository
{
    void Insert(Webinar webinar);
    void Remove(Webinar webinar);
    Task<Webinar?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
}
```

```csharp
// Infrastructure/Repositories/WebinarRepository.cs (refactored)
public sealed class WebinarRepository : IWebinarRepository
{
    private readonly ApplicationDbContext _dbContext;
    public WebinarRepository(ApplicationDbContext dbContext) => _dbContext = dbContext;

    public void Insert(Webinar webinar) => _dbContext.Set<Webinar>().Add(webinar);

    public void Remove(Webinar webinar) => _dbContext.Set<Webinar>().Remove(webinar);

    public Task<Webinar?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _dbContext.Set<Webinar>().FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
}
```

`Insert`/`Remove` stay synchronous deliberately — `Add`/`Remove` only manipulate the change tracker; the database round-trip happens at `SaveChangesAsync` time. This is the canonical EF Core pattern.

**Refactoring steps:**
1. Extend `IWebinarRepository` with the methods commands actually need.
2. Implement them in `WebinarRepository`.
3. Resist the temptation to expose `IQueryable<Webinar>` — that breaks the abstraction and pushes EF semantics into handlers.

---

### 7. ⚠️ Validation rules don't match persistence contract

**Location:** `Application/Webinars/Commands/CreateWebinar/CreateWebinarCommandValidator.cs:1-13`

**Issue:**

```csharp
// Current
public sealed class CreateWebinarCommandValidator : AbstractValidator<CreateWebinarCommand>
{
    public CreateWebinarCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.ScheduledOn).NotEmpty();
    }
}
```

`Name` has no max-length although `WebinarConfiguration.cs:15` declares `HasMaxLength(100)`. `ScheduledOn` has no future-date check although a webinar scheduled in the past is semantically nonsense. A 200-character name will pass validation and then fail at the database with a misleading error.

**Impact:** Invalid data reaches the data layer, producing 500-level errors and DB constraint violations instead of clean 400 responses with a clear `errors` payload.

**Recommended fix:**

```csharp
public sealed class CreateWebinarCommandValidator : AbstractValidator<CreateWebinarCommand>
{
    public CreateWebinarCommandValidator(IDateTimeProvider clock)
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Webinar name is required.")
            .MaximumLength(100).WithMessage("Webinar name cannot exceed 100 characters.");

        RuleFor(x => x.ScheduledOn)
            .NotEmpty().WithMessage("Webinar schedule date is required.")
            .Must((cmd, when) => when > clock.UtcNow)
                .WithMessage("Webinar must be scheduled in the future.");
    }
}
```

If domain enrichment from Finding #3 is also applied, the domain itself enforces these invariants, but the validator still provides cheap fast-path rejection without hitting the handler.

**Refactoring steps:**
1. Audit every validator for max-length / range / format rules that mirror EF configuration.
2. Make `IDateTimeProvider` injectable (validators are resolved per-request; this is safe).
3. Consider extracting magic numbers (`100`) to a constant on the entity (`Webinar.NameMaxLength`) to keep DB and validator in sync.

---

### 8. ⚠️ `ExceptionHandlingMiddleware` leaks `exception.Message` to clients

**Location:** `Web/Middleware/ExceptionHandlingMiddleware.cs:55-60`

**Issue:**

```csharp
// Current — Web/Middleware/ExceptionHandlingMiddleware.cs
var response = new
{
    status = httpContext.Response.StatusCode,
    message = exception.Message,    // ⚠ raw exception message goes on the wire
    errors
};
await httpContext.Response.WriteAsync(JsonSerializer.Serialize(response));
```

A `NullReferenceException` thrown deep in EF Core will leak its `Message` (often containing column names, type names, or even partial SQL) to anyone hitting the API. The shape is also bespoke — not RFC 7807 `ProblemDetails`, which is the standard ASP.NET Core clients expect.

**Impact:** Information disclosure (severity depends on what your handlers throw), and a non-standard error contract that clients have to learn separately.

**Recommended fix — return `ProblemDetails`, hide internals in production:**

```csharp
// Web/Middleware/ExceptionHandlingMiddleware.cs (refactored)
internal sealed class ExceptionHandlingMiddleware : IMiddleware
{
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ExceptionHandlingMiddleware(
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try { await next(context); }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unhandled exception. TraceId={TraceId} Path={Path}",
                context.TraceIdentifier, context.Request.Path);
            await HandleAsync(context, ex);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception exception)
    {
        var (status, title) = exception switch
        {
            ValidationException     => (StatusCodes.Status400BadRequest, "Validation failed"),
            BadRequestException     => (StatusCodes.Status400BadRequest, "Bad request"),
            NotFoundException       => (StatusCodes.Status404NotFound,   "Resource not found"),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Unauthorized"),
            _                       => (StatusCodes.Status500InternalServerError, "Server error")
        };

        var problem = new ProblemDetails
        {
            Status   = status,
            Title    = title,
            Detail   = exception is BadRequestException or NotFoundException
                          ? exception.Message
                          : (_env.IsDevelopment() ? exception.Message : null),
            Instance = context.Request.Path,
            Type     = $"https://httpstatuses.io/{status}"
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;

        if (exception is ValidationException ve)
        {
            problem.Extensions["errors"] = ve.Errors;
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem));
    }
}
```

**Refactoring steps:**
1. Switch the response shape to `ProblemDetails` (`Microsoft.AspNetCore.Mvc.ProblemDetails`).
2. Inject `IHostEnvironment` so production hides internal exception messages while dev keeps them.
3. Always include `traceId` in the response so client errors can be cross-referenced with server logs.
4. Use a structured log template (`"... TraceId={TraceId} Path={Path}"`), not string interpolation.

---

### 9. ⚠️ Only a single MediatR pipeline behavior

**Location:** `Application/Behaviors/` (`ValidationBehavior.cs` is the only file), `Web/Startup.cs:38`

**Issue:** The pipeline runs validation and nothing else. There is no logging, no transaction wrapping, no performance timing, no retry, no caching. This works for a one-entity demo but does not scale.

**Recommended fix — add the standard set of behaviors:**

**LoggingBehavior** — every command/query gets a structured log record:
```csharp
// Application/Behaviors/LoggingBehavior.cs (new)
internal sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;
    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger) => _logger = logger;

    public async Task<TResponse> Handle(
        TRequest request, CancellationToken cancellationToken, RequestHandlerDelegate<TResponse> next)
    {
        var name = typeof(TRequest).Name;
        _logger.LogInformation("Handling {RequestName}", name);
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await next();
            sw.Stop();
            _logger.LogInformation("Handled {RequestName} in {Elapsed}ms", name, sw.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Failed {RequestName} after {Elapsed}ms", name, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
```

**PerformanceBehavior** — warn on slow handlers:
```csharp
internal sealed class PerformanceBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<PerformanceBehavior<TRequest, TResponse>> _logger;
    private const int WarnThresholdMs = 500;

    public PerformanceBehavior(ILogger<PerformanceBehavior<TRequest, TResponse>> logger) => _logger = logger;

    public async Task<TResponse> Handle(TRequest request, CancellationToken ct, RequestHandlerDelegate<TResponse> next)
    {
        var sw = Stopwatch.StartNew();
        var response = await next();
        sw.Stop();
        if (sw.ElapsedMilliseconds > WarnThresholdMs)
            _logger.LogWarning("Slow request: {RequestName} took {Elapsed}ms", typeof(TRequest).Name, sw.ElapsedMilliseconds);
        return response;
    }
}
```

**UnitOfWorkBehavior** (controversial — debate below):
```csharp
internal sealed class UnitOfWorkBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : class, ICommand<TResponse>   // Commands only
{
    private readonly IUnitOfWork _unitOfWork;
    public UnitOfWorkBehavior(IUnitOfWork unitOfWork) => _unitOfWork = unitOfWork;

    public async Task<TResponse> Handle(TRequest request, CancellationToken ct, RequestHandlerDelegate<TResponse> next)
    {
        var response = await next();
        await _unitOfWork.SaveChangesAsync(ct);
        return response;
    }
}
```

*Debate:* a UnitOfWork behavior removes the `SaveChangesAsync` call from every handler, which is DRY. But it hides when the commit happens, makes domain-event-after-commit semantics implicit, and means every handler — including read-only ones — pays for an EF round-trip. The codebase's current explicit pattern (`await _unitOfWork.SaveChangesAsync(ct)` in each handler) is arguably clearer. Choose deliberately; don't add this behavior just because tutorials suggest it.

Register all behaviors:
```csharp
services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
services.AddTransient(typeof(IPipelineBehavior<,>), typeof(PerformanceBehavior<,>));
services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
// services.AddTransient(typeof(IPipelineBehavior<,>), typeof(UnitOfWorkBehavior<,>));   // if you decide to use it
```

**Order matters** — MediatR runs behaviors in registration order. Logging on the outside (sees timing of everything), validation on the inside (closest to the handler).

---

## Medium findings

### 10. 🟡 Namespace mismatch in EF migrations

**Location:** `Infrastructure/Migrations/20210728191856_InitialCreate.cs:4`

**Issue:** The migration file declares `namespace Persistence.Migrations` even though the project is named `Infrastructure`. Same in the Designer file and the model snapshot. This is a leftover from an old project rename.

**Impact:** Confusing for new contributors. `dotnet ef` commands still work, but `using` statements and any type lookups (e.g. reflection-based migration introspection) need the wrong namespace.

**Recommended fix:** Either bulk-rename the namespace in all migration files (low risk; just a `find/replace` from `Persistence.Migrations` to `Infrastructure.Migrations`) or accept the legacy state and add a comment. Then verify with `dotnet ef migrations list --project Infrastructure --startup-project Web` that EF still recognizes them.

If you go the rename route, add an empty no-op migration immediately after to ensure the model snapshot regenerates cleanly.

---

### 11. 🟡 DI registration concentrated in `Startup.cs`

**Location:** `Web/Startup.cs:27-66`

**Issue:** Every DI registration lives in a single `ConfigureServices` method. With a single aggregate this is fine; with ten it becomes a junk drawer.

**Recommended fix — extract per-layer extension methods:**

```csharp
// Application/DependencyInjection.cs (new)
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(AssemblyReference).Assembly;
        services.AddMediatR(assembly);
        services.AddValidatorsFromAssembly(assembly);

        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(PerformanceBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        return services;
    }
}
```

```csharp
// Infrastructure/DependencyInjection.cs (new)
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ApplicationDbContext>(o =>
            o.UseNpgsql(configuration.GetConnectionString("Application")));

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<IWebinarRepository, WebinarRepository>();
        services.AddScoped<IWebinarReadRepository, WebinarReadRepository>();
        services.AddSingleton<ISqlConnectionFactory, NpgsqlConnectionFactory>();
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();

        return services;
    }
}
```

```csharp
// Presentation/DependencyInjection.cs (new)
public static class DependencyInjection
{
    public static IMvcBuilder AddPresentation(this IServiceCollection services) =>
        services.AddControllers().AddApplicationPart(typeof(AssemblyReference).Assembly);
}
```

`Startup.cs` shrinks to:
```csharp
services.AddPresentation();
services.AddApplication();
services.AddInfrastructure(Configuration);
services.AddSwaggerGen(...);
services.AddTransient<ExceptionHandlingMiddleware>();
```

---

### 12. 🟡 Duplicate `CreateWebinarRequest` vs `CreateWebinarCommand`

**Location:** `Application/Webinars/Commands/CreateWebinar/CreateWebinarRequest.cs`, `CreateWebinarCommand.cs`

**Issue:** Both records have the identical shape `(string Name, DateTime ScheduledOn)`. The controller adapts one to the other via Mapster:

```csharp
var command = request.Adapt<CreateWebinarCommand>();
```

The Request type exists only to be converted into the Command. Maintenance cost without clear benefit.

**Recommended fix — pick one:**

**Option A (preferred when request shape == command shape):** Bind the Command directly.
```csharp
[HttpPost]
public async Task<IActionResult> CreateWebinar(
    [FromBody] CreateWebinarCommand command,
    CancellationToken cancellationToken)
{
    var id = await Sender.Send(command, cancellationToken);
    return CreatedAtAction(nameof(GetWebinar), new { webinarId = id }, id);
}
```

**Option B (keep them separate when the wire shape diverges from the command — e.g. you need different field names or nested DTOs):** Replace convention-based `Adapt<T>()` with explicit configuration so renames break at compile time, not runtime — see File 02 §11.

---

### 13. 🟡 `SELECT *` in Dapper read query

**Location:** `Application/Webinars/Queries/GetWebinarById/GetWebinarQueryHandler.cs:11`

**Issue:**
```csharp
const string sql = @"SELECT * FROM ""Webinars"" WHERE ""Id"" = @WebinarId";
```

A future column on `Webinars` will silently flow into the materialized `WebinarResponse`, or worse, cause a mapping failure. Star-select couples the query to the table's physical shape.

**Recommended fix:**
```csharp
const string sql = """
    SELECT "Id", "Name", "ScheduledOn"
    FROM "Webinars"
    WHERE "Id" = @WebinarId;
    """;
```

Apply the same to every read query you add.

---

### 14. 🟡 `AllowedHosts: "*"`

**Location:** `Web/appsettings.json:9`

**Issue:** `AllowedHosts: "*"` accepts any `Host` header. Combined with no authentication, this widens the attack surface for Host-header-poisoning style attacks.

**Recommended fix:** Set per environment.

```jsonc
// appsettings.Production.json (new)
{ "AllowedHosts": "api.example.com;www.example.com" }
```

Keep `"*"` for `Development.json` if convenient.

---

### 15. 🟡 Mapster dependency declared in `Application` but unused

**Location:** `Application/Application.csproj:10`

**Issue:**
```xml
<PackageReference Include="Mapster" Version="7.3.0" />
```

Mapster is only used in `WebinarsController` (Presentation). The Application project's package reference is dead weight that inflates the dependency graph and confuses readers about layer responsibilities.

**Recommended fix — remove it from Application:**
```xml
<!-- Application.csproj after the fix -->
<ItemGroup>
    <PackageReference Include="Dapper" Version="2.0.123" />    <!-- remove after Finding #5 fix -->
    <PackageReference Include="FluentValidation" Version="11.1.1" />
    <PackageReference Include="MediatR" Version="10.0.1" />
</ItemGroup>
```

If you adopt explicit Mapster configurations in Application (File 02 §11), re-add it then with intent.

---

### 16. 🟡 Legacy `Startup.cs` pattern on .NET 6

**Location:** `Web/Program.cs:22-25`

**Issue:** .NET 6 introduced minimal hosting, but the project still uses the .NET 5 `Host.CreateDefaultBuilder().ConfigureWebHostDefaults(b => b.UseStartup<Startup>())` style. Both are valid and supported, but minimal hosting is the modern idiom and unifies the two startup files into one.

**Recommended fix — consolidate into `Program.cs`:**

```csharp
// Web/Program.cs (refactored)
using Application;
using Infrastructure;
using Presentation;
using Web.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddPresentation()
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

builder.Services.AddTransient<ExceptionHandlingMiddleware>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(/* ... */);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseAuthentication();    // (after Finding #2 fix)
app.UseAuthorization();
app.MapControllers();

await app.RunAsync();
```

`Startup.cs` is deleted. `Program.cs` becomes the single composition root.

---

### 17. 🟡 `ValidationBehavior` runs validators synchronously

**Location:** `Application/Behaviors/ValidationBehavior.cs:26`

**Issue:**
```csharp
var errorsDictionary = _validators
    .Select(x => x.Validate(context))      // synchronous validation
    .SelectMany(x => x.Errors)
    ...
```

`Validate(context)` blocks; async rules (e.g. "name must be unique" requiring a DB hit) won't work, and any I/O happening inside a validator will block the calling thread.

**Recommended fix:**
```csharp
public async Task<TResponse> Handle(
    TRequest request, CancellationToken cancellationToken, RequestHandlerDelegate<TResponse> next)
{
    if (!_validators.Any()) return await next();

    var context = new ValidationContext<TRequest>(request);
    var results = await Task.WhenAll(
        _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

    var errors = results
        .SelectMany(r => r.Errors)
        .Where(e => e is not null)
        .GroupBy(e => e.PropertyName, e => e.ErrorMessage)
        .ToDictionary(g => g.Key, g => g.Distinct().ToArray());

    if (errors.Any()) throw new ValidationException(errors);
    return await next();
}
```

---

### 18. 🟡 `ValidationBehavior` only applies to Commands (not Queries)

**Location:** `Application/Behaviors/ValidationBehavior.cs:13`

**Issue:**
```csharp
where TRequest : class, ICommand<TResponse>
```

Queries are excluded from validation entirely. Defensible — most queries take simple types like `Guid` and validation would be cosmetic. But once queries take pagination and filtering inputs (`PageSize`, `From`/`To` dates, sort fields), validation gaps become real.

**Recommended fix:** Switch the constraint to a marker that both commands and queries can implement, or drop the constraint entirely and let absence of validators serve as the gate (which the current "if not _validators.Any() return await next()" already handles).

```csharp
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull   // accept any request type
{
    // ... rest unchanged
}
```

Validators are scanned from the Application assembly already; only requests that actually have a registered validator pay the cost.

---

## Low findings

### 19. 🔵 No correlation-id middleware

Add a small middleware that reads (or generates) a `X-Correlation-Id` header and pushes it into `Activity.Current` + the logging scope:
```csharp
public sealed class CorrelationIdMiddleware : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault()
                          ?? Guid.NewGuid().ToString();
        context.Response.Headers["X-Correlation-Id"] = correlationId;
        using var _ = Activity.Current?.SetTag("correlation_id", correlationId);
        await next(context);
    }
}
```

### 20. 🔵 No structured logging

Add Serilog with `UseSerilogRequestLogging()` and JSON-formatted output. See File 04 for the full recommendation.

### 21. 🔵 No health checks

```csharp
services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Application")!,
               name: "postgres", tags: new[] { "ready" });
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });
```

### 22. 🔵 No CORS configuration

If any browser client (SPA, mobile WebView) is in scope:
```csharp
services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(configuration.GetSection("Cors:Origins").Get<string[]>()!)
    .AllowAnyMethod()
    .AllowAnyHeader()));
// in the pipeline: app.UseRouting(); app.UseCors(); app.UseAuthentication(); ...
```

### 23. 🔵 Docker image runs as root with no `HEALTHCHECK`

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443
RUN groupadd -r app && useradd -r -g app app
USER app

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -fsS http://localhost/health/live || exit 1
```

(curl needs to be installed in the base image, or use `wget`, or use a `.NET` HC executable.)

### 24. 🔵 Floating `postgres:13.2` image tag

Pin a digest in production: `image: postgres:13.2@sha256:<digest>`. Also: PostgreSQL 13 is past its prime — plan a 15 or 16 upgrade.

### 25. 🔵 Typo in `launchSettings.json` profile (`OnionArchitecutre.Web`)

Rename to `CleanArchitecture.Web` for hygiene; nothing depends on this string in runtime code.

### 26. 🔵 No nullable reference type enforcement

Add `<Nullable>enable</Nullable>` to every `.csproj`. This will surface a number of latent `null` issues — fix them as you find them, rather than turning the feature off again.

### 27. 🔵 No global usings / file-scoped namespaces inconsistency

Some files use file-scoped namespaces (`namespace Foo;`), others use block-scoped. Pick one and convert. Optionally add a `GlobalUsings.cs` per project for common imports (`MediatR`, `FluentValidation`, etc.).

### 28. 🔵 No automated tests

No `tests/` folder, no `*.Tests.csproj`. For a sample project this is forgivable; for an enterprise foundation it is not. At minimum add:
- `Domain.UnitTests` for aggregate behavior (once the domain is enriched).
- `Application.UnitTests` for handlers, validators, and pipeline behaviors using `WebApplicationFactory` and an in-memory `TestServer`.
- `Infrastructure.IntegrationTests` using `Testcontainers.PostgreSql` for the real DB schema and migrations.

---

## Executive summary

This codebase is a **competent sample** for a one-aggregate teaching demo. The skeleton is right: Clean Architecture layering is enforced by `.csproj` references, CQRS with MediatR is in place, controllers are thin, the Domain layer is genuinely framework-free, exception handling is centralized.

It is **not yet** an enterprise foundation. The four critical findings above (secrets in repo, dead-code authorization, anemic domain, startup migrations) are non-negotiable for production. The five high findings will become very expensive to fix the larger the system grows on top of them — the infrastructure leak in the read path in particular gets worse with every query you add.

**Recommended refactor order:**

1. **Days 1-2 — secrets** (Finding #1) and **dead-code authorization** (#2). One PR each. These are pure-config changes with no business impact and they remove production-blocking risks immediately.
2. **Days 3-5 — DI extraction** (#11), **minimal hosting** (#16), **ProblemDetails error contract** (#8), **`AllowedHosts`** (#14), **dead Mapster reference** (#15), **`SELECT *`** (#13). All small, all independent, all reduce friction.
3. **Week 2 — observability foundation**: Serilog (#20), correlation IDs (#19), health checks (#21), CORS (#22) if applicable. Add structured logging *before* enriching the domain so you can see the refactor in action.
4. **Week 2-3 — domain enrichment** (#3). The biggest single change. Convert `Webinar` to an aggregate root, add `IDomainEvent`, add `IDateTimeProvider`, add factory + behavior. Update the command handler to call `Webinar.Create(...)`.
5. **Week 3 — read-side abstraction** (#5), **complete repository** (#6), **validator tightening** (#7). These three are easier to do together because they touch the same use-case folder.
6. **Week 3 — pipeline behaviors** (#9), **async validation** (#17), **migration-on-startup removal** (#4). The last item needs a CI/CD change so coordinate with whoever owns deployments.
7. **Ongoing — tests** (#28). Start with Domain unit tests as soon as the enrichment is done, then Application handler tests using `WebApplicationFactory`, then Infrastructure integration tests using Testcontainers.

After step 7, the architecture scores improve roughly:

| Dimension | Current | After refactor |
|-----------|---------|----------------|
| Architecture | 6 | 9 |
| Scalability | 4 | 7 (still needs caching + queue for full 9) |
| Maintainability | 5 | 9 |
| Production readiness | 3 | 8 (still needs observability hardening + auth integration for 10) |
