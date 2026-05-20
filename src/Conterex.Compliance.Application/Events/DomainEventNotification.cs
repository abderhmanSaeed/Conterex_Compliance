using Conterex.Compliance.Domain.Primitives;
using MediatR;

namespace Conterex.Compliance.Application.Events;

/// <summary>
/// MediatR adapter that wraps a pure-domain <see cref="IDomainEvent"/> so it can
/// flow through the MediatR notification pipeline. The Domain project itself
/// remains MediatR-free.
/// </summary>
public sealed record DomainEventNotification<TDomainEvent>(TDomainEvent DomainEvent) : INotification
    where TDomainEvent : IDomainEvent;
