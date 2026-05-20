using System;
using System.Threading;
using System.Threading.Tasks;
using Conterex.Compliance.Application.Webinars.Commands.CreateWebinar;
using Conterex.Compliance.Application.Webinars.Queries.GetWebinarById;
using Mapster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Conterex.Compliance.Presentation.Controllers;

/// <summary>
/// Represents the webinars controller. The controller-level <c>[Authorize]</c>
/// attribute protects every endpoint by default; individual actions opt out
/// via <c>[AllowAnonymous]</c>.
/// </summary>
[Authorize]
public sealed class WebinarsController : ApiController
{
    /// <summary>
    /// Gets the webinar with the specified identifier, if it exists.
    /// Anonymous access is permitted on this read endpoint as a worked example
    /// of mixing protected and public endpoints in a single controller.
    /// </summary>
    /// <param name="webinarId">The webinar identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The webinar with the specified identifier, if it exists.</returns>
    [AllowAnonymous]
    [HttpGet("{webinarId:guid}")]
    [ProducesResponseType(typeof(WebinarResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWebinar(Guid webinarId, CancellationToken cancellationToken)
    {
        var query = new GetWebinarByIdQuery(webinarId);

        var webinar = await Sender.Send(query, cancellationToken);

        return Ok(webinar);
    }

    /// <summary>
    /// Creates a new webinar based on the specified request. Requires a valid JWT.
    /// </summary>
    /// <param name="request">The create webinar request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The identifier of the newly created webinar.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(Guid), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateWebinar(
        [FromBody] CreateWebinarRequest request,
        CancellationToken cancellationToken)
    {
        var command = request.Adapt<CreateWebinarCommand>();

        var webinarId = await Sender.Send(command, cancellationToken);

        return CreatedAtAction(nameof(GetWebinar), new { webinarId }, webinarId);
    }
}
