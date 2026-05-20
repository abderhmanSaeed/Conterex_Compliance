using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Conterex.Compliance.Application.Abstractions.Authentication;
using Microsoft.Extensions.Options;

namespace Conterex.Compliance.Web.Authentication;

/// <summary>
/// DEV-ONLY <see cref="IUserStore"/> implementation. Accepts exactly ONE user whose
/// credentials are read from configuration (typically supplied via user-secrets
/// or environment variables). Replace with a database-backed implementation before
/// using outside local development.
/// </summary>
public sealed class DevUserStore : IUserStore
{
    private static readonly Guid DevUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly IReadOnlyList<string> DevRoles = new[] { "Admin" };

    private readonly DevUserOptions _options;

    public DevUserStore(IOptions<DevUserOptions> options)
    {
        _options = options.Value;
    }

    public Task<UserCredentials?> FindAsync(string email, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Email) || string.IsNullOrWhiteSpace(_options.Password))
        {
            // No dev user configured = nobody can log in. Production safety net.
            return Task.FromResult<UserCredentials?>(null);
        }

        var emailMatch = string.Equals(email, _options.Email, StringComparison.OrdinalIgnoreCase);
        var passwordMatch = string.Equals(password, _options.Password, StringComparison.Ordinal);

        if (!emailMatch || !passwordMatch)
        {
            return Task.FromResult<UserCredentials?>(null);
        }

        var credentials = new UserCredentials(DevUserId, _options.Email, DevRoles);
        return Task.FromResult<UserCredentials?>(credentials);
    }
}
