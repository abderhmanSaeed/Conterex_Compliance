using System;
using Conterex.Compliance.Domain.Primitives;

namespace Conterex.Compliance.Domain.Events;

public sealed record WebinarCancelledDomainEvent(
    Guid WebinarId,
    string Reason) : IDomainEvent;
