using System;
using System.Collections.Generic;

namespace Conterex.Compliance.Domain.Primitives;

/// <summary>
/// Base class for aggregate roots. Tracks raised domain events until they are
/// collected and dispatched (typically inside <c>SaveChangesAsync</c>).
/// </summary>
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = new();

    protected AggregateRoot(Guid id) : base(id)
    {
    }

    protected AggregateRoot()
    {
    }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void RaiseDomainEvent(IDomainEvent @event) => _domainEvents.Add(@event);
}
