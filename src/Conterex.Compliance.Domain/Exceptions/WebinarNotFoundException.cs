using System;
using Conterex.Compliance.Domain.Exceptions.Base;

namespace Conterex.Compliance.Domain.Exceptions;

public sealed class WebinarNotFoundException : NotFoundException
{
    public WebinarNotFoundException(Guid webinarId)
        : base($"The webinar with the identifier {webinarId} was not found.")
    {
    }
}