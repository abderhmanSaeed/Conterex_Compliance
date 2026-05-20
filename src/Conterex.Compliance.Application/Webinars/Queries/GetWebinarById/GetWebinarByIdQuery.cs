using System;
using Conterex.Compliance.Application.Abstractions.Messaging;

namespace Conterex.Compliance.Application.Webinars.Queries.GetWebinarById;

public sealed record GetWebinarByIdQuery(Guid WebinarId) : IQuery<WebinarResponse>;