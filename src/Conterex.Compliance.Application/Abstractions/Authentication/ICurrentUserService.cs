using System;

namespace Conterex.Compliance.Application.Abstractions.Authentication;

/// <summary>
/// Read-only view of the authenticated principal for the current request.
/// Returns nulls when the caller is anonymous (e.g. background jobs or
/// public endpoints).
/// </summary>
public interface ICurrentUserService
{
    Guid? UserId { get; }
    string? Email { get; }
    bool IsAuthenticated { get; }
}
