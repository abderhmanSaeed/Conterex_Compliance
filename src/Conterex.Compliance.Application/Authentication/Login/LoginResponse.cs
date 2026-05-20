using System;

namespace Conterex.Compliance.Application.Authentication.Login;

public sealed record LoginResponse(string AccessToken, DateTime ExpiresAtUtc);
