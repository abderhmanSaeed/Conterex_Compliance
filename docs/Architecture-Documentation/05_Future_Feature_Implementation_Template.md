# 05 — Future Feature Implementation Templates

> Copy-paste-ready templates that match the **actual conventions** of this codebase. If you follow them, your new code will be indistinguishable in style from the existing `Webinar` feature. The reference example throughout is a hypothetical `Speaker` aggregate (someone who presents at webinars).

Every template states **file paths to create**, **order of operations**, and **DI registrations needed**.

---

## Convention reminders

These are non-negotiable; everything below assumes them:

| Element | Style |
|---------|-------|
| DTOs / Commands / Queries | `public sealed record Name(...);` |
| Handlers | `internal sealed class …Handler : ICommand/QueryHandler<…>` |
| Validators | `public sealed class …Validator : AbstractValidator<…>` |
| Entities | `public sealed class … : Entity` with `private set` properties + private parameterless ctor |
| Repository interface | In `Domain/Abstractions/` — only the methods your code *actually* needs |
| Repository implementation | `public sealed class …Repository : I…Repository` in `Infrastructure/Repositories/` |
| EF configuration | `internal sealed class …Configuration : IEntityTypeConfiguration<T>` in `Infrastructure/Configurations/` |
| Controllers | `public sealed class …sController : ApiController` (plural) |
| Async | every `Task`-returning method takes `CancellationToken cancellationToken` |
| Namespaces | one per file, matching the folder path |
| Nullable | enable on every `.csproj` (`<Nullable>enable</Nullable>`) — see File 03 §26 |

---

## Template 1 — New Module (aggregate folder)

A "module" here is the folder structure for a new aggregate. Use it once, at the start of any new aggregate's work.

### Files to create

```
Domain/
├── Entities/Speaker.cs
└── Abstractions/ISpeakerRepository.cs

Application/
└── Speakers/                        ← module folder (created lazily as features are added)

Infrastructure/
├── Configurations/SpeakerConfiguration.cs
└── Repositories/SpeakerRepository.cs

Presentation/
└── Controllers/SpeakersController.cs
```

### Order of operations

1. Domain entity.
2. Repository interface in Domain.
3. EF configuration in Infrastructure.
4. Repository implementation in Infrastructure.
5. DI registration: `services.AddScoped<ISpeakerRepository, SpeakerRepository>();`
6. Migration: `dotnet ef migrations add Add_Speakers_Table -p Infrastructure -s Web`.
7. Empty controller skeleton (will be populated by Templates 4-6).

The first command/query for the new aggregate uses Template 9 below; the folder `Application/Speakers/Commands/...` is created at that point.

---

## Template 2 — New Entity

### File: `Domain/Entities/Speaker.cs`

```csharp
using Domain.Primitives;

namespace Domain.Entities;

public sealed class Speaker : Entity
{
    public Speaker(Guid id, string fullName, string email, string? bio) : base(id)
    {
        FullName = fullName;
        Email = email;
        Bio = bio;
    }

    private Speaker() { } // EF Core materialization

    public string FullName { get; private set; } = default!;
    public string Email    { get; private set; } = default!;
    public string? Bio     { get; private set; }
}
```

(After the domain enrichment recommended in File 03 §3, replace the public constructor with a `static Speaker Create(...)` factory that validates invariants and raises a `SpeakerCreatedDomainEvent`.)

### File: `Infrastructure/Configurations/SpeakerConfiguration.cs`

```csharp
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Configurations;

internal sealed class SpeakerConfiguration : IEntityTypeConfiguration<Speaker>
{
    public void Configure(EntityTypeBuilder<Speaker> builder)
    {
        builder.ToTable("Speakers");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.FullName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(s => s.Email)
            .HasMaxLength(320)        // RFC 5321 max email length
            .IsRequired();

        builder.Property(s => s.Bio)
            .HasMaxLength(4000);

        builder.HasIndex(s => s.Email).IsUnique();
    }
}
```

### Migration

```powershell
dotnet ef migrations add Add_Speakers_Table `
    --project Infrastructure `
    --startup-project Web `
    --output-dir Migrations
```

Review the generated migration, then either apply it locally with `dotnet ef database update -p Infrastructure -s Web` (development) or let your deploy pipeline run the bundle (production — see File 03 §4).

---

## Template 3 — New Repository

### File: `Domain/Abstractions/ISpeakerRepository.cs`

```csharp
using Domain.Entities;

namespace Domain.Abstractions;

public interface ISpeakerRepository
{
    void Insert(Speaker speaker);
    void Remove(Speaker speaker);
    Task<Speaker?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);
}
```

Add only the methods you have a caller for. Resist a kitchen-sink `IGenericRepository<T>` — it leaks into handlers and is impossible to mock cleanly.

### File: `Infrastructure/Repositories/SpeakerRepository.cs`

```csharp
using Domain.Abstractions;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public sealed class SpeakerRepository : ISpeakerRepository
{
    private readonly ApplicationDbContext _dbContext;

    public SpeakerRepository(ApplicationDbContext dbContext) => _dbContext = dbContext;

    public void Insert(Speaker speaker) => _dbContext.Set<Speaker>().Add(speaker);

    public void Remove(Speaker speaker) => _dbContext.Set<Speaker>().Remove(speaker);

    public Task<Speaker?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _dbContext.Set<Speaker>().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken) =>
        _dbContext.Set<Speaker>().AnyAsync(s => s.Email == email, cancellationToken);
}
```

### DI registration

Today (in `Web/Startup.cs:57` next to the existing line):

```csharp
services.AddScoped<ISpeakerRepository, SpeakerRepository>();
```

After applying File 03 §11 (DI extension methods):

```csharp
// Infrastructure/DependencyInjection.cs
services.AddScoped<ISpeakerRepository, SpeakerRepository>();
```

---

## Template 4 — New CQRS Command (write use case)

Example: `RegisterSpeaker`.

### File list
```
Application/Speakers/Commands/RegisterSpeaker/
├── RegisterSpeakerCommand.cs
├── RegisterSpeakerCommandHandler.cs
└── RegisterSpeakerCommandValidator.cs
```

(Add `RegisterSpeakerRequest.cs` only if the wire shape differs from the command — see File 03 §12.)

### `RegisterSpeakerCommand.cs`

```csharp
using Application.Abstractions.Messaging;

namespace Application.Speakers.Commands.RegisterSpeaker;

public sealed record RegisterSpeakerCommand(string FullName, string Email, string? Bio)
    : ICommand<Guid>;
```

### `RegisterSpeakerCommandValidator.cs`

```csharp
using FluentValidation;

namespace Application.Speakers.Commands.RegisterSpeaker;

public sealed class RegisterSpeakerCommandValidator : AbstractValidator<RegisterSpeakerCommand>
{
    public RegisterSpeakerCommandValidator()
    {
        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Full name is required.")
            .MaximumLength(200);

        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(320);

        RuleFor(x => x.Bio)
            .MaximumLength(4000)
            .When(x => x.Bio is not null);
    }
}
```

### `RegisterSpeakerCommandHandler.cs`

```csharp
using Application.Abstractions.Messaging;
using Domain.Abstractions;
using Domain.Entities;
using Domain.Exceptions;

namespace Application.Speakers.Commands.RegisterSpeaker;

internal sealed class RegisterSpeakerCommandHandler : ICommandHandler<RegisterSpeakerCommand, Guid>
{
    private readonly ISpeakerRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public RegisterSpeakerCommandHandler(
        ISpeakerRepository repository,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Guid> Handle(RegisterSpeakerCommand request, CancellationToken cancellationToken)
    {
        if (await _repository.EmailExistsAsync(request.Email, cancellationToken))
            throw new SpeakerEmailAlreadyExistsException(request.Email);

        var speaker = new Speaker(Guid.NewGuid(), request.FullName, request.Email, request.Bio);

        _repository.Insert(speaker);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return speaker.Id;
    }
}
```

(Add the corresponding `SpeakerEmailAlreadyExistsException : BadRequestException` to `Domain/Exceptions/`.)

### DI

Nothing to register. MediatR (`AddMediatR(applicationAssembly)`) and FluentValidation (`AddValidatorsFromAssembly(applicationAssembly)`) scan the Application assembly at startup and pick up the new handler and validator automatically.

---

## Template 5 — New CQRS Query (read use case)

Example: `GetSpeakerById`.

### File list
```
Application/Speakers/Queries/GetSpeakerById/
├── GetSpeakerByIdQuery.cs
├── GetSpeakerByIdQueryHandler.cs
└── SpeakerResponse.cs
```

### `GetSpeakerByIdQuery.cs`

```csharp
using Application.Abstractions.Messaging;

namespace Application.Speakers.Queries.GetSpeakerById;

public sealed record GetSpeakerByIdQuery(Guid SpeakerId) : IQuery<SpeakerResponse>;
```

### `SpeakerResponse.cs`

```csharp
namespace Application.Speakers.Queries.GetSpeakerById;

public sealed record SpeakerResponse(Guid Id, string FullName, string Email, string? Bio);
```

### `GetSpeakerByIdQueryHandler.cs` — current convention (inline Dapper)

```csharp
using System.Data;
using Application.Abstractions.Messaging;
using Dapper;
using Domain.Exceptions;

namespace Application.Speakers.Queries.GetSpeakerById;

internal sealed class GetSpeakerByIdQueryHandler : IQueryHandler<GetSpeakerByIdQuery, SpeakerResponse>
{
    private readonly IDbConnection _dbConnection;

    public GetSpeakerByIdQueryHandler(IDbConnection dbConnection) => _dbConnection = dbConnection;

    public async Task<SpeakerResponse> Handle(GetSpeakerByIdQuery request, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "Id", "FullName", "Email", "Bio"
            FROM "Speakers"
            WHERE "Id" = @SpeakerId;
            """;

        var speaker = await _dbConnection.QueryFirstOrDefaultAsync<SpeakerResponse>(
            new CommandDefinition(sql, new { request.SpeakerId }, cancellationToken: cancellationToken));

        return speaker ?? throw new SpeakerNotFoundException(request.SpeakerId);
    }
}
```

### `GetSpeakerByIdQueryHandler.cs` — recommended convention (read repository — see File 03 §5)

```csharp
internal sealed class GetSpeakerByIdQueryHandler : IQueryHandler<GetSpeakerByIdQuery, SpeakerResponse>
{
    private readonly ISpeakerReadRepository _reads;
    public GetSpeakerByIdQueryHandler(ISpeakerReadRepository reads) => _reads = reads;

    public async Task<SpeakerResponse> Handle(GetSpeakerByIdQuery request, CancellationToken cancellationToken)
        => await _reads.GetByIdAsync(request.SpeakerId, cancellationToken)
           ?? throw new SpeakerNotFoundException(request.SpeakerId);
}
```

(`ISpeakerReadRepository` lives in `Application/Abstractions/Data/`, implementation lives in `Infrastructure/Repositories/`, registered in `AddInfrastructure()`.)

### DI

Same as Commands — none required; assembly scanning picks it up.

---

## Template 6 — New API Endpoint

If the endpoint belongs to an existing controller, add a method:

```csharp
// Presentation/Controllers/SpeakersController.cs
[HttpGet("{speakerId:guid}")]
[ProducesResponseType(typeof(SpeakerResponse), StatusCodes.Status200OK)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
public async Task<IActionResult> GetSpeaker(Guid speakerId, CancellationToken cancellationToken)
{
    var query = new GetSpeakerByIdQuery(speakerId);
    var speaker = await Sender.Send(query, cancellationToken);
    return Ok(speaker);
}

[HttpPost]
[ProducesResponseType(typeof(Guid), StatusCodes.Status201Created)]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
public async Task<IActionResult> RegisterSpeaker(
    [FromBody] RegisterSpeakerCommand command,
    CancellationToken cancellationToken)
{
    var speakerId = await Sender.Send(command, cancellationToken);
    return CreatedAtAction(nameof(GetSpeaker), new { speakerId }, speakerId);
}
```

If it needs a new controller:

```csharp
// Presentation/Controllers/SpeakersController.cs
using Application.Speakers.Commands.RegisterSpeaker;
using Application.Speakers.Queries.GetSpeakerById;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.Controllers;

public sealed class SpeakersController : ApiController
{
    [HttpGet("{speakerId:guid}")]
    [ProducesResponseType(typeof(SpeakerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSpeaker(Guid speakerId, CancellationToken cancellationToken) =>
        Ok(await Sender.Send(new GetSpeakerByIdQuery(speakerId), cancellationToken));

    [HttpPost]
    [ProducesResponseType(typeof(Guid), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegisterSpeaker(
        [FromBody] RegisterSpeakerCommand command,
        CancellationToken cancellationToken)
    {
        var id = await Sender.Send(command, cancellationToken);
        return CreatedAtAction(nameof(GetSpeaker), new { speakerId = id }, id);
    }
}
```

### Response-code conventions

| Verb | Success | Failure modes |
|------|---------|---------------|
| GET (single) | 200 OK | 404 Not Found |
| GET (list) | 200 OK (empty list is still 200) | 400 if pagination invalid |
| POST (create) | 201 Created + Location header | 400 / 409 (conflict) |
| PUT / PATCH | 200 OK or 204 No Content | 400 / 404 |
| DELETE | 204 No Content | 404 |

Always include `[ProducesResponseType]` for every status code you might return.

### DI

Nothing. `AddApplicationPart(presentationAssembly)` in `Startup.cs:32` picks up new controllers automatically.

---

## Template 7 — New Application Service

Reusable, cross-aggregate capabilities. Example: `IDateTimeProvider`.

### Contract — `Application/Abstractions/IDateTimeProvider.cs`

```csharp
namespace Application.Abstractions;

public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
}
```

### Implementation — `Infrastructure/Services/SystemDateTimeProvider.cs`

```csharp
using Application.Abstractions;

namespace Infrastructure.Services;

internal sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}
```

### DI

```csharp
services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
```

Pick the lifetime intentionally:

| Lifetime | Use case |
|----------|----------|
| `Singleton` | Stateless utilities (date provider, password hasher, JSON serializer config) |
| `Scoped` | Anything depending on `DbContext`, current user / HttpContext, per-request caches |
| `Transient` | Genuinely transient state holders — rare in this codebase |

---

## Template 8 — New Integration (external HTTP API)

Example: SendGrid email sender.

### Contract — `Application/Abstractions/Integrations/IEmailSender.cs`

```csharp
namespace Application.Abstractions.Integrations;

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public sealed record EmailMessage(string To, string Subject, string HtmlBody);
```

### Implementation — `Infrastructure/Integrations/Email/SendGridEmailSender.cs`

```csharp
using Application.Abstractions.Integrations;
using System.Net.Http.Json;

namespace Infrastructure.Integrations.Email;

internal sealed class SendGridEmailSender : IEmailSender
{
    private readonly HttpClient _httpClient;

    public SendGridEmailSender(HttpClient httpClient) => _httpClient = httpClient;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var payload = new
        {
            personalizations = new[]
            {
                new { to = new[] { new { email = message.To } }, subject = message.Subject }
            },
            from = new { email = "noreply@example.com" },
            content = new[] { new { type = "text/html", value = message.HtmlBody } }
        };

        var response = await _httpClient.PostAsJsonAsync("v3/mail/send", payload, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
```

### DI

```csharp
services.AddHttpClient<IEmailSender, SendGridEmailSender>(client =>
{
    client.BaseAddress = new Uri(configuration["Integrations:SendGrid:BaseUrl"]!);
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", configuration["Integrations:SendGrid:ApiKey"]);
    client.Timeout = TimeSpan.FromSeconds(10);
});
// Recommended: add Polly retry + circuit-breaker policies (see File 06 for the package).
```

The API key comes from your secret store, not `appsettings.json`.

---

## Template 9 — New Background Job

> **Status:** the codebase has no background processing yet. The template below assumes you've added Hangfire (see File 04 §5).

### Contract — `Application/Abstractions/Jobs/ISendUpcomingWebinarRemindersJob.cs`

```csharp
namespace Application.Abstractions.Jobs;

public interface ISendUpcomingWebinarRemindersJob
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}
```

### Implementation — `Infrastructure/Jobs/SendUpcomingWebinarRemindersJob.cs`

```csharp
internal sealed class SendUpcomingWebinarRemindersJob : ISendUpcomingWebinarRemindersJob
{
    private readonly IWebinarReadRepository _reads;
    private readonly IEmailSender _email;
    private readonly ILogger<SendUpcomingWebinarRemindersJob> _logger;

    public SendUpcomingWebinarRemindersJob(
        IWebinarReadRepository reads,
        IEmailSender email,
        ILogger<SendUpcomingWebinarRemindersJob> logger)
    {
        _reads = reads;
        _email = email;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var upcoming = await _reads.ListStartingWithinAsync(TimeSpan.FromHours(24), cancellationToken);
        _logger.LogInformation("Sending reminders for {Count} upcoming webinars", upcoming.Count);

        foreach (var webinar in upcoming)
        {
            await _email.SendAsync(
                new EmailMessage(
                    To: webinar.OrganizerEmail,
                    Subject: $"Reminder: '{webinar.Name}' starts soon",
                    HtmlBody: $"<p>Your webinar starts at {webinar.ScheduledOn:u}.</p>"),
                cancellationToken);
        }
    }
}
```

### Registration & scheduling — `Web/Program.cs` (after the Hangfire bootstrap)

```csharp
services.AddScoped<ISendUpcomingWebinarRemindersJob, SendUpcomingWebinarRemindersJob>();

// after app.Build():
RecurringJob.AddOrUpdate<ISendUpcomingWebinarRemindersJob>(
    "send-upcoming-reminders",
    j => j.ExecuteAsync(CancellationToken.None),
    Cron.Hourly,
    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
```

For ad-hoc, fire-and-forget enqueuing from a handler:
```csharp
_backgroundJobClient.Enqueue<ISomeJob>(j => j.ExecuteAsync(arg, CancellationToken.None));
```

---

## Template 10 — New Validation

### Static rule

```csharp
public sealed class CancelWebinarCommandValidator : AbstractValidator<CancelWebinarCommand>
{
    public CancelWebinarCommandValidator()
    {
        RuleFor(x => x.WebinarId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}
```

### Async rule that hits the database

Requires the async validation fix from File 03 §17 (so validators run via `ValidateAsync`).

```csharp
public sealed class CreateWebinarCommandValidator : AbstractValidator<CreateWebinarCommand>
{
    public CreateWebinarCommandValidator(IWebinarRepository repo, IDateTimeProvider clock)
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(100)
            .MustAsync(async (name, ct) => !await repo.AnyByNameAsync(name, ct))
                .WithMessage("A webinar with that name already exists.");

        RuleFor(x => x.ScheduledOn)
            .GreaterThan(_ => clock.UtcNow)
            .WithMessage("Webinar must be scheduled in the future.");
    }
}
```

Validators are resolved per scope, so injecting `IWebinarRepository` and `IDateTimeProvider` is safe.

---

## Template 11 — New DTO

The codebase uses `sealed record` universally. Pick the suffix that matches role:

| Suffix | Role | Lives in |
|--------|------|----------|
| `…Request` | HTTP input | feature folder, next to its command |
| `…Response` | Query output (read model) | feature folder, next to its query |
| `…Dto` | Cross-feature shared DTO | new `Shared/` folder under the aggregate |

```csharp
public sealed record CancelWebinarRequest(string Reason);

public sealed record WebinarSummaryResponse(Guid Id, string Name, DateTime ScheduledOn);

public sealed record AddressDto(string Line1, string? Line2, string City, string Country);
```

If you need positional records with derived members, add them with body braces:

```csharp
public sealed record WebinarResponse(Guid Id, string Name, DateTime ScheduledOn)
{
    public bool IsInFuture => ScheduledOn > DateTime.UtcNow;
}
```

---

## Template 12 — New Mapping Profile (Mapster)

Today the codebase uses convention-based `Adapt<T>()` (`WebinarsController:48`). Once you have any non-trivial mapping, register explicit configs to get compile-time safety against renames.

### File: `Application/Mapping/MappingRegistration.cs` (new)

```csharp
using Application.Webinars.Commands.CreateWebinar;
using Application.Webinars.Queries.GetWebinarById;
using Domain.Entities;
using Mapster;
using MapsterMapper;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Mapping;

public static class MappingRegistration
{
    public static IServiceCollection AddMappings(this IServiceCollection services)
    {
        var config = TypeAdapterConfig.GlobalSettings;
        config.Scan(typeof(AssemblyReference).Assembly);

        services.AddSingleton(config);
        services.AddScoped<IMapper, ServiceMapper>();
        return services;
    }
}

public sealed class WebinarMappingConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<CreateWebinarRequest, CreateWebinarCommand>();
        config.NewConfig<Webinar, WebinarResponse>();
    }
}
```

### DI hook-up

```csharp
// In AddApplication():
services.AddMappings();
```

### Use in controllers

```csharp
public sealed class WebinarsController : ApiController
{
    private readonly IMapper _mapper;
    public WebinarsController(IMapper mapper) => _mapper = mapper;

    [HttpPost]
    public async Task<IActionResult> CreateWebinar(
        [FromBody] CreateWebinarRequest request, CancellationToken ct)
    {
        var command = _mapper.Map<CreateWebinarCommand>(request);
        var id = await Sender.Send(command, ct);
        return CreatedAtAction(nameof(GetWebinar), new { webinarId = id }, id);
    }
}
```

If the Request and Command share the same shape exactly, the simplest fix is to **delete the Request type entirely and bind the Command directly** — see File 03 §12.

---

## End-to-end walkthrough — adding `Speaker` to the system

Putting all the templates together. New requirement: "allow creating and looking up Speakers."

1. **Domain entity** (Template 2) — `Domain/Entities/Speaker.cs`.
2. **Repository interface** (Template 3) — `Domain/Abstractions/ISpeakerRepository.cs`.
3. **EF configuration** (Template 2) — `Infrastructure/Configurations/SpeakerConfiguration.cs`.
4. **Repository implementation** (Template 3) — `Infrastructure/Repositories/SpeakerRepository.cs`.
5. **DI** — `services.AddScoped<ISpeakerRepository, SpeakerRepository>();` (in `AddInfrastructure()` if you've extracted the extension methods).
6. **Migration** — `dotnet ef migrations add Add_Speakers_Table -p Infrastructure -s Web`.
7. **Domain exceptions** — `Domain/Exceptions/SpeakerNotFoundException.cs`, `SpeakerEmailAlreadyExistsException.cs`.
8. **Command** (Template 4) — `RegisterSpeakerCommand` + validator + handler.
9. **Query** (Template 5) — `GetSpeakerByIdQuery` + handler + response.
10. **Controller** (Template 6) — `Presentation/Controllers/SpeakersController.cs`.
11. **Tests** (not yet a convention, but should be) — `Domain.UnitTests/SpeakerTests.cs`, `Application.UnitTests/RegisterSpeakerCommandHandlerTests.cs`, `Infrastructure.IntegrationTests/SpeakerRepositoryTests.cs`.

Approximate effort: 1-2 hours for a single developer once these templates are internalized. The reason that's possible is precisely *because* the existing patterns are uniform — there's no novel scaffolding to invent.

---

## Quick reference card

| I need to add… | Templates | Files touched |
|----------------|-----------|---------------|
| A new aggregate | 1, 2, 3 | 4 new files + 1 DI line + 1 migration |
| A write use case | 4, 6 | 3-4 new files + 1 controller method |
| A read use case | 5, 6 | 3 new files + 1 controller method |
| A cross-aggregate service | 7 | 2 new files + 1 DI line |
| An external API integration | 8 | 2 new files + DI block |
| A scheduled job | 9 | 2 new files + DI + cron registration |
| A new validation rule | 10 | 1 new file (or edit existing validator) |
| A new DTO | 11 | 1 new file |
| A mapping rule | 12 | 1 new file (or extend `WebinarMappingConfig`) |
