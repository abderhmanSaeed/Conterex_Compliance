using System;
using Conterex.Compliance.Domain.Primitives;

namespace Conterex.Compliance.Domain.Events;

public sealed record WebinarRescheduledDomainEvent(
    Guid WebinarId,
    DateTime PreviousScheduledOn,
    DateTime NewScheduledOn) : IDomainEvent;
