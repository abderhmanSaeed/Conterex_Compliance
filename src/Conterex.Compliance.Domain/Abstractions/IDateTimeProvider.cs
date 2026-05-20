using System;

namespace Conterex.Compliance.Domain.Abstractions;

/// <summary>
/// Abstraction over the system clock so domain rules that depend on "now"
/// (e.g. <c>scheduledOn &gt; UtcNow</c>) remain deterministic and testable.
/// </summary>
public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
}
