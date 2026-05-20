# 03 — Domain Model Refactor (anemic → rich)

> **Status:** ✅ Completed. **Critical Issue #3** from the audit.

## Why the old implementation was dangerous

```csharp
// Domain/Entities/Webinar.cs (BEFORE — 22 lines, zero behavior)
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

- The public constructor accepted `null`, empty strings, and past dates without complaint.
- Business rules ("must be in the future", "name max 100 chars") lived in the validator and the EF configuration — duplicated, drifting independently.
- No way for the aggregate to **emit events** when something happened.
- Future operations (`Cancel`, `Reschedule`, `Complete`) would all be invented inside command handlers, scattering the domain's behavior across the Application layer.

## What changed

### `Domain/Primitives/IDomainEvent.cs` — pure marker

```csharp
namespace src/Conterex.Compliance.Domain.Primitives;

public interface IDomainEvent { }
```

**Zero NuGet dependencies.** The Domain layer remains framework-free. MediatR's `INotification` is *not* used here — the wrapping happens in the Application layer (see `DomainEventNotification`).

### `Domain/Primitives/AggregateRoot.cs` — event collection

```csharp
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = new();

    protected AggregateRoot(Guid id) : base(id) { }
    protected AggregateRoot() { }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void RaiseDomainEvent(IDomainEvent @event) => _domainEvents.Add(@event);
}
```

`Entity` (the existing base) is untouched. `AggregateRoot` extends it with the events list. Only aggregate roots emit events — entities inside an aggregate do not.

### `Domain/Enums/WebinarStatus.cs`

```csharp
public enum WebinarStatus { Scheduled = 0, Cancelled = 1, Completed = 2 }
```

Persisted as a `varchar(20)` (not the underlying int) so DB queries are human-readable — `HasConversion<string>()` in the EF configuration.

### `Domain/Events/*` — three concrete events

```csharp
public sealed record WebinarCreatedDomainEvent(Guid WebinarId, string Name, DateTime ScheduledOn) : IDomainEvent;
public sealed record WebinarRescheduledDomainEvent(Guid WebinarId, DateTime PreviousScheduledOn, DateTime NewScheduledOn) : IDomainEvent;
public sealed record WebinarCancelledDomainEvent(Guid WebinarId, string Reason) : IDomainEvent;
```

### `Domain/Abstractions/IDateTimeProvider.cs` — clock abstraction

```csharp
public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
}
```

Lives in **Domain** (not Application) because the domain itself needs "now" for its invariants. `SystemDateTimeProvider` implements this in Infrastructure and is registered as a singleton.

### `Domain/Exceptions/InvalidWebinarStateException.cs`

```csharp
public sealed class InvalidWebinarStateException : BadRequestException
{
    public InvalidWebinarStateException(string message) : base(message) { }
}
```

Single exception type for all Webinar invariant violations. The message encodes the specific rule that failed. This is mapped to **400 Bad Request** by the existing `ExceptionHandlingMiddleware` (no changes needed there).

### `Domain/Entities/Webinar.cs` — refactored

```csharp
public sealed class Webinar : AggregateRoot
{
    public const int NameMaxLength = 100;
    public const int CancellationReasonMaxLength = 500;

    private Webinar(Guid id, string name, DateTime scheduledOn) : base(id) { /* ... */ }
    private Webinar() { } // EF

    public string Name { get; private set; } = default!;
    public DateTime ScheduledOn { get; private set; }
    public WebinarStatus Status { get; private set; }
    public string? CancellationReason { get; private set; }

    public static Webinar Create(string name, DateTime scheduledOn, IDateTimeProvider clock)
    {
        // ... trim, validate name (empty? > 100 chars?), validate scheduledOn (future?) ...
        var webinar = new Webinar(Guid.NewGuid(), trimmedName, scheduledOn);
        webinar.RaiseDomainEvent(new WebinarCreatedDomainEvent(...));
        return webinar;
    }

    public void Reschedule(DateTime newScheduledOn, IDateTimeProvider clock)
    {
        // ... must still be Scheduled, must be future, idempotent if unchanged ...
        RaiseDomainEvent(new WebinarRescheduledDomainEvent(...));
    }

    public void Cancel(string reason)
    {
        // ... idempotent if already cancelled, refuse if completed, validate reason ...
        RaiseDomainEvent(new WebinarCancelledDomainEvent(...));
    }
}
```

Key changes:
- **`public` constructor → `private` constructor.** Outside callers must use `Create`. There is no way to bypass invariants.
- **All invariants are now expressed in code that lives with the data they protect.** `NameMaxLength` is a constant on the entity and is reused by both the EF configuration (`HasMaxLength(Webinar.NameMaxLength)`) and the validator (`MaximumLength(Webinar.NameMaxLength)`).
- **Trimming applied once, at the boundary.** `name.Trim()` happens in `Create`, not in three different places.
- **Idempotent behaviour where it makes sense.** `Cancel` of an already-cancelled webinar is a no-op; same for `Reschedule` to the same date.

### `Application/Events/DomainEventNotification.cs` — MediatR adapter

```csharp
public sealed record DomainEventNotification<TDomainEvent>(TDomainEvent DomainEvent) : INotification
    where TDomainEvent : IDomainEvent;
```

This is the only place where MediatR meets `IDomainEvent`. Future handlers subscribe like this:

```csharp
public sealed class SendWebinarCreatedNotificationHandler
    : INotificationHandler<DomainEventNotification<WebinarCreatedDomainEvent>>
{
    public Task Handle(DomainEventNotification<WebinarCreatedDomainEvent> notification, CancellationToken ct)
    {
        var @event = notification.DomainEvent;
        // ... do something ...
        return Task.CompletedTask;
    }
}
```

### `Infrastructure/ApplicationDbContext.cs` — overridden SaveChangesAsync

```csharp
public sealed class ApplicationDbContext : DbContext, IUnitOfWork
{
    private readonly IPublisher _publisher;

    public ApplicationDbContext(DbContextOptions options, IPublisher publisher) : base(options)
        => _publisher = publisher;

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var domainEvents = ChangeTracker.Entries<AggregateRoot>()
            .Select(entry => entry.Entity)
            .SelectMany(aggregate =>
            {
                var events = aggregate.DomainEvents.ToList();
                aggregate.ClearDomainEvents();
                return events;
            })
            .ToList();

        var result = await base.SaveChangesAsync(cancellationToken);

        foreach (var domainEvent in domainEvents)
        {
            var notificationType = typeof(DomainEventNotification<>).MakeGenericType(domainEvent.GetType());
            var notification = (INotification)Activator.CreateInstance(notificationType, domainEvent)!;
            await _publisher.Publish(notification, cancellationToken);
        }

        return result;
    }
}
```

Important property: **events are published only after the DB commit succeeds.** If `base.SaveChangesAsync` throws, the events are never dispatched. This rules out the "side effect fired but data not saved" failure mode.

### `Infrastructure/Configurations/WebinarConfiguration.cs` — updated

```csharp
builder.Property(webinar => webinar.Name)
    .HasMaxLength(Webinar.NameMaxLength)
    .IsRequired();

builder.Property(webinar => webinar.Status)
    .HasConversion<string>()
    .HasMaxLength(20)
    .IsRequired();

builder.Property(webinar => webinar.CancellationReason)
    .HasMaxLength(Webinar.CancellationReasonMaxLength);

builder.Ignore(webinar => webinar.DomainEvents);
```

`builder.Ignore(...DomainEvents)` is essential — without it, EF would scream about not knowing how to map an `IReadOnlyCollection<IDomainEvent>`.

### `Application/Webinars/Commands/CreateWebinar/CreateWebinarCommandHandler.cs` — uses factory

```csharp
public async Task<Guid> Handle(CreateWebinarCommand request, CancellationToken cancellationToken)
{
    var webinar = Webinar.Create(request.Name, request.ScheduledOn, _dateTimeProvider);
    _webinarRepository.Insert(webinar);
    await _unitOfWork.SaveChangesAsync(cancellationToken);
    return webinar.Id;
}
```

The handler is now a thin coordinator. Business rules live in `Webinar.Create`.

### `Application/Webinars/Commands/CreateWebinar/CreateWebinarCommandValidator.cs` — tightened

```csharp
public CreateWebinarCommandValidator(IDateTimeProvider dateTimeProvider)
{
    RuleFor(x => x.Name)
        .NotEmpty().WithMessage("Webinar name is required.")
        .MaximumLength(Webinar.NameMaxLength)
            .WithMessage($"Webinar name cannot exceed {Webinar.NameMaxLength} characters.");

    RuleFor(x => x.ScheduledOn)
        .NotEmpty().WithMessage("Webinar schedule date is required.")
        .Must(scheduledOn => scheduledOn > dateTimeProvider.UtcNow)
            .WithMessage("Webinar must be scheduled in the future.");
}
```

The validator catches bad input **before** the handler runs — fast 400 response with structured errors. The domain factory catches the same invariants again as a defence-in-depth check — they cannot be bypassed by alternate call paths.

## Architecture invariants preserved

- **Domain has zero NuGet dependencies.** Verified by re-reading `src/Conterex.Compliance.Domain.csproj`.
- **Application depends only on Domain.** Verified by `src/Conterex.Compliance.Application.csproj` (`MediatR`, `FluentValidation`, `Mapster`, `Dapper`, + ProjectReference to Domain).
- **Infrastructure depends on Domain + Application.** This is a deliberate widening introduced by this refactor. Infrastructure now references Application so it can use the `DomainEventNotification<>` wrapper. This matches the canonical Clean Architecture variant and is documented in the audit's File 06 §5 as recommended.

## Files modified / created

| Change | File |
|--------|------|
| NEW | `src/Conterex.Compliance.Domain/Primitives/AggregateRoot.cs` |
| NEW | `src/Conterex.Compliance.Domain/Primitives/IDomainEvent.cs` |
| NEW | `src/Conterex.Compliance.Domain/Abstractions/IDateTimeProvider.cs` |
| NEW | `src/Conterex.Compliance.Domain/Enums/WebinarStatus.cs` |
| NEW | `src/Conterex.Compliance.Domain/Events/WebinarCreatedDomainEvent.cs` |
| NEW | `src/Conterex.Compliance.Domain/Events/WebinarRescheduledDomainEvent.cs` |
| NEW | `src/Conterex.Compliance.Domain/Events/WebinarCancelledDomainEvent.cs` |
| NEW | `src/Conterex.Compliance.Domain/Exceptions/InvalidWebinarStateException.cs` |
| NEW | `src/Conterex.Compliance.Application/Events/DomainEventNotification.cs` |
| NEW | `src/Conterex.Compliance.Infrastructure/Services/SystemDateTimeProvider.cs` |
| MODIFIED | `src/Conterex.Compliance.Domain/Entities/Webinar.cs` (factory + behavior + events) |
| MODIFIED | `src/Conterex.Compliance.Domain/Conterex.Compliance.Domain.csproj` (nullable enable) |
| MODIFIED | `src/Conterex.Compliance.Application/Conterex.Compliance.Application.csproj` (nullable enable) |
| MODIFIED | `src/Conterex.Compliance.Application/Webinars/Commands/CreateWebinar/CreateWebinarCommandHandler.cs` (use factory) |
| MODIFIED | `src/Conterex.Compliance.Application/Webinars/Commands/CreateWebinar/CreateWebinarCommandValidator.cs` (tightened rules) |
| MODIFIED | `src/Conterex.Compliance.Infrastructure/ApplicationDbContext.cs` (dispatch events after SaveChanges) |
| MODIFIED | `src/Conterex.Compliance.Infrastructure/Configurations/WebinarConfiguration.cs` (Status, CancellationReason, Ignore events) |
| MODIFIED | `src/Conterex.Compliance.Infrastructure/Conterex.Compliance.Infrastructure.csproj` (added MediatR + Application ProjectReference) |
| MODIFIED | `src/Conterex.Compliance.Web/Startup.cs` (registered `IDateTimeProvider`) |
| NEW | `src/Conterex.Compliance.Infrastructure/Migrations/20260520073233_Enrich_Webinar_With_Status.cs` (adds Status + CancellationReason columns) |

## Validation steps

- ✅ `dotnet build` succeeds with no errors.
- ✅ The new migration was generated by `dotnet ef migrations add Enrich_Webinar_With_Status` and emits `AddColumn` operations for `Status` (`varchar(20)` not null) and `CancellationReason` (`varchar(500)` nullable), plus a minor alter on `Name` to make it non-nullable.
- ✅ Calling `new Webinar(...)` from outside the assembly is now a compile error (the constructor is `private`).
- ✅ `Webinar.Create("", DateTime.UtcNow.AddDays(1), clock)` throws `InvalidWebinarStateException("Webinar name is required.")`.
- ✅ `Webinar.Create(new string('a', 200), ..., clock)` throws `InvalidWebinarStateException` for length.
- ✅ `Webinar.Create("ok", DateTime.UtcNow.AddMinutes(-1), clock)` throws `InvalidWebinarStateException` for past schedule.
- ✅ A created webinar has exactly one `WebinarCreatedDomainEvent` in `DomainEvents` until `SaveChangesAsync` collects + clears it.
- ✅ The validator's max-length and future-date rules match the entity's invariants.

## How to add a new domain event (3 steps)

1. **Define the event** as a `sealed record` implementing `IDomainEvent` in `src/Conterex.Compliance.Domain/Events/`.
2. **Raise it** from the aggregate method that produces it:
   ```csharp
   public void DoSomething(...) {
       // ... mutate state ...
       RaiseDomainEvent(new SomethingHappenedDomainEvent(Id, ...));
   }
   ```
3. **Handle it** (optional) by creating an `INotificationHandler<DomainEventNotification<SomethingHappenedDomainEvent>>` in the Application layer. MediatR's assembly scan registers it automatically.

## Security impact

Indirect but real: invariants now live in the domain so command handlers cannot accidentally skip them. A future careless handler that does `new Webinar(...)` would not compile (the constructor is private), eliminating one whole class of "I forgot the validation" bugs.

## Production impact

- The new migration **alters the existing `ScheduledOn` column from `timestamp without time zone` to `timestamp with time zone`**. This is the Npgsql 6 + EF Core 6 default for DateTime properties without `Kind=Unspecified`. For an existing populated database this is a non-trivial migration — the migration is safe to run on PostgreSQL but values will be interpreted as UTC. Review before applying to a database that contains real customer data.
- The migration also makes `Name` non-nullable (default `""` for any existing NULL rows).
- New columns `Status` (varchar(20)) and `CancellationReason` (varchar(500), nullable) are added — pure additive.

## Not addressed in this phase (deferred)

- The `Reschedule` and `Cancel` command/endpoint exposures are intentionally **not** wired through the API yet. The domain methods exist on the aggregate; future use-case work can add the corresponding `RescheduleWebinarCommand` / `CancelWebinarCommand` in 30 minutes using the existing `CreateWebinar` feature as a template (see audit File 05).
- No `INotificationHandler` is shipped for the new domain events — they're raised, dispatched, and currently fall on the floor (which is correct — there are no consumers yet). When the system needs to react to "webinar created" with an email, projection write, or outbox entry, add the handler then.
- The Outbox pattern for guaranteed delivery of cross-context integration events is a separate concern. See audit File 04 §5.
