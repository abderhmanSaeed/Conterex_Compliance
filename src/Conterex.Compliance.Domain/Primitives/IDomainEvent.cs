namespace Conterex.Compliance.Domain.Primitives;

/// <summary>
/// Marker interface for domain events. Kept dependency-free so the Domain layer
/// retains zero NuGet references. The Application layer wraps these in a
/// MediatR <c>INotification</c> adapter for dispatch.
/// </summary>
public interface IDomainEvent
{
}
