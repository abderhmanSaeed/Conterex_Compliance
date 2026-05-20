using System;
using Conterex.Compliance.Domain.Primitives;

namespace Conterex.Compliance.Domain.Events;

public sealed record WebinarCreatedDomainEvent(
    Guid WebinarId,
    string Name,
    DateTime ScheduledOn) : IDomainEvent;
