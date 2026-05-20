using System;

namespace Conterex.Compliance.Domain.Primitives;

public abstract class Entity
{
    protected Entity(Guid id) => Id = id;

    protected Entity()
    {
    }

    public Guid Id { get; protected set; }
}