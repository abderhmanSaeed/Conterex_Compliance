using Conterex.Compliance.Domain.Exceptions.Base;

namespace Conterex.Compliance.Domain.Exceptions;

/// <summary>
/// Thrown when a domain invariant on a <see cref="Conterex.Compliance.Domain.Entities.Webinar"/>
/// is violated (e.g. empty name, past schedule date, cancelling something already cancelled).
/// </summary>
public sealed class InvalidWebinarStateException : BadRequestException
{
    public InvalidWebinarStateException(string message) : base(message)
    {
    }
}
