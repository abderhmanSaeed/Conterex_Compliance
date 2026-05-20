# 01 — Project Architecture Overview

> **Audit context** — Codebase: `D:\Projects\Clean Architecture\`. Target framework: `net6.0`. Single aggregate (`Webinar`). PostgreSQL via Npgsql/EF Core 6. MediatR 10, FluentValidation 11, Mapster 7, Dapper 2. Snapshot taken from the live source tree; every claim below is anchored to a file path and line number you can verify yourself.

---

## 1. Architecture style

The solution follows **Clean Architecture** (also called Onion / Hexagonal in some sources). Dependency flow is strictly inward: outer layers depend on inner layers, never the reverse. The reference graph below is taken **directly** from the `<ProjectReference>` declarations in every `.csproj`.

```
                +--------------------+
                |        Web         |   (composition root)
                |  net6.0 / aspnet   |
                +---------+----------+
                          |
       +------------------+------------------+----------------+
       |                  |                  |                |
       v                  v                  v                v
+-------------+   +---------------+   +------------------+   +----------+
| Presentation|-->|  Application  |-->|     Domain       |<--|Infrastructure|
+-------------+   +---------------+   +------------------+   +----------+
       (Web references all four; Presentation -> Application -> Domain;
        Infrastructure -> Domain. Domain depends on nothing.)
```

| Project | References | Lives in | Purpose |
|---------|------------|----------|---------|
| `Domain` | *(none)* | `Domain/Domain.csproj` | Enterprise entities, repository contracts, domain exceptions, primitives |
| `Application` | `Domain` | `Application/Application.csproj` | Use cases (CQRS), validators, pipeline behaviors, application contracts |
| `Infrastructure` | `Domain` | `Infrastructure/Infrastructure.csproj` | EF Core DbContext, entity configurations, repositories, migrations |
| `Presentation` | `Application` | `Presentation/Presentation.csproj` | MVC controllers (ApiController base + WebinarsController) |
| `Web` | `Domain`, `Application`, `Infrastructure`, `Presentation` | `Web/Web.csproj` | ASP.NET Core host, DI composition, middleware pipeline, Swagger, Docker |

**Verdict on dependency direction:** Correct. Domain has zero dependencies. Application only references Domain. Infrastructure only references Domain (does **not** depend on Application — a deliberate Clean Architecture move that prevents Application code from accidentally calling Infrastructure types and vice versa; composition happens in Web).

---

## 2. Solution structure walkthrough

Verified file tree (every file enumerated by exploration agents):

```
D:\Projects\Clean Architecture\
├── CleanArchitecture.sln
├── docker-compose.yml
├── docker-compose.override.yml
├── docker-compose.dcproj
├── Domain/
│   ├── Domain.csproj
│   ├── AssemblyReference.cs
│   ├── Primitives/Entity.cs
│   ├── Entities/Webinar.cs
│   ├── Abstractions/IUnitOfWork.cs
│   ├── Abstractions/IWebinarRepository.cs
│   └── Exceptions/
│       ├── WebinarNotFoundException.cs
│       └── Base/
│           ├── BadRequestException.cs
│           └── NotFoundException.cs
├── Application/
│   ├── Application.csproj
│   ├── AssemblyReference.cs
│   ├── Abstractions/Messaging/
│   │   ├── ICommand.cs
│   │   ├── ICommandHandler.cs
│   │   ├── IQuery.cs
│   │   └── IQueryHandler.cs
│   ├── Behaviors/ValidationBehavior.cs
│   ├── Exceptions/ValidationException.cs
│   └── Webinars/
│       ├── Commands/CreateWebinar/
│       │   ├── CreateWebinarCommand.cs
│       │   ├── CreateWebinarCommandHandler.cs
│       │   ├── CreateWebinarCommandValidator.cs
│       │   └── CreateWebinarRequest.cs
│       └── Queries/GetWebinarById/
│           ├── GetWebinarByIdQuery.cs
│           ├── GetWebinarQueryHandler.cs
│           └── WebinarResponse.cs
├── Infrastructure/
│   ├── Infrastructure.csproj
│   ├── AssemblyReference.cs
│   ├── ApplicationDbContext.cs
│   ├── Configurations/WebinarConfiguration.cs
│   ├── Repositories/WebinarRepository.cs
│   └── Migrations/
│       ├── 20210728191856_InitialCreate.cs
│       ├── 20210728191856_InitialCreate.Designer.cs
│       └── ApplicationDbContextModelSnapshot.cs
├── Presentation/
│   ├── Presentation.csproj
│   ├── AssemblyReference.cs
│   └── Controllers/
│       ├── ApiController.cs
│       └── WebinarsController.cs
└── Web/
    ├── Web.csproj
    ├── Program.cs
    ├── Startup.cs
    ├── appsettings.json
    ├── appsettings.Development.json
    ├── AssemblyReference.cs
    ├── Dockerfile
    ├── Properties/launchSettings.json
    └── Middleware/ExceptionHandlingMiddleware.cs
```

Every project carries an `AssemblyReference.cs` whose sole job is to expose `typeof(AssemblyReference).Assembly` for scanning (used by `AddMediatR(applicationAssembly)`, `AddValidatorsFromAssembly(applicationAssembly)`, `AddApplicationPart(presentationAssembly)`, and the EF `ApplyConfigurationsFromAssembly(...)` call). This is a clean, intent-revealing convention worth preserving.

---

## 3. Per-layer responsibilities (with file:line citations)

### 3.1 Domain layer

**Files:** 9 source files. **NuGet:** none. **Framework references:** none.

| Concern | File | Notes |
|---------|------|-------|
| Base entity | `Domain/Primitives/Entity.cs:1-14` | `abstract class Entity` with `protected set` Guid `Id` and a protected parameterless ctor for EF materialization. |
| Aggregate root | `Domain/Entities/Webinar.cs:1-22` | `sealed class Webinar : Entity` with `private set` properties `Name`, `ScheduledOn`. **Anemic** — no behavior, no factory, no validation in ctor. |
| Persistence abstraction | `Domain/Abstractions/IUnitOfWork.cs:1-9` | `Task<int> SaveChangesAsync(CancellationToken)`. |
| Repository contract | `Domain/Abstractions/IWebinarRepository.cs:1-8` | Single method `void Insert(Webinar)`. No reads, no async, no Update/Delete. |
| Exception hierarchy | `Domain/Exceptions/Base/NotFoundException.cs:1-11`, `BadRequestException.cs:1-11`, `Domain/Exceptions/WebinarNotFoundException.cs:1-11` | Two abstract bases + one concrete `WebinarNotFoundException : NotFoundException`. |

Strict isolation is preserved: no EF Core attributes, no `System.Text.Json` annotations, no `[Table]`/`[Key]`/`[Column]`, no framework using-clauses. Domain is genuinely framework-agnostic.

What's **missing** at the Domain layer (would matter for an enterprise build): value objects, domain events / `IDomainEvent`, an event dispatcher contract, specifications, factories, an `IAggregateRoot` marker, audit / timestamp base, soft-delete primitives, base class for `Enumeration` / smart enums.

### 3.2 Application layer

**Files:** 15 source files. **NuGet:** `MediatR 10.0.1`, `FluentValidation 11.1.1`, `Mapster 7.3.0`, `Dapper 2.0.123`.

| Concern | File | Notes |
|---------|------|-------|
| CQRS abstractions | `Application/Abstractions/Messaging/ICommand.cs:1-6`, `ICommandHandler.cs:1-7`, `IQuery.cs:1-6`, `IQueryHandler.cs:1-7` | Thin wrappers over MediatR `IRequest` / `IRequestHandler` to enforce intent at the type level. |
| Pipeline behavior | `Application/Behaviors/ValidationBehavior.cs:1-52` | The **only** behavior. Generic constraint `where TRequest : class, ICommand<TResponse>` — runs on Commands only, never Queries. |
| Application exception | `Application/Exceptions/ValidationException.cs:1-13` | Inherits `Domain.Exceptions.Base.BadRequestException`; carries `Dictionary<string, string[]> Errors`. |
| Command | `Application/Webinars/Commands/CreateWebinar/CreateWebinarCommand.cs:1-6` | `sealed record CreateWebinarCommand(string Name, DateTime ScheduledOn) : ICommand<Guid>`. |
| Command validator | `Application/Webinars/Commands/CreateWebinar/CreateWebinarCommandValidator.cs:1-13` | `RuleFor(x => x.Name).NotEmpty();` / `RuleFor(x => x.ScheduledOn).NotEmpty();` — no MaxLength, no future-date guard. |
| Command handler | `Application/Webinars/Commands/CreateWebinar/CreateWebinarCommandHandler.cs:1-31` | `internal sealed class` — instantiates entity, calls `_webinarRepository.Insert`, then `_unitOfWork.SaveChangesAsync`. |
| Request DTO | `Application/Webinars/Commands/CreateWebinar/CreateWebinarRequest.cs:1-5` | `sealed record CreateWebinarRequest(string Name, DateTime ScheduledOn)` — structurally identical to the Command. |
| Query | `Application/Webinars/Queries/GetWebinarById/GetWebinarByIdQuery.cs:1-6` | `sealed record GetWebinarByIdQuery(Guid WebinarId) : IQuery<WebinarResponse>`. |
| Query handler | `Application/Webinars/Queries/GetWebinarById/GetWebinarQueryHandler.cs:1-33` | **Bypasses the repository.** Takes `IDbConnection` directly, executes `SELECT * FROM ""Webinars""` via `_dbConnection.QueryFirstOrDefaultAsync<WebinarResponse>`. |
| Response DTO | `Application/Webinars/Queries/GetWebinarById/WebinarResponse.cs:1-5` | `sealed record WebinarResponse(Guid Id, string Name, DateTime ScheduledOn)`. |

Feature organization is the strongest pattern here: one folder per use case (`Webinars/Commands/CreateWebinar/`, `Webinars/Queries/GetWebinarById/`). This scales much better than the alternative "DTOs / Handlers / Validators" technical foldering because related files cluster together.

What's **missing**: Logging behavior, Transaction/UnitOfWork behavior, Performance/Timing behavior, optional Cache behavior, mapping configuration registry (Mapster `TypeAdapterConfig`), application-level services (`IDateTimeProvider`, `ICurrentUserService`, `IEmailSender`).

### 3.3 Infrastructure layer

**Files:** 8 source files. **NuGet:** `Npgsql.EntityFrameworkCore.PostgreSQL 6.0.6`.

| Concern | File | Notes |
|---------|------|-------|
| DbContext | `Infrastructure/ApplicationDbContext.cs:1-15` | `sealed class ApplicationDbContext : DbContext, IUnitOfWork`. **No `DbSet<>` declarations** — repositories use `_dbContext.Set<Webinar>()`. `OnModelCreating` applies configurations from the executing assembly. |
| Entity configuration | `Infrastructure/Configurations/WebinarConfiguration.cs:1-19` | `internal sealed class WebinarConfiguration : IEntityTypeConfiguration<Webinar>` — table `"Webinars"`, key on `Id`, `Name.HasMaxLength(100)`, `ScheduledOn.IsRequired()`. |
| Repository impl | `Infrastructure/Repositories/WebinarRepository.cs:1-13` | `sealed class WebinarRepository : IWebinarRepository`. The lone method `Insert` is **synchronous**, calls `_dbContext.Set<Webinar>().Add(webinar)`. |
| Initial migration | `Infrastructure/Migrations/20210728191856_InitialCreate.cs` | **Namespace is `Persistence.Migrations`** — does not match the `Infrastructure` namespace used by the rest of the project. Legacy leftover. |
| Model snapshot | `Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs` | Same namespace mismatch. |

What's **missing**: distributed cache, file storage, email/SMS service, HttpClient factory registrations, hosted/background services, Identity, JWT validation, Outbox publisher, seed data, AsNoTracking helpers, audit interceptors.

### 3.4 Presentation layer

**Files:** 3 source files + generated XML doc. **NuGet:** `Mapster 7.3.0`, `MediatR 10.0.1`, `Microsoft.AspNetCore.Mvc.Core 2.2.5` *(EOL — see File 06)*.

| Concern | File | Notes |
|---------|------|-------|
| Base controller | `Presentation/Controllers/ApiController.cs:1-20` | `[ApiController]` + `[Route("api/[controller]")]`. Exposes a `protected ISender Sender => _sender ??= HttpContext.RequestServices.GetService<ISender>();` — lazy MediatR sender via the request services. |
| Concrete controller | `Presentation/Controllers/WebinarsController.cs:1-54` | Two endpoints. `GET /api/webinars/{webinarId:guid}` → `GetWebinarByIdQuery`. `POST /api/webinars` → adapts `CreateWebinarRequest` to `CreateWebinarCommand` via Mapster `Adapt<T>` and sends through MediatR. Returns `Ok(...)` / `CreatedAtAction(...)`. |

Controllers are **thin** (the WebinarsController is 54 lines, almost entirely attributes and dispatch). This is the pattern to preserve as the codebase grows.

### 3.5 Web (composition root)

**Files:** `Program.cs`, `Startup.cs`, `Middleware/ExceptionHandlingMiddleware.cs`, settings, Dockerfile, launchSettings.

| Concern | File | Notes |
|---------|------|-------|
| Entry point | `Web/Program.cs:1-34` | Uses the **legacy** `Host.CreateDefaultBuilder().ConfigureWebHostDefaults(...).UseStartup<Startup>()` pattern even though the project targets `net6.0`. Calls `ApplyMigrations` before `RunAsync`. |
| DI / pipeline | `Web/Startup.cs:1-89` | All composition lives here. See section 6 below. |
| Exception middleware | `Web/Middleware/ExceptionHandlingMiddleware.cs:1-65` | `internal sealed class ... : IMiddleware`. Maps `BadRequestException` / `ValidationException` → 400, `NotFoundException` → 404, everything else → 500. Writes `{ status, message, errors[] }`. Leaks `exception.Message` directly to clients. |
| Base config | `Web/appsettings.json:1-10` | Logging defaults, `AllowedHosts: "*"`. |
| Dev config | `Web/appsettings.Development.json:1-12` | Plaintext PostgreSQL credentials. |
| Dockerfile | `Web/Dockerfile:1-22` | Multi-stage; exposes 80 & 443; final stage `mcr.microsoft.com/dotnet/aspnet:6.0`. No `HEALTHCHECK`, no non-root user. |

---

## 4. Request lifecycle (end to end)

Anchored to actual files; arrows show control flow:

```
1. HTTP request
   POST /api/webinars  (JSON body)
            │
            ▼
2. ASP.NET routing & model binding
   - UseHttpsRedirection (Startup.cs:81)
   - UseRouting          (Startup.cs:83)
   - UseAuthorization    (Startup.cs:85)  ⚠ no-op (no auth services registered)
            │
            ▼
3. ExceptionHandlingMiddleware  (Startup.cs:79 — registered AFTER swagger but
                                  BEFORE routing in Dev; in Prod it's the outermost handler)
            │
            ▼
4. Endpoint executes WebinarsController.CreateWebinar
   (Presentation/Controllers/WebinarsController.cs:38-53)
   - Mapster adapts CreateWebinarRequest → CreateWebinarCommand   (line 48)
   - Sender.Send(command, cancellationToken)                       (line 50)
            │
            ▼
5. MediatR pipeline
   - ValidationBehavior<TRequest,TResponse>
     (Application/Behaviors/ValidationBehavior.cs:1-52)
     - Resolves IEnumerable<IValidator<TRequest>>
     - Runs synchronous Validate(context)
     - Aggregates errors → throws ValidationException if any
            │
            ▼
6. Handler executes
   CreateWebinarCommandHandler
   (Application/Webinars/Commands/CreateWebinar/CreateWebinarCommandHandler.cs:11-29)
   - new Webinar(Guid.NewGuid(), ...)
   - _webinarRepository.Insert(webinar)         ── sync call
   - await _unitOfWork.SaveChangesAsync(...)    ── flushes EF change tracker
            │
            ▼
7. ApplicationDbContext (Infrastructure/ApplicationDbContext.cs:1-15)
   - SaveChangesAsync on the underlying DbContext
   - Implicit single-statement transaction via Npgsql
            │
            ▼
8. Return value bubbles back: Guid → Ok / CreatedAtAction → 201 response
```

Read path is structurally similar but the handler injects `IDbConnection` directly and executes raw SQL — see Section 5 below.

---

## 5. CQRS implementation

**The split is real, not nominal.** Commands and Queries take different paths from the moment they leave MediatR:

| Aspect | Commands (write side) | Queries (read side) |
|--------|----------------------|----------------------|
| Marker interface | `ICommand<T>` (`Application/Abstractions/Messaging/ICommand.cs`) | `IQuery<T>` (`Application/Abstractions/Messaging/IQuery.cs`) |
| Validator runs? | **Yes** (`ValidationBehavior` `where TRequest : class, ICommand<TResponse>`) | No |
| Data access | EF Core via `IWebinarRepository.Insert` + `IUnitOfWork.SaveChangesAsync` | Dapper via `IDbConnection.QueryFirstOrDefaultAsync<WebinarResponse>` |
| Returns | Primary key / Result | Read-model record (e.g. `WebinarResponse`) |
| Where the SQL lives | Hidden behind EF tracking | Inline string literal in the handler (`Application/.../GetWebinarQueryHandler.cs:11`) |

This is a valid CQRS shape, but the read path **leaks infrastructure into the Application layer** (`IDbConnection` is from `System.Data`, the SQL string knows the table name). The clean fix is documented in File 03.

---

## 6. Dependency injection structure

Everything is composed in `Web/Startup.cs:27-66`. There are no per-layer DI extension methods (no `AddApplication()`, `AddInfrastructure()`, `AddPresentation()`).

```csharp
// Startup.cs (excerpts)

// 1. Controllers from the Presentation assembly (lines 29-32)
var presentationAssembly = typeof(Presentation.AssemblyReference).Assembly;
services.AddControllers().AddApplicationPart(presentationAssembly);

// 2. MediatR + validators + pipeline (lines 34-40)
var applicationAssembly = typeof(Application.AssemblyReference).Assembly;
services.AddMediatR(applicationAssembly);
services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
services.AddValidatorsFromAssembly(applicationAssembly);

// 3. Swagger with XML doc inclusion (lines 42-52)
services.AddSwaggerGen(c =>
{
    var presentationDocumentationFile = $"{presentationAssembly.GetName().Name}.xml";
    var presentationDocumentationFilePath =
        Path.Combine(AppContext.BaseDirectory, presentationDocumentationFile);
    c.IncludeXmlComments(presentationDocumentationFilePath);
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Web", Version = "v1" });
});

// 4. EF Core DbContext (lines 54-55)
services.AddDbContext<ApplicationDbContext>(builder =>
    builder.UseNpgsql(Configuration.GetConnectionString("Application")));

// 5. Repository (line 57)
services.AddScoped<IWebinarRepository, WebinarRepository>();

// 6. UnitOfWork aliased to DbContext (lines 59-60) — same scope, same instance
services.AddScoped<IUnitOfWork>(
    factory => factory.GetRequiredService<ApplicationDbContext>());

// 7. Raw DbConnection for Dapper (lines 62-63)
services.AddScoped<IDbConnection>(
    factory => factory.GetRequiredService<ApplicationDbContext>().Database.GetDbConnection());

// 8. Exception middleware (line 65)
services.AddTransient<ExceptionHandlingMiddleware>();
```

**Lifetimes are correct:** DbContext, repositories, UoW and the borrowed `IDbConnection` are all `Scoped`, which keeps a single DbContext per HTTP request and lets the same change-tracking instance back both commands and SaveChanges. Middleware is `Transient` (the IMiddleware pattern).

What's wrong: there is no separation of concerns. As soon as a second module is added, `Startup.cs` becomes a junk drawer. Recommendation in File 03.

---

## 7. Middleware pipeline (exact order)

`Web/Startup.cs:68-88`:

```csharp
public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    if (env.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();           // line 72 (dev only)
        app.UseSwagger();                          // line 74
        app.UseSwaggerUI(c =>                      // line 75-76
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Web v1"));
    }

    app.UseMiddleware<ExceptionHandlingMiddleware>(); // line 79
    app.UseHttpsRedirection();                        // line 81
    app.UseRouting();                                 // line 83
    app.UseAuthorization();                           // line 85  ⚠ no-op
    app.UseEndpoints(endpoints => endpoints.MapControllers()); // line 87
}
```

Problems already visible: `UseAuthorization` runs without `UseAuthentication` and without any `AddAuthentication`/`AddAuthorization` in DI. It is dead weight that misleads readers into believing the app is protected. Also missing: CORS, request logging, correlation IDs, response compression, rate limiting.

---

## 8. Authentication & authorization flow

**Not implemented.** No `AddAuthentication`, no `AddAuthorization`, no `[Authorize]` attribute anywhere in the codebase. The `app.UseAuthorization()` line is registered but cannot enforce anything because no policies or schemes exist.

> **Recommended approach** — Introduce JWT bearer authentication in Web (composition root) and define authorization policies in Application:
>
> ```csharp
> // Web/Extensions/AuthenticationConfiguration.cs
> services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
>     .AddJwtBearer(options =>
>     {
>         options.TokenValidationParameters = new TokenValidationParameters
>         {
>             ValidateIssuer = true,
>             ValidateAudience = true,
>             ValidateLifetime = true,
>             ValidateIssuerSigningKey = true,
>             ValidIssuer = configuration["Jwt:Issuer"],
>             ValidAudience = configuration["Jwt:Audience"],
>             IssuerSigningKey = new SymmetricSecurityKey(
>                 Encoding.UTF8.GetBytes(configuration["Jwt:Key"]!))
>         };
>     });
> services.AddAuthorization(o =>
> {
>     o.AddPolicy("Admin", p => p.RequireRole("Admin"));
> });
> // ...
> app.UseAuthentication();  // MUST be before UseAuthorization
> app.UseAuthorization();
> ```
>
> Inject an `ICurrentUserService` (interface in `Application/Abstractions/`, implementation in `Infrastructure/Identity/` reading `IHttpContextAccessor`) so application handlers can ask "who am I acting on behalf of?" without coupling to ASP.NET.

---

## 9. Exception handling strategy

Centralized in `Web/Middleware/ExceptionHandlingMiddleware.cs`:

```csharp
httpContext.Response.StatusCode = exception switch
{
    BadRequestException or ValidationException => StatusCodes.Status400BadRequest,
    NotFoundException                          => StatusCodes.Status404NotFound,
    _                                          => StatusCodes.Status500InternalServerError
};
```

The middleware also flattens `ValidationException.Errors` into an array of `ApiError(PropertyName, ErrorMessage)` and writes JSON.

**Strengths:** typed exception → status code mapping, single chokepoint, validation errors structured.

**Weaknesses:** `exception.Message` is written straight to the response body (information leak), `LogError(e, e.Message)` discards the structured arguments pattern (Serilog-style template strings would be better), no `traceId` or correlation identifier in the response, no `ProblemDetails` (RFC 7807) shape — clients have to learn a bespoke contract.

---

## 10. Logging strategy

**What exists:** `appsettings.json` configures the built-in `Microsoft.Extensions.Logging` levels. That's it.

```json
"Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information"
    }
}
```

**Missing:** structured logging (Serilog/NLog), enricher for request/correlation IDs, sink to a centralized store (Seq/ELK/AppInsights), HTTP request logging middleware (`UseSerilogRequestLogging()`), EF Core SQL log routing.

> **Recommended approach** — Add Serilog with `IRequestEnricher` for `TraceId` + `UserId`, an Async sink, console + Seq/Elastic exporters; configure in `Program.cs` before `Build()`.

---

## 11. Validation strategy

**FluentValidation 11** with auto-discovery from the Application assembly (`Startup.cs:40`):

```csharp
services.AddValidatorsFromAssembly(applicationAssembly);
```

The `ValidationBehavior<,>` pipeline behavior runs validators **synchronously** (`Validate`, not `ValidateAsync`) and **only for Commands** (`where TRequest : class, ICommand<TResponse>`). Queries skip validation entirely — a defensible choice when queries don't carry user input that could be malformed, but worth a deliberate decision.

Validator rules in the only existing validator are anemic:

```csharp
public CreateWebinarCommandValidator()
{
    RuleFor(x => x.Name).NotEmpty();
    RuleFor(x => x.ScheduledOn).NotEmpty();
}
```

`Name` has no max length even though the DB column is `character varying(100)` (`WebinarConfiguration.cs:15`), and `ScheduledOn` has no future-date guard. Validation does not match the persistence contract.

---

## 12. Mapping strategy

**Mapster 7.3.0**. Used in exactly one place:

```csharp
// Presentation/Controllers/WebinarsController.cs:48
var command = request.Adapt<CreateWebinarCommand>();
```

There is no `TypeAdapterConfig` registration, no profile, no mapping module. `Adapt<T>()` works because `CreateWebinarRequest` and `CreateWebinarCommand` have structurally identical properties (`record (string Name, DateTime ScheduledOn)`), so Mapster's convention-based mapping succeeds at runtime. This is fragile: rename one property and silent mapping failures appear.

Mapster is also listed in `Application/Application.csproj` but **not referenced anywhere in Application** — dead dependency in that project.

> **Recommended approach** — Either remove the duplicate Request type (let the controller bind directly to the Command), or register an explicit `TypeAdapterConfig<CreateWebinarRequest, CreateWebinarCommand>` in a startup helper. File 02 (Section "How to add mappings") and File 03 cover the fix.

---

## 13. Configuration management

| File | Role |
|------|------|
| `Web/appsettings.json` | Base configuration; logging defaults; `AllowedHosts: "*"`. **No connection string** here. |
| `Web/appsettings.Development.json` | Dev overrides. **Plaintext** `User Id=postgres;Password=postgres` connection string. |
| `Web/Web.csproj` | Declares `<UserSecretsId>540db5be-b2a8-4c4d-ace3-5761b60b3c97</UserSecretsId>` — User Secrets is **enabled but unused**. |
| `docker-compose.yml` / `docker-compose.override.yml` | Repeat the plaintext PostgreSQL credentials in env vars. |

Configuration is read via the conventional `IConfiguration.GetConnectionString("Application")` (`Startup.cs:55`). There is no validation of required configuration values at startup, no Options pattern (`IOptions<T>`), no environment-specific overrides beyond the `Development` file.

---

## 14. Background jobs architecture

**Not implemented.** No `IHostedService`, no `BackgroundService`, no Hangfire / Quartz / Coravel.

> **Recommended approach** — For lightweight scheduling and "fire and forget" work pick **Hangfire** (`Hangfire.AspNetCore` + `Hangfire.PostgreSql`); it shares your database so it ships with no extra infra. Register in Web composition root. For high-throughput streaming, prefer a dedicated worker (`dotnet new worker`) consuming from a message broker (RabbitMQ / Azure Service Bus). Either way, define job contracts in Application (`IXxxJob` interfaces) so handlers remain testable.

---

## 15. Event-driven architecture

**Not implemented.** No domain events on the `Webinar` aggregate, no event dispatcher, no integration event bus, no Outbox.

> **Recommended approach** — Two layers:
> 1. **Domain events** — In-process; raised by aggregates, dispatched after `SaveChanges` by an EF `SaveChangesInterceptor` or a MediatR `INotification` chain. Use them to coordinate state changes inside the same bounded context.
> 2. **Integration events** — Cross-context / cross-service; persist via the Outbox pattern (write to an `OutboxMessages` table in the same transaction as the aggregate change, dispatch asynchronously via a background worker). `MassTransit` or a custom worker reading from PostgreSQL both work.

---

## 16. Caching strategy

**Not implemented.** No `IMemoryCache`, no `IDistributedCache`, no `Microsoft.Extensions.Caching.*` references.

> **Recommended approach** — Add a `ICachedQuery<TResponse>` marker on queries that should be cached, plus a `CachingBehavior<TRequest, TResponse>` MediatR behavior that wraps `IDistributedCache` (Redis in production, in-memory in development). Cache invalidation on writes is the engineering challenge — usually solved by tag-based eviction or domain event handlers that purge specific keys after commits.

---

## 17. API versioning strategy

**Not implemented.** No `Asp.Versioning.*` packages; Swagger is hard-coded to `"v1"` (`Startup.cs:51`) and routes use a static `[Route("api/[controller]")]` template.

> **Recommended approach** — Add `Asp.Versioning.Mvc.ApiExplorer`, switch the route template to `[Route("api/v{version:apiVersion}/[controller]")]`, and emit a separate Swagger document per version (`/swagger/v1/swagger.json`, `/swagger/v2/swagger.json`). Decorate controllers with `[ApiVersion("1.0")]`.

---

## 18. Executive summary scores (placeholder — full justification in File 03)

| Dimension | Score (/10) | Reasoning capsule |
|-----------|-------------|--------------------|
| Architecture | 6 | Layering, CQRS, exception strategy are right. Domain is anemic, reads leak Dapper into Application, DI is monolithic, auth absent. |
| Scalability | 4 | Stateless web service ✓, but no caching, no async processing, no queue, no read replica strategy, single migration. |
| Maintainability | 5 | Feature folders + thin controllers help. Startup.cs becomes a junk drawer the moment you add a second module. No tests at all. |
| Production readiness | 3 | Plaintext credentials in repo, no auth, no health checks, no observability, migrations on startup. |

These numbers reflect the **current** state. The same skeleton, with the refactors recommended in Files 03 / 04 / 05, would land in the 8-9 range across the board.

---

## 19. Recommended reading order for newcomers

If you're new to this codebase and want to understand it fast:

1. `Domain/Entities/Webinar.cs` (the only aggregate)
2. `Application/Abstractions/Messaging/ICommand.cs` & `IQuery.cs`
3. `Application/Webinars/Commands/CreateWebinar/` (all four files, in order)
4. `Application/Webinars/Queries/GetWebinarById/` (all three files, in order)
5. `Application/Behaviors/ValidationBehavior.cs`
6. `Infrastructure/ApplicationDbContext.cs` + `Configurations/WebinarConfiguration.cs` + `Repositories/WebinarRepository.cs`
7. `Presentation/Controllers/ApiController.cs` + `WebinarsController.cs`
8. `Web/Startup.cs`
9. `Web/Middleware/ExceptionHandlingMiddleware.cs`
10. `Web/Program.cs`

After step 5 you understand the **shape** of every future use case. After step 10 you understand how the host wires it all together.
