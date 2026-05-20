using System;

namespace Conterex.Compliance.Application.Abstractions.Authentication;

public sealed record AccessToken(string Token, DateTime ExpiresAtUtc);
