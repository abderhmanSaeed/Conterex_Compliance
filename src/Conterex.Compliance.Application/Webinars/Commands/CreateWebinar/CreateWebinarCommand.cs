using System;
using Conterex.Compliance.Application.Abstractions.Messaging;

namespace Conterex.Compliance.Application.Webinars.Commands.CreateWebinar;

public sealed record CreateWebinarCommand(string Name, DateTime ScheduledOn) : ICommand<Guid>;