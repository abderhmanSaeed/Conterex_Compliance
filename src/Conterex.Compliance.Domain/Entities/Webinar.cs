using System;
using Conterex.Compliance.Domain.Abstractions;
using Conterex.Compliance.Domain.Enums;
using Conterex.Compliance.Domain.Events;
using Conterex.Compliance.Domain.Exceptions;
using Conterex.Compliance.Domain.Primitives;

namespace Conterex.Compliance.Domain.Entities;

public sealed class Webinar : AggregateRoot
{
    public const int NameMaxLength = 100;
    public const int CancellationReasonMaxLength = 500;

    private Webinar(Guid id, string name, DateTime scheduledOn) : base(id)
    {
        Name = name;
        ScheduledOn = scheduledOn;
        Status = WebinarStatus.Scheduled;
    }

    private Webinar()
    {
    }

    public string Name { get; private set; } = default!;

    public DateTime ScheduledOn { get; private set; }

    public WebinarStatus Status { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>
    /// Factory entrypoint enforcing all creation invariants. The constructor is
    /// private so callers cannot bypass these checks.
    /// </summary>
    public static Webinar Create(string name, DateTime scheduledOn, IDateTimeProvider clock)
    {
        if (clock is null)
        {
            throw new ArgumentNullException(nameof(clock));
        }

        var trimmedName = (name ?? string.Empty).Trim();

        if (trimmedName.Length == 0)
        {
            throw new InvalidWebinarStateException("Webinar name is required.");
        }

        if (trimmedName.Length > NameMaxLength)
        {
            throw new InvalidWebinarStateException(
                $"Webinar name cannot exceed {NameMaxLength} characters.");
        }

        if (scheduledOn <= clock.UtcNow)
        {
            throw new InvalidWebinarStateException("Webinar must be scheduled in the future.");
        }

        var webinar = new Webinar(Guid.NewGuid(), trimmedName, scheduledOn);

        webinar.RaiseDomainEvent(new WebinarCreatedDomainEvent(
            webinar.Id,
            webinar.Name,
            webinar.ScheduledOn));

        return webinar;
    }

    public void Reschedule(DateTime newScheduledOn, IDateTimeProvider clock)
    {
        if (clock is null)
        {
            throw new ArgumentNullException(nameof(clock));
        }

        if (Status != WebinarStatus.Scheduled)
        {
            throw new InvalidWebinarStateException(
                $"Cannot reschedule a webinar whose status is '{Status}'.");
        }

        if (newScheduledOn <= clock.UtcNow)
        {
            throw new InvalidWebinarStateException("New schedule date must be in the future.");
        }

        if (newScheduledOn == ScheduledOn)
        {
            return; // idempotent: no-op
        }

        var previous = ScheduledOn;
        ScheduledOn = newScheduledOn;

        RaiseDomainEvent(new WebinarRescheduledDomainEvent(Id, previous, newScheduledOn));
    }

    public void Cancel(string reason)
    {
        if (Status == WebinarStatus.Cancelled)
        {
            return; // idempotent: already cancelled
        }

        if (Status == WebinarStatus.Completed)
        {
            throw new InvalidWebinarStateException("A completed webinar cannot be cancelled.");
        }

        var trimmedReason = (reason ?? string.Empty).Trim();

        if (trimmedReason.Length == 0)
        {
            throw new InvalidWebinarStateException("Cancellation reason is required.");
        }

        if (trimmedReason.Length > CancellationReasonMaxLength)
        {
            throw new InvalidWebinarStateException(
                $"Cancellation reason cannot exceed {CancellationReasonMaxLength} characters.");
        }

        Status = WebinarStatus.Cancelled;
        CancellationReason = trimmedReason;

        RaiseDomainEvent(new WebinarCancelledDomainEvent(Id, trimmedReason));
    }
}
