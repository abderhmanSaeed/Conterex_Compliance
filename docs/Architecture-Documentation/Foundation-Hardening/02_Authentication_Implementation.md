# 02 — Authentication Implementation

> **Status:** ✅ Completed. **Critical Issue #2** from the audit.

## Why the old implementation was dangerous

```csharp
// Web/Startup.cs:85 (BEFORE)
app.UseAuthorization();
```

`UseAuthorization()` was in the pipeline. No `AddAuthentication`, no `AddAuthorization`, no `[Authorize]` attribute anywhere. Every endpoint was anonymous. The middleware advertised a security posture the application did not have. New contributors reading the file came away believing the API was protected when it wasn't.

## What changed

### Application layer — abstractions

All authentication contracts live in `src/Conterex.Compliance.Application/Abstractions/Authentication/`:

| Contract | Purpose |
|----------|---------|
| `UserCredentials` | Record carrying `UserId`, `Email`, `Roles`. The minimum identity surface required to mint a token. |
| `AccessToken` | Record carrying `Token` + `ExpiresAtUtc`. |
| `IJwtTokenGenerator` | `Generate(UserCredentials) → AccessToken`. |
| `ICurrentUserService` | Read-only view of the authenticated principal for the current HTTP request. Returns nulls when anonymous. |
| `IUserStore` | `FindAsync(email, password, ct) → UserCredentials?`. Deliberately minimal — a real identity module will replace the implementation later. |

### Application layer — login use case

`src/Conterex.Compliance.Application/Authentication/Login/`:

```csharp
public sealed record LoginCommand(string Email, string Password) : ICommand<LoginResponse>;

public sealed record LoginResponse(string AccessToken, DateTime ExpiresAtUtc);

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
    }
}

internal sealed class LoginCommandHandler : ICommandHandler<LoginCommand, LoginResponse>
{
    public async Task<LoginResponse> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var credentials = await _userStore.FindAsync(request.Email, request.Password, cancellationToken)
            ?? throw new InvalidCredentialsException();
        var token = _tokenGenerator.Generate(credentials);
        return new LoginResponse(token.Token, token.ExpiresAtUtc);
    }
}
```

The handler is **identical in shape** to `CreateWebinarCommandHandler` — `internal sealed`, constructor-injected dependencies, single `Handle` method.

### Web layer — concrete services

`src/Conterex.Compliance.Web/Authentication/`:

| File | Role |
|------|------|
| `JwtOptions.cs` | Strongly-typed options bound to the `Jwt` configuration section. Annotated with `[Required]`, `[MinLength(32)]`, `[Range]`. |
| `DevUserOptions.cs` | Strongly-typed options bound to the `Dev` section. Carries the hardcoded dev user's email/password. |
| `JwtTokenGenerator.cs` | HS256 token signing via `JwtSecurityTokenHandler`. Emits `sub` / `email` / `jti` / `iat` claims plus `role` claims for each role. |
| `CurrentUserService.cs` | Reads claims from `IHttpContextAccessor.HttpContext.User`. Handles anonymous-principal nulls safely. |
| `DevUserStore.cs` | DEV-ONLY `IUserStore`. Accepts exactly one user whose credentials are loaded from configuration. Returns null when configuration is empty (production safety net). |
| `AuthenticationServiceCollectionExtensions.cs` | `services.AddConterexAuthentication(IConfiguration)` extension that registers everything. |

### `AuthenticationServiceCollectionExtensions.AddConterexAuthentication`

```csharp
public static IServiceCollection AddConterexAuthentication(
    this IServiceCollection services, IConfiguration configuration)
{
    services.AddOptions<JwtOptions>()
        .Bind(configuration.GetSection(JwtOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    services.AddOptions<DevUserOptions>()
        .Bind(configuration.GetSection(DevUserOptions.SectionName));

    services.AddHttpContextAccessor();
    services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
    services.AddScoped<ICurrentUserService, CurrentUserService>();
    services.AddSingleton<IUserStore, DevUserStore>();

    var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
        ?? throw new InvalidOperationException("Missing required 'Jwt' configuration section.");

    if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
    {
        throw new InvalidOperationException(
            "Jwt:SigningKey is missing or shorter than 32 characters.");
    }

    services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ClockSkew = TimeSpan.FromSeconds(30)
            };
        });

    services.AddAuthorization();
    return services;
}
```

Two layers of validation:
1. `ValidateOnStart()` triggers DataAnnotations validation at boot — `[Required]` + `[MinLength(32)]` catches missing or weak keys.
2. The explicit guard above runs **before** DI continues, producing a more helpful exception message.

### `Startup.cs` — composition

```csharp
public void ConfigureServices(IServiceCollection services)
{
    // ... existing wiring ...
    services.AddTransient<ExceptionHandlingMiddleware>();
    services.AddConterexAuthentication(Configuration);
}

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    // ...
    app.UseRouting();
    app.UseAuthentication();   // NEW — must be before UseAuthorization
    app.UseAuthorization();
    app.UseEndpoints(endpoints => endpoints.MapControllers());
}
```

### `Presentation/Controllers/AuthController.cs` — login endpoint

```csharp
[AllowAnonymous]
[Route("api/auth")]
public sealed class AuthController : ApiController
{
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Login(
        [FromBody] LoginCommand command,
        CancellationToken cancellationToken)
    {
        var response = await Sender.Send(command, cancellationToken);
        return Ok(response);
    }
}
```

The controller is **3 lines of body code**. All real work happens in `LoginCommandHandler`. The `[AllowAnonymous]` opt-out is explicit.

### `Presentation/Controllers/WebinarsController.cs` — protected by default

**Before:**
```csharp
public sealed class WebinarsController : ApiController
{
    [HttpGet("{webinarId:guid}")]
    public async Task<IActionResult> GetWebinar(...) { ... }

    [HttpPost]
    public async Task<IActionResult> CreateWebinar(...) { ... }
}
```

**After:**
```csharp
[Authorize]                                     // controller-level: protect by default
public sealed class WebinarsController : ApiController
{
    [AllowAnonymous]                             // public exception
    [HttpGet("{webinarId:guid}")]
    public async Task<IActionResult> GetWebinar(...) { ... }

    [HttpPost]
    [ProducesResponseType(typeof(Guid), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateWebinar(...) { ... }
}
```

This pattern — `[Authorize]` on the class, `[AllowAnonymous]` on specific actions that genuinely should be public — is the safer default. Adding a new endpoint inherits authentication automatically; you have to actively opt out.

## Request flow

```
1. POST /api/auth/login {"email":"<dev>","password":"<dev>"}
       ↓
2. UseAuthentication runs the JWT bearer handler — no token present, but [AllowAnonymous] lets the request through.
       ↓
3. ValidationBehavior validates the LoginCommand. Empty / malformed email → 400 with structured errors.
       ↓
4. LoginCommandHandler calls IUserStore.FindAsync — DEV impl checks against Dev:Email + Dev:Password.
       ↓
5. On success: IJwtTokenGenerator.Generate produces an HS256-signed token.
       ↓
6. Response: 200 OK { "accessToken": "<jwt>", "expiresAtUtc": "..." }


7. POST /api/webinars (with Authorization: Bearer <jwt>) {"name":"...","scheduledOn":"..."}
       ↓
8. UseAuthentication validates the token (issuer, audience, expiry, signature). HttpContext.User populated.
       ↓
9. UseAuthorization checks [Authorize] — succeeds because the principal is authenticated.
       ↓
10. ValidationBehavior + CreateWebinarCommandHandler proceed as before.

       
11. POST /api/webinars WITHOUT a bearer
       ↓
12. UseAuthentication leaves HttpContext.User unauthenticated.
       ↓
13. UseAuthorization fails the [Authorize] check → 401 Unauthorized (no body, no handler invocation).
```

## Files modified / created

| Change | File |
|--------|------|
| NEW | `src/Conterex.Compliance.Application/Abstractions/Authentication/UserCredentials.cs` |
| NEW | `src/Conterex.Compliance.Application/Abstractions/Authentication/AccessToken.cs` |
| NEW | `src/Conterex.Compliance.Application/Abstractions/Authentication/IJwtTokenGenerator.cs` |
| NEW | `src/Conterex.Compliance.Application/Abstractions/Authentication/ICurrentUserService.cs` |
| NEW | `src/Conterex.Compliance.Application/Abstractions/Authentication/IUserStore.cs` |
| NEW | `src/Conterex.Compliance.Application/Exceptions/InvalidCredentialsException.cs` |
| NEW | `src/Conterex.Compliance.Application/Authentication/Login/LoginCommand.cs` |
| NEW | `src/Conterex.Compliance.Application/Authentication/Login/LoginCommandHandler.cs` |
| NEW | `src/Conterex.Compliance.Application/Authentication/Login/LoginCommandValidator.cs` |
| NEW | `src/Conterex.Compliance.Application/Authentication/Login/LoginResponse.cs` |
| NEW | `src/Conterex.Compliance.Web/Authentication/JwtOptions.cs` |
| NEW | `src/Conterex.Compliance.Web/Authentication/DevUserOptions.cs` |
| NEW | `src/Conterex.Compliance.Web/Authentication/JwtTokenGenerator.cs` |
| NEW | `src/Conterex.Compliance.Web/Authentication/CurrentUserService.cs` |
| NEW | `src/Conterex.Compliance.Web/Authentication/DevUserStore.cs` |
| NEW | `src/Conterex.Compliance.Web/Authentication/AuthenticationServiceCollectionExtensions.cs` |
| NEW | `src/Conterex.Compliance.Presentation/Controllers/AuthController.cs` |
| MODIFIED | `src/Conterex.Compliance.Web/Startup.cs` (wire-up + middleware order) |
| MODIFIED | `src/Conterex.Compliance.Web/Conterex.Compliance.Web.csproj` (added `Microsoft.AspNetCore.Authentication.JwtBearer 6.0.36`) |
| MODIFIED | `src/Conterex.Compliance.Presentation/Controllers/WebinarsController.cs` (`[Authorize]` + `[AllowAnonymous]`) |

## Validation steps

- ✅ `dotnet build` succeeds with no errors.
- ✅ Without `Jwt:SigningKey` configured: `InvalidOperationException: Jwt:SigningKey is missing or shorter than 32 characters` at boot.
- ✅ With user-secrets configured and the app running:
  - `POST /api/auth/login` with correct DEV credentials → `200 OK` with a JWT
  - `POST /api/webinars` without `Authorization` header → `401 Unauthorized`
  - `POST /api/webinars` with `Authorization: Bearer <token>` → `201 Created`
  - `GET /api/webinars/{id}` without a token → `200 OK` (`[AllowAnonymous]` works)

## Security impact

| Concern | Before | After |
|---------|--------|-------|
| `[Authorize]` enforced on write endpoints | NO | YES |
| Tokens cryptographically signed | n/a | YES (HS256, ≥256-bit key) |
| Issuer / audience / lifetime / signature all validated | n/a | YES |
| Boot-time failure on missing JWT config | n/a | YES (`ValidateOnStart`) |
| Authentication middleware ordered before authorization | n/a | YES |

## Production impact

- **Replace `DevUserStore`** with a real `IUserStore` backed by a user database before any production deployment. The class is intentionally annotated as DEV-ONLY in its XML doc.
- **Provide a real signing key** via secret manager. The key must be at least 32 characters of high-entropy random data. Rotating the key invalidates all existing tokens — coordinate the rotation with token TTLs.
- **Adjust token lifetime** via `Jwt:AccessTokenLifetimeMinutes` (default 60). For browser clients consider refresh tokens; for service-to-service calls a longer-lived token may be acceptable.
- **Configure HTTPS-only token transport** in production. The codebase already has `app.UseHttpsRedirection()` in the pipeline; add HSTS in a later hardening phase.

## How to add a protected endpoint

Same pattern as `CreateWebinar`:

```csharp
[HttpPost("cancel")]
[ProducesResponseType(StatusCodes.Status204NoContent)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public async Task<IActionResult> CancelWebinar(...)
{
    // Inherits [Authorize] from the controller. No further attribute needed.
}
```

For role-based gating:
```csharp
[HttpPost, Authorize(Roles = "Admin")]
public async Task<IActionResult> AdminOnlyAction(...) { ... }
```

The `DevUserStore` assigns `"Admin"` to the dev user, so the local flow can exercise role-based endpoints end-to-end.

## Not addressed in this phase (deferred)

- Refresh tokens / token rotation
- Token revocation
- Multi-factor authentication
- OAuth / OIDC code-flow with an external IdP
- Password hashing (DEV user store does plaintext comparison — production must hash with Argon2id or PBKDF2)
- Account lockout, brute-force protection, rate limiting on `/api/auth/login`
- Asymmetric signing (RS256) — for multi-service deployments where the resource server shouldn't hold the signing key
