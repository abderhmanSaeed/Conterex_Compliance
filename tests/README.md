# Tests

Test projects land here. None exist yet — this folder is a placeholder so the layout already matches the conventions established in [`../docs/Architecture-Documentation/`](../docs/Architecture-Documentation/).

Recommended test projects for the next phase:

- `Conterex.Compliance.Domain.UnitTests` — pure unit tests for aggregate behavior (`Webinar.Create`, `Reschedule`, `Cancel`) and value-object invariants.
- `Conterex.Compliance.Application.UnitTests` — command/query handlers with `IUserStore`, `IDateTimeProvider`, and repository test doubles.
- `Conterex.Compliance.Infrastructure.IntegrationTests` — `WebApplicationFactory` + [`Testcontainers.PostgreSql`](https://github.com/testcontainers/testcontainers-dotnet) for end-to-end checks against a real database.

When adding a test project:

```powershell
dotnet new xunit -n Conterex.Compliance.Domain.UnitTests -o tests/Conterex.Compliance.Domain.UnitTests
dotnet sln Conterex.Compliance.sln add tests/Conterex.Compliance.Domain.UnitTests/Conterex.Compliance.Domain.UnitTests.csproj
dotnet add tests/Conterex.Compliance.Domain.UnitTests reference src/Conterex.Compliance.Domain/Conterex.Compliance.Domain.csproj
```
