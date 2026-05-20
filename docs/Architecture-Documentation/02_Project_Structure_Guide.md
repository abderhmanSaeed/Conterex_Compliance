# 02 — Project Structure Guide

> A developer onboarding handbook. Everything below is derived from the **actual conventions in the codebase**, not from generic Clean Architecture lecture material. Examples mirror the existing `Webinar` feature so new work will be indistinguishable in style from existing work.

---

## 1. Folder-by-folder map

```
D:\Projects\Clean Architecture\
├── Domain/                  ← enterprise rules (entities, value objects, domain exceptions)
│   ├── Primitives/          ← `Entity`, future `ValueObject`, future `AggregateRoot`
│   ├── Entities/            ← aggregate roots and their child entities
│   ├── Abstractions/        ← repository contracts, `IUnitOfWork`, future `IDomainEventDispatcher`
│   ├── Exceptions/          ← domain-specific exceptions
│   │   └── Base/            ← `NotFoundException`, `BadRequestException` etc.
│   └── AssemblyReference.cs ← marker class for assembly scanning
│
├── Application/             ← use cases (orchestration, no business invariants)
│   ├── Abstractions/
│   │   └── Messaging/       ← `ICommand`, `IQuery`, `ICommandHandler`, `IQueryHandler`
│   ├── Behaviors/           ← MediatR pipeline behaviors
│   ├── Exceptions/          ← `ValidationException`
│   ├── <AggregateName>/     ← feature folder PER AGGREGATE (e.g. Webinars/, Speakers/)
│   │   ├── Commands/
│   │   │   └── <UseCase>/   ← one folder per use case (e.g. CreateWebinar/)
│   │   │       ├── <Use>Command.cs
│   │   │       ├── <Use>CommandHandler.cs
│   │   │       ├── <Use>CommandValidator.cs
│   │   │       └── <Use>Request.cs  (optional public DTO if shape differs from Command)
│   │   └── Queries/
│   │       └── <UseCase>/
│   │           ├── <Use>Query.cs
│   │           ├── <Use>QueryHandler.cs
│   │           └── <Use>Response.cs
│   └── AssemblyReference.cs
│
├── Infrastructure/          ← concrete implementations of Domain & Application abstractions
│   ├── ApplicationDbContext.cs        ← EF Core DbContext + implements IUnitOfWork
│   ├── Configurations/                ← `IEntityTypeConfiguration<T>` files
│   ├── Repositories/                  ← repository implementations
│   ├── Migrations/                    ← `dotnet ef` generated migrations
│   └── AssemblyReference.cs
│
├── Presentation/            ← HTTP transport layer
│   ├── Controllers/
│   │   ├── ApiController.cs           ← base class for all controllers
│   │   └── <Aggregate>sController.cs  ← one controller per aggregate
│   └── AssemblyReference.cs
│
└── Web/                     ← host & composition root
    ├── Program.cs                     ← entry point
    ├── Startup.cs                     ← DI + middleware (target: move to extension methods)
    ├── Middleware/                    ← custom middleware (e.g. ExceptionHandlingMiddleware)
    ├── Properties/launchSettings.json
    ├── appsettings.json
    ├── appsettings.Development.json
    ├── Dockerfile
    └── AssemblyReference.cs
```

---

## 2. Layer responsibility contract (what's allowed where)

| Layer | MAY contain | MUST NOT contain |
|-------|-------------|-------------------|
| **Domain** | Entities, value objects, domain events, repository contracts (interfaces), domain exceptions, domain services (pure C#) | EF Core / Dapper / ASP.NET / `System.Data` references; serialization attributes; logging; HTTP concepts; any NuGet packages |
| **Application** | Commands, queries, handlers, validators, pipeline behaviors, application contracts (`IDateTimeProvider`, `ICurrentUserService`), DTOs | EF Core DbContext access, raw SQL, ASP.NET types (`HttpContext`), file-system / network calls, Mapster / FluentValidation should be the **only** non-Domain NuGet deps |
| **Infrastructure** | DbContext, EF configurations, repository implementations, migrations, HttpClient wrappers, file storage, email senders | Direct references from Application or Presentation; only Web touches it |
| **Presentation** | Controllers, controller-only DTOs (rare), Swagger XML, OpenAPI examples | Business logic, EF queries, repository calls — those go through MediatR |
| **Web** | DI registration, middleware, hosting config, Dockerfile, settings | Domain rules, repository implementations, controller code |

**Hard rule:** when in doubt about which layer owns a concept, ask "*could this exist if we replaced PostgreSQL with MongoDB or replaced ASP.NET with gRPC?*" If yes, it belongs in Domain or Application. If no, it belongs in Infrastructure, Presentation, or Web.

---

## 3. Naming conventions (extracted from the codebase)

These are the rules that make existing files **look like they belong together** — keep new files following them:

| Element | Convention | Example |
|---------|-----------|---------|
| Entities | `sealed class <Name> : Entity` | `Webinar : Entity` (`Domain/Entities/Webinar.cs:6`) |
| Properties on entities | `public T Prop { get; private set; }` | `public string Name { get; private set; }` (`Webinar.cs:18`) |
| Commands | `public sealed record <Verb><Aggregate>Command(...) : ICommand<T>;` | `CreateWebinarCommand` (`CreateWebinarCommand.cs:5`) |
| Queries | `public sealed record <Verb><Aggregate>Query(...) : IQuery<T>;` (often `Get<Aggregate>ByX`) | `GetWebinarByIdQuery` (`GetWebinarByIdQuery.cs:5`) |
| Command/Query handlers | `internal sealed class <Use>CommandHandler : ICommandHandler<<Use>Command, T>` | `CreateWebinarCommandHandler` (`CreateWebinarCommandHandler.cs:5`) |
| Validators | `public sealed class <Use>CommandValidator : AbstractValidator<<Use>Command>` | `CreateWebinarCommandValidator` (`CreateWebinarCommandValidator.cs:5`) |
| Request DTOs (controller-bound) | `public sealed record <Use>Request(...);` | `CreateWebinarRequest` |
| Response DTOs (query results) | `public sealed record <Aggregate>Response(...);` | `WebinarResponse` |
| Repository contract | `IXxxRepository` in `Domain/Abstractions` | `IWebinarRepository` |
| Repository implementation | `XxxRepository : IXxxRepository` in `Infrastructure/Repositories` | `WebinarRepository` |
| EF configurations | `internal sealed class XxxConfiguration : IEntityTypeConfiguration<Xxx>` in `Infrastructure/Configurations` | `WebinarConfiguration` |
| Domain exceptions | `XxxNotFoundException : NotFoundException`, `XxxInvalidException : BadRequestException` | `WebinarNotFoundException` |
| Controllers | `<Aggregate>sController : ApiController` (plural; sealed) | `WebinarsController` |
| Middleware | `internal sealed class XxxMiddleware : IMiddleware` | `ExceptionHandlingMiddleware` |
| Assembly markers | `public class AssemblyReference { }` (one per project) | every project has one |

**Use `record` for immutable data transfer types.** Use `class` only when behavior or mutable identity is involved.

**Use `sealed`** unless the type is genuinely designed for inheritance (it almost never is).

**Use `internal sealed`** for handler implementations — they should not be invoked except via MediatR.

**Constructor pattern** — single constructor, `private readonly` fields, expression-bodied ctor when there's only one assignment, classic block ctor for two or more. Look at `WebinarRepository.cs:9` for the one-liner pattern.

---

## 4. File organization rules

1. **One folder per use case.** Resist the temptation to put every command into a single `Commands/` folder. The existing `Webinars/Commands/CreateWebinar/` pattern keeps every file you need to touch for a single feature in one place.
2. **One controller per aggregate.** `WebinarsController` handles all Webinar endpoints. If endpoints exceed roughly a dozen, consider splitting by sub-aggregate (e.g. `WebinarRegistrationsController`).
3. **Configurations live next to each other.** `Infrastructure/Configurations/` is a flat folder — no per-aggregate subdirectory needed unless the count exceeds ~10.
4. **Migrations are append-only.** Never edit a generated migration after merge to main. Add a new migration instead.
5. **AssemblyReference.cs at the root of each project.** Empty marker class; `typeof(AssemblyReference).Assembly` is used by `AddMediatR(...)`, `AddValidatorsFromAssembly(...)`, `AddApplicationPart(...)`, and `ApplyConfigurationsFromAssembly(...)`.

---

## 5. How to add a new feature (end-to-end)

The most common task: add a new use case to an existing aggregate. The pattern is the same whether it's a command or a query.

### 5.1 Adding a new Command (write) — example: `CancelWebinar`

**Step 1 — Define the Command in Application:**
```csharp
// Application/Webinars/Commands/CancelWebinar/CancelWebinarCommand.cs
namespace Application.Webinars.Commands.CancelWebinar;

public sealed record CancelWebinarCommand(Guid WebinarId, string Reason) : ICommand<Unit>;
```

(Use `Unit` from MediatR when the command doesn't return data, or a `Guid` / specific record when it does.)

**Step 2 — Define the Validator:**
```csharp
// Application/Webinars/Commands/CancelWebinar/CancelWebinarCommandValidator.cs
using FluentValidation;

namespace Application.Webinars.Commands.CancelWebinar;

public sealed class CancelWebinarCommandValidator : AbstractValidator<CancelWebinarCommand>
{
    public CancelWebinarCommandValidator()
    {
        RuleFor(x => x.WebinarId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}
```

**Step 3 — Define the Handler:**
```csharp
// Application/Webinars/Commands/CancelWebinar/CancelWebinarCommandHandler.cs
using Application.Abstractions.Messaging;
using Domain.Abstractions;
using Domain.Exceptions;
using MediatR;

namespace Application.Webinars.Commands.CancelWebinar;

internal sealed class CancelWebinarCommandHandler : ICommandHandler<CancelWebinarCommand, Unit>
{
    private readonly IWebinarRepository _webinarRepository;
    private readonly IUnitOfWork _unitOfWork;

    public CancelWebinarCommandHandler(IWebinarRepository webinarRepository, IUnitOfWork unitOfWork)
    {
        _webinarRepository = webinarRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Unit> Handle(CancelWebinarCommand request, CancellationToken cancellationToken)
    {
        // Note: the current IWebinarRepository has no GetByIdAsync. You will need to
        // extend it (see Section 9). Once extended:
        var webinar = await _webinarRepository.GetByIdAsync(request.WebinarId, cancellationToken)
            ?? throw new WebinarNotFoundException(request.WebinarId);

        webinar.Cancel(request.Reason);  // requires enriching the domain (see Domain section in File 03)

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
```

**Step 4 — Optional Request DTO** (only if the wire shape must differ from the command — usually it shouldn't; bind the Command directly):
```csharp
// Application/Webinars/Commands/CancelWebinar/CancelWebinarRequest.cs
public sealed record CancelWebinarRequest(string Reason);
```

**Step 5 — Add the controller method** in `Presentation/Controllers/WebinarsController.cs`:
```csharp
[HttpPost("{webinarId:guid}/cancel")]
[ProducesResponseType(StatusCodes.Status204NoContent)]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
public async Task<IActionResult> CancelWebinar(
    Guid webinarId,
    [FromBody] CancelWebinarRequest request,
    CancellationToken cancellationToken)
{
    var command = new CancelWebinarCommand(webinarId, request.Reason);
    await Sender.Send(command, cancellationToken);
    return NoContent();
}
```

**Step 6 — No DI registration needed.** MediatR's assembly scanning (`AddMediatR(applicationAssembly)`) and FluentValidation's (`AddValidatorsFromAssembly(applicationAssembly)`) pick up the new handler and validator automatically.

### 5.2 Adding a new Query (read) — example: `ListUpcomingWebinars`

**Step 1 — Query:**
```csharp
// Application/Webinars/Queries/ListUpcomingWebinars/ListUpcomingWebinarsQuery.cs
public sealed record ListUpcomingWebinarsQuery(int PageNumber, int PageSize)
    : IQuery<IReadOnlyCollection<WebinarSummaryResponse>>;
```

**Step 2 — Response:**
```csharp
// Application/Webinars/Queries/ListUpcomingWebinars/WebinarSummaryResponse.cs
public sealed record WebinarSummaryResponse(Guid Id, string Name, DateTime ScheduledOn);
```

**Step 3 — Handler** — uses Dapper today (matching existing convention; see File 03 for the long-term fix that abstracts this):
```csharp
internal sealed class ListUpcomingWebinarsQueryHandler
    : IQueryHandler<ListUpcomingWebinarsQuery, IReadOnlyCollection<WebinarSummaryResponse>>
{
    private readonly IDbConnection _dbConnection;

    public ListUpcomingWebinarsQueryHandler(IDbConnection dbConnection) =>
        _dbConnection = dbConnection;

    public async Task<IReadOnlyCollection<WebinarSummaryResponse>> Handle(
        ListUpcomingWebinarsQuery request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "Id", "Name", "ScheduledOn"
            FROM "Webinars"
            WHERE "ScheduledOn" > NOW()
            ORDER BY "ScheduledOn"
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        var rows = await _dbConnection.QueryAsync<WebinarSummaryResponse>(
            sql,
            new { Offset = (request.PageNumber - 1) * request.PageSize, request.PageSize });

        return rows.ToList();
    }
}
```

**Step 4 — Controller endpoint:**
```csharp
[HttpGet("upcoming")]
[ProducesResponseType(typeof(IReadOnlyCollection<WebinarSummaryResponse>), StatusCodes.Status200OK)]
public async Task<IActionResult> ListUpcoming(
    [FromQuery] int pageNumber = 1,
    [FromQuery] int pageSize = 20,
    CancellationToken cancellationToken = default)
{
    var result = await Sender.Send(new ListUpcomingWebinarsQuery(pageNumber, pageSize), cancellationToken);
    return Ok(result);
}
```

---

## 6. How to add a new entity (aggregate)

Worked example: `Speaker`.

**Step 1 — Domain entity:**
```csharp
// Domain/Entities/Speaker.cs
using Domain.Primitives;

namespace Domain.Entities;

public sealed class Speaker : Entity
{
    public Speaker(Guid id, string fullName, string email) : base(id)
    {
        FullName = fullName;
        Email = email;
    }

    private Speaker() { }  // EF Core

    public string FullName { get; private set; }
    public string Email { get; private set; }
}
```

(Once the Domain is enriched per File 03, prefer a `Speaker.Create(...)` factory with invariant checks.)

**Step 2 — Repository contract** in `Domain/Abstractions/ISpeakerRepository.cs`:
```csharp
public interface ISpeakerRepository
{
    void Insert(Speaker speaker);
    Task<Speaker?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
}
```

**Step 3 — EF configuration** in `Infrastructure/Configurations/SpeakerConfiguration.cs`:
```csharp
internal sealed class SpeakerConfiguration : IEntityTypeConfiguration<Speaker>
{
    public void Configure(EntityTypeBuilder<Speaker> builder)
    {
        builder.ToTable("Speakers");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.FullName).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Email).HasMaxLength(320).IsRequired();
        builder.HasIndex(s => s.Email).IsUnique();
    }
}
```

**Step 4 — Repository implementation** in `Infrastructure/Repositories/SpeakerRepository.cs`:
```csharp
public sealed class SpeakerRepository : ISpeakerRepository
{
    private readonly ApplicationDbContext _dbContext;

    public SpeakerRepository(ApplicationDbContext dbContext) => _dbContext = dbContext;

    public void Insert(Speaker speaker) => _dbContext.Set<Speaker>().Add(speaker);

    public Task<Speaker?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _dbContext.Set<Speaker>().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
}
```

**Step 5 — DI registration** in `Web/Startup.cs` (next to the existing `IWebinarRepository` line — although see File 03 §"DI bloat" for the recommended extraction into `AddInfrastructure()`):
```csharp
services.AddScoped<ISpeakerRepository, SpeakerRepository>();
```

**Step 6 — Migration:**
```powershell
dotnet ef migrations add Add_Speakers_Table `
    --project Infrastructure `
    --startup-project Web `
    --output-dir Migrations
```

**Step 7 — Apply** locally (`dotnet ef database update --project Infrastructure --startup-project Web`) — in production, do **not** run `Database.MigrateAsync()` from `Program.cs`; use `dotnet ef migrations bundle` or `dotnet-ef` in the deploy pipeline. See File 03.

---

## 7. How to add a new API endpoint

If the endpoint relates to an **existing** aggregate, add a method to that aggregate's controller. Pattern:

```csharp
[HttpVerb("optional-route-template")]
[ProducesResponseType(typeof(TResponse), StatusCodes.Status200OK)]    // or 201/204
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status404NotFound)]                  // only if applicable
public async Task<IActionResult> MethodName(
    [route/query/body bindings],
    CancellationToken cancellationToken)
{
    var commandOrQuery = ...;
    var result = await Sender.Send(commandOrQuery, cancellationToken);
    return Ok(result); // or CreatedAtAction, NoContent, etc.
}
```

If the endpoint relates to a **new** aggregate, create a new controller:

```csharp
// Presentation/Controllers/SpeakersController.cs
public sealed class SpeakersController : ApiController
{
    [HttpGet("{speakerId:guid}")]
    public async Task<IActionResult> Get(Guid speakerId, CancellationToken ct) =>
        Ok(await Sender.Send(new GetSpeakerByIdQuery(speakerId), ct));
}
```

The controller is **automatically picked up** by the `AddApplicationPart(presentationAssembly)` call in `Startup.cs:32`. No registration required.

---

## 8. How to add a new application service

Application services are reusable cross-cutting capabilities (date provider, current user, email, file storage). The contract lives in **Application**, the implementation in **Infrastructure** or **Web**.

**Step 1 — Contract:**
```csharp
// Application/Abstractions/IDateTimeProvider.cs
public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
}
```

**Step 2 — Implementation:**
```csharp
// Infrastructure/Services/SystemDateTimeProvider.cs
internal sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}
```

**Step 3 — DI registration** in the appropriate composition extension:
```csharp
services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
```

**Pick the right lifetime:** stateless ⇒ `Singleton`; per-request context ⇒ `Scoped`; everything else (rare) ⇒ `Transient`. Repositories and DbContext are always `Scoped`.

---

## 9. How to add a new repository

**Contract first** (in `Domain/Abstractions/`):
```csharp
public interface ISomethingRepository
{
    void Insert(Something something);
    Task<Something?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    void Remove(Something something);
    // Add ONLY methods the domain actually needs.
}
```

**Implementation** (in `Infrastructure/Repositories/`):
```csharp
public sealed class SomethingRepository : ISomethingRepository
{
    private readonly ApplicationDbContext _dbContext;
    public SomethingRepository(ApplicationDbContext dbContext) => _dbContext = dbContext;

    public void Insert(Something something) => _dbContext.Set<Something>().Add(something);

    public Task<Something?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _dbContext.Set<Something>()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public void Remove(Something something) => _dbContext.Set<Something>().Remove(something);
}
```

**Don't** expose `IQueryable<T>` from repositories — it leaks ORM concerns up the stack and breaks the abstraction.

**Don't** create a generic `IRepository<T>` — it tempts callers into "any entity I want, any way I want," which is the symptom that killed Generic Repository in DDD writing. One purposeful repository per aggregate.

**Register** the implementation in Web's DI (or, after the File 03 refactor, in `Infrastructure.DependencyInjection.AddInfrastructure(...)`).

---

## 10. How to add a new DTO

DTOs in this codebase are all **`sealed record`s**. Pick the right location:

| DTO type | Location | Example |
|----------|----------|---------|
| Request DTO (HTTP body input) | Inside the relevant feature folder | `Application/Webinars/Commands/CreateWebinar/CreateWebinarRequest.cs` |
| Response DTO (read model) | Inside the relevant feature folder | `Application/Webinars/Queries/GetWebinarById/WebinarResponse.cs` |
| Cross-feature shared DTO | A new shared folder like `Application/Webinars/Shared/` | (doesn't exist yet) |
| Controller-only DTO | Avoid — keep DTOs in Application where they can be reused by tests | — |

Naming: `<Use>Request`, `<Aggregate>Response`, `<Aggregate>Dto` (only when neither Request nor Response fits).

---

## 11. How to add a new mapping

The codebase currently uses Mapster's convention-based `Adapt<T>()` for one call (`WebinarsController.CreateWebinar`). For anything non-trivial — different property names, computed fields, nested mappings — register an **explicit** Mapster config:

```csharp
// Application/Mapping/WebinarMappingConfig.cs  (new folder)
using Mapster;
using Application.Webinars.Commands.CreateWebinar;
using Domain.Entities;

public sealed class WebinarMappingConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<CreateWebinarRequest, CreateWebinarCommand>();

        config.NewConfig<Webinar, WebinarResponse>()
              .Map(dest => dest.Id, src => src.Id)
              .Map(dest => dest.Name, src => src.Name)
              .Map(dest => dest.ScheduledOn, src => src.ScheduledOn);
    }
}
```

Register at startup:
```csharp
var config = TypeAdapterConfig.GlobalSettings;
config.Scan(typeof(Application.AssemblyReference).Assembly);
services.AddSingleton(config);
services.AddScoped<IMapper, ServiceMapper>();
```

The current codebase **does not** do this; it relies on convention. File 03 marks this as a Medium-severity gap.

---

## 12. How to add an integration (external system)

External-system clients live in **Infrastructure** behind an **Application-defined contract**:

**Step 1 — Contract in Application:**
```csharp
// Application/Abstractions/Integrations/IEmailSender.cs
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct);
}
```

**Step 2 — Implementation in Infrastructure** (typed HttpClient pattern):
```csharp
// Infrastructure/Integrations/Email/SendGridEmailSender.cs
internal sealed class SendGridEmailSender : IEmailSender
{
    private readonly HttpClient _httpClient;
    public SendGridEmailSender(HttpClient httpClient) => _httpClient = httpClient;

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct)
    {
        // call SendGrid
    }
}
```

**Step 3 — Registration:**
```csharp
services.AddHttpClient<IEmailSender, SendGridEmailSender>(client =>
{
    client.BaseAddress = new Uri(configuration["Integrations:SendGrid:BaseUrl"]!);
    client.DefaultRequestHeaders.Add(
        "Authorization",
        $"Bearer {configuration["Integrations:SendGrid:ApiKey"]}");
})
.AddPolicyHandler(GetRetryPolicy())          // Polly (recommended addition — see File 06)
.AddPolicyHandler(GetCircuitBreakerPolicy());
```

Application code only ever sees `IEmailSender`. The handler doesn't know whether the implementation calls SendGrid, SES, or a fake test sender.

---

## 13. How to add a background job

> **Status: Not implemented.** The codebase has no hosted service or scheduling library. Until Hangfire (or similar) is added, this section is forward-looking.

**Recommended pattern** (Hangfire):

**Step 1 — Add packages** to `Web.csproj`:
```xml
<PackageReference Include="Hangfire.AspNetCore" Version="1.8.x" />
<PackageReference Include="Hangfire.PostgreSql" Version="1.20.x" />
```

**Step 2 — Define job contract** in Application:
```csharp
// Application/Abstractions/Jobs/ISendUpcomingWebinarRemindersJob.cs
public interface ISendUpcomingWebinarRemindersJob
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}
```

**Step 3 — Implement** in Infrastructure:
```csharp
// Infrastructure/Jobs/SendUpcomingWebinarRemindersJob.cs
internal sealed class SendUpcomingWebinarRemindersJob : ISendUpcomingWebinarRemindersJob
{
    // injects repository + email sender + ICurrentUserService
    public Task ExecuteAsync(CancellationToken cancellationToken) { /* ... */ }
}
```

**Step 4 — Register & schedule** in Web composition:
```csharp
services.AddHangfire(cfg => cfg.UsePostgreSqlStorage(
    configuration.GetConnectionString("Application")));
services.AddHangfireServer();

// Later, in the pipeline:
app.UseHangfireDashboard("/jobs");
RecurringJob.AddOrUpdate<ISendUpcomingWebinarRemindersJob>(
    "send-upcoming-reminders",
    j => j.ExecuteAsync(CancellationToken.None),
    Cron.Hourly);
```

---

## 14. How to add a validation rule

Validators live next to the command they validate. Always:
- Mark the validator `public sealed`.
- Constructor-build the rules.
- Validate **all** properties the handler depends on.
- Match constraints to the database (`MaxLength(100)` on `Name` because the column is `varchar(100)`).
- For business rules requiring data access (e.g. "name must be unique"), inject the repository **into the validator**:
  ```csharp
  public CreateWebinarCommandValidator(IWebinarRepository repository)
  {
      RuleFor(x => x.Name)
          .NotEmpty()
          .MaximumLength(100)
          .MustAsync(async (name, ct) =>
              !await repository.AnyByNameAsync(name, ct))
          .WithMessage("A webinar with that name already exists.");
  }
  ```
  Note: this requires switching `ValidationBehavior.Validate(context)` to `await validator.ValidateAsync(context, ct)` — see File 03.

---

## 15. How to add a CQRS Command/Query

Already covered fully in Section 5 above. The short version:

1. Create a feature folder: `Application/<Aggregate>/Commands/<UseCase>/` or `Queries/<UseCase>/`.
2. Add the `<Use>Command.cs` or `<Use>Query.cs` (sealed record implementing `ICommand<T>` / `IQuery<T>`).
3. Add the `<Use>Handler.cs` (internal sealed, implements the matching handler interface).
4. If a Command, add the `<Use>CommandValidator.cs`.
5. If the wire shape differs from the command/query, add `<Use>Request.cs` (Command) or `<Use>Response.cs` (Query).
6. Wire the controller endpoint.

---

## 16. Consistency checklist (use before opening a PR)

- [ ] New entity is `sealed`, has `private set` properties, and a private parameterless ctor for EF
- [ ] Domain layer has no NuGet, no EF/ASP.NET references
- [ ] New command / query is a `sealed record` implementing `ICommand<T>` or `IQuery<T>` (not `IRequest<T>` directly)
- [ ] New handler is `internal sealed`
- [ ] New validator extends `AbstractValidator<TCommand>` and includes max-length / range / future-date rules that match EF configuration and business rules
- [ ] New repository contract is in `Domain/Abstractions/` and implementation in `Infrastructure/Repositories/`
- [ ] New EF configuration is in `Infrastructure/Configurations/`
- [ ] New DI registration uses the right lifetime (`Scoped` for stateful, `Singleton` for stateless)
- [ ] New endpoint inherits `ApiController`, uses `Sender.Send(...)`, returns proper status codes, and is decorated with `[ProducesResponseType]`
- [ ] Migration generated via `dotnet ef migrations add ...` (not hand-written) and reviewed
- [ ] No `IQueryable` returned from repository
- [ ] No EF / Dapper code outside Infrastructure (the `IDbConnection` leak in queries is a known issue tracked in File 03)
- [ ] No `Console.WriteLine` / `Debug.WriteLine` (use the logger)
- [ ] All async methods accept `CancellationToken`
- [ ] No secrets in committed files (`appsettings.*.json`, `docker-compose*.yml`)
