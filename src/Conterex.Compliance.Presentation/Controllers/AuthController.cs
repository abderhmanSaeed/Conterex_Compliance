using System.Threading;
using System.Threading.Tasks;
using Conterex.Compliance.Application.Authentication.Login;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Conterex.Compliance.Presentation.Controllers;

/// <summary>
/// Authentication endpoints. The login endpoint here is DEV-ONLY; it consults
/// the stubbed <c>IUserStore</c> (single hardcoded user from configuration).
/// Replace with a real identity module before any deployment outside local dev.
/// </summary>
[AllowAnonymous]
[Route("api/auth")]
public sealed class AuthController : ApiController
{
    /// <summary>
    /// Issues a JWT bearer access token for a valid email/password combination.
    /// </summary>
    /// <param name="command">Email + password.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An access token plus its UTC expiry.</returns>
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Login(
        [FromBody] LoginCommand command,
        CancellationToken cancellationToken)
    {
        var response = await Sender.Send(command, cancellationToken);
        return Ok(response);
    }
}
