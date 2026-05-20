using System.Threading;
using System.Threading.Tasks;

namespace Conterex.Compliance.Application.Abstractions.Authentication;

/// <summary>
/// Minimal user lookup contract used by the login flow. The Foundation Hardening
/// phase ships a DEV-only implementation; a future identity module is expected
/// to provide a real, persisted implementation behind this same interface.
/// </summary>
public interface IUserStore
{
    Task<UserCredentials?> FindAsync(string email, string password, CancellationToken cancellationToken);
}
