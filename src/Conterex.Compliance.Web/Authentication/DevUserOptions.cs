using System.ComponentModel.DataAnnotations;

namespace Conterex.Compliance.Web.Authentication;

/// <summary>
/// DEV-ONLY hardcoded user surfaced via the stubbed <c>IUserStore</c>. Replace
/// with a real identity module backed by a user database before any deployment
/// outside of local development.
/// </summary>
public sealed class DevUserOptions
{
    public const string SectionName = "Dev";

    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}
