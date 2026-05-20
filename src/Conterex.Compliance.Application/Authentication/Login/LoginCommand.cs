using Conterex.Compliance.Application.Abstractions.Messaging;

namespace Conterex.Compliance.Application.Authentication.Login;

public sealed record LoginCommand(string Email, string Password) : ICommand<LoginResponse>;
