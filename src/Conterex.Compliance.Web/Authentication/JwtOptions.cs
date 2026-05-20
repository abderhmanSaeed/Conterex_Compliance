using System.ComponentModel.DataAnnotations;

namespace Conterex.Compliance.Web.Authentication;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required(AllowEmptyStrings = false)]
    public string Issuer { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Symmetric signing key (HS256). MUST be at least 32 characters of high-entropy
    /// random data. NEVER commit a real value — supply via user-secrets or environment.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(32, ErrorMessage = "Jwt:SigningKey must be at least 32 characters.")]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 1440, ErrorMessage = "Jwt:AccessTokenLifetimeMinutes must be between 1 and 1440 minutes.")]
    public int AccessTokenLifetimeMinutes { get; set; } = 60;
}
