using Conterex.Compliance.Domain.Exceptions.Base;

namespace Conterex.Compliance.Application.Exceptions;

/// <summary>
/// Raised when login credentials cannot be matched to any known user.
/// Mapped to <c>401 Unauthorized</c> by the exception middleware via a small
/// addition that recognises this dedicated type.
/// </summary>
public sealed class InvalidCredentialsException : BadRequestException
{
    public InvalidCredentialsException()
        : base("Invalid email or password.")
    {
    }
}
