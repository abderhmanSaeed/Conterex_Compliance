using System;
using System.Collections.Generic;

namespace Conterex.Compliance.Application.Abstractions.Authentication;

/// <summary>
/// The minimum identity surface the authentication subsystem needs to issue a token.
/// Deliberately small so a real identity module can replace the source without
/// touching consumers.
/// </summary>
public sealed record UserCredentials(
    Guid UserId,
    string Email,
    IReadOnlyList<string> Roles);
