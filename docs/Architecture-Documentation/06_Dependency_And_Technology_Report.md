# 06 — Dependency and Technology Report

> Snapshot date: **2026-05-20**. All findings derived from the actual `.csproj` files; recommendations target the .NET LTS landscape current at that time.

---

## 1. Framework version

| Project | TargetFramework | Status as of 2026-05 |
|---------|-----------------|----------------------|
| `Domain` | `net6.0` | **End of life (Nov 2024)** |
| `Application` | `net6.0` | **End of life (Nov 2024)** |
| `Infrastructure` | `net6.0` | **End of life (Nov 2024)** |
| `Presentation` | `net6.0` | **End of life (Nov 2024)** |
| `Web` | `net6.0` | **End of life (Nov 2024)** |

**.NET 6 LTS reached end-of-support on 12 November 2024.** Running on it today means:
- No more security patches from Microsoft.
- Public CVE advisories that affect runtime/framework will not be fixed.
- Many newer NuGet packages have dropped `net6.0` from their `TargetFrameworks`.
- Container base images (`mcr.microsoft.com/dotnet/aspnet:6.0`) will eventually be removed.

### Recommendation: upgrade to .NET 8 (LTS, supported through Nov 2026) or .NET 9 (STS, supported through May 2026)

| Option | Pros | Cons |
|--------|------|------|
| **Upgrade to .NET 8 LTS** | Supported through Nov 2026; minimal breaking changes vs net6; battle-tested for two years | Will need another upgrade by late 2026 |
| **Upgrade to .NET 9 STS** | Newer feature surface; OpenTelemetry integrations more mature | Standard-Term Support ends May 2026 (very soon); will need .NET 10 upgrade |
| **Plan for .NET 10 LTS** (releasing Nov 2026) | Supported through Nov 2028 | Not yet released |

**Pragmatic path:** upgrade to **.NET 8 LTS now**, plan a second upgrade to .NET 10 LTS in late 2026. This is the standard "two-step" enterprise approach. The net6 → net8 upgrade is largely a `<TargetFramework>` swap plus addressing minor breaking changes in EF Core and ASP.NET (most notably `Microsoft.AspNetCore.Mvc.Core 2.2.5` must go — see §3).

### Migration checklist

1. Bump every `.csproj`: `<TargetFramework>net8.0</TargetFramework>`.
2. Update every `PackageReference` to a `8.0.x`-compatible version (see §3 below).
3. Remove `Microsoft.AspNetCore.Mvc.Core 2.2.5` from `Presentation.csproj` — the framework-included version is correct, and the explicit reference is wrong.
4. Switch `Web/Program.cs` + `Web/Startup.cs` to minimal hosting (File 03 §16).
5. Run `dotnet test`; address any analyzer warnings.
6. Re-test docker build with the new base image `mcr.microsoft.com/dotnet/aspnet:8.0`.

---

## 2. NuGet package inventory

### Domain — `Domain.csproj`

```xml
<!-- No package references — clean domain -->
```

✅ **Perfect.** Zero dependencies. The Domain is genuinely framework-free.

### Application — `Application.csproj`

| Package | Current version | Latest stable (May 2026) | Risk | Recommendation |
|---------|-----------------|---------------------------|------|----------------|
| `Dapper` | 2.0.123 | 2.1.x | ⚠️ Outdated minor; no known CVEs at 2.0.123 | **Remove from Application** — Dapper belongs in Infrastructure only (File 03 §5). If kept, upgrade to 2.1.x for `CommandDefinition` ergonomics |
| `FluentValidation` | 11.1.1 | 11.x latest | 🟢 Patches available | Bump to latest 11.x; do **not** jump to 12 yet (breaking changes; not all integrations have caught up) |
| `Mapster` | 7.3.0 | 7.4.x | 🔴 **Unused in Application** | **Remove** — it's only used in Presentation. If you adopt explicit Mapster configs in Application (File 05 §12), re-add with intent |
| `MediatR` | 10.0.1 | **12.x** (now under a paid license model from 13.x) | 🟢 No known CVEs | **Stay on 10 or upgrade carefully to 11.x**. MediatR licensing changed in late 2024 — versions 12.x are still permissive, but 13.x+ requires a commercial license for organizations >$1M revenue. Evaluate carefully or migrate to an in-house mediator |

### Infrastructure — `Infrastructure.csproj`

| Package | Current version | Latest stable (May 2026) | Risk | Recommendation |
|---------|-----------------|---------------------------|------|----------------|
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 6.0.6 | 8.0.x (matches .NET 8) | ⚠️ Old patch; CVEs in older Npgsql versions exist | Upgrade alongside the .NET 8 framework move; pin `8.0.x` |

### Presentation — `Presentation.csproj`

| Package | Current version | Latest stable (May 2026) | Risk | Recommendation |
|---------|-----------------|---------------------------|------|----------------|
| `Mapster` | 7.3.0 | 7.4.x | 🟡 outdated minor | Upgrade to 7.4.x |
| `MediatR` | 10.0.1 | see Application note | — | Same as Application — keep version aligned |
| `Microsoft.AspNetCore.Mvc.Core` | **2.2.5** | — | 🚨 **EOL / wrong** | **Remove entirely.** AspNetCore.Mvc.Core 2.2.x is from the ASP.NET Core 2.x era — long EOL. On a `net6.0`+ project, MVC types come from the `Microsoft.NET.Sdk.Web` framework reference (or via `AddApplicationPart`). The 2.2.5 package is at best redundant, at worst a binding-redirect risk |

The fix:
```xml
<!-- Presentation/Presentation.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Debug|AnyCPU'">
    <DocumentationFile>Presentation.xml</DocumentationFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Mapster" Version="7.4.0" />
    <PackageReference Include="MediatR.Contracts" Version="2.0.1" />
  </ItemGroup>

  <!-- Framework reference grants MVC types without an explicit Mvc.Core PackageReference -->
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Application\Application.csproj" />
  </ItemGroup>

</Project>
```

### Web — `Web.csproj`

| Package | Current version | Latest stable (May 2026) | Risk | Recommendation |
|---------|-----------------|---------------------------|------|----------------|
| `FluentValidation.AspNetCore` | 11.1.3 | **deprecated** | 🚨 **The package itself is deprecated** by the FluentValidation maintainers | Remove. Auto-MVC integration is officially discontinued; use `services.AddValidatorsFromAssembly(...)` (already present in `Startup.cs:40`) and do validation via the MediatR `ValidationBehavior`. The MVC ModelState integration is the part being deprecated |
| `MediatR.Extensions.Microsoft.DependencyInjection` | 10.0.1 | bundled with MediatR 11+ | 🟢 | When you upgrade MediatR to 11.x, this package merges into the main MediatR package — remove the separate reference |
| `Microsoft.EntityFrameworkCore.Tools` | 6.0.7 | 8.0.x | ⚠️ Old | Bump together with EF Core during the .NET 8 migration |
| `Microsoft.VisualStudio.Azure.Containers.Tools.Targets` | 1.16.1 | latest | 🟢 | VS-only tooling; auto-updates via VS. Safe to leave |
| `Swashbuckle.AspNetCore` | 6.4.0 | 6.6.x | 🟡 | Upgrade to latest 6.x. Note: `Microsoft.AspNetCore.OpenApi` is now the supported approach for ASP.NET Core 8+ if you want to drop Swashbuckle entirely |

---

## 3. Critical dependency findings

### 🚨 `Microsoft.AspNetCore.Mvc.Core 2.2.5` on a `net6.0` project

`AspNetCore.Mvc.Core 2.2.5` predates ASP.NET Core 3.0 (which is where the framework moved into the shared runtime). Pulling that package into a `net6.0` project is either:

- **Redundant** — the symbols it provides are already available via `Microsoft.NET.Sdk.Web`'s `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, OR
- **Conflicting** — older types and method signatures may shadow the framework-included ones, producing subtle runtime issues.

It also pulls a long tail of `2.2.x` transitive packages (`Microsoft.Extensions.*`, `Microsoft.AspNetCore.*`) that have all received CVE patches in subsequent major versions. `dotnet list package --vulnerable --include-transitive` will likely surface a handful from this single line.

**Fix:** Remove the package; rely on the framework reference (see Presentation.csproj sketch above).

### 🚨 `FluentValidation.AspNetCore 11.1.3` is deprecated

The FluentValidation team [explicitly deprecated the ASP.NET Core auto-MVC integration](https://docs.fluentvalidation.net/en/latest/aspnet.html) — the recommended approach is "manual" validation (which is exactly what your `ValidationBehavior` does already). Remove this package; nothing else needs to change because the pipeline behavior handles all validation today.

### ⚠️ `MediatR` licensing change (versions 13+)

As of late 2024 the MediatR maintainers introduced a commercial license for MediatR 13.x+ — organizations above a revenue threshold are required to purchase a license. Versions 10.x and 11.x remain free, but new features (in particular better source-generator performance and `NotificationHandler` improvements) live in 12.x+.

Three options:
1. **Stay on 10.x** — works, but you're locked out of perf improvements.
2. **Upgrade to 11.x or 12.x** — last permissive versions; pin them.
3. **Migrate to an in-house mediator or alternative** — for the surface area this codebase uses (request → handler → behaviors), rolling your own is roughly 50-100 lines. Worth considering if you want to avoid the licensing question entirely.

### ⚠️ Mapster declared in Application but unused

`Application.csproj:10` references Mapster 7.3.0, but no Application file imports or uses `Mapster.*` types. The only call is in `Presentation/Controllers/WebinarsController.cs:48`. Remove the Application reference (already documented in File 03 §15).

### ⚠️ Dapper in Application is a layer violation

Dapper *itself* is fine, but its presence in `Application.csproj` (not just `Infrastructure.csproj`) is the proximate cause of the infrastructure leakage in `GetWebinarQueryHandler` (File 03 §5). Once you abstract Dapper behind an Application-defined contract, the package reference moves to Infrastructure only.

---

## 4. Recommended additions

Packages this codebase **should** depend on for an enterprise build but currently doesn't:

### Cross-cutting

| Package | Why | Where to install |
|---------|-----|------------------|
| `Serilog.AspNetCore` + `Serilog.Sinks.Console` + `Serilog.Sinks.Seq` (dev) / `Serilog.Sinks.Elasticsearch` (prod) | Structured logging, request logging, correlation context | Web |
| `OpenTelemetry.Extensions.Hosting` + `OpenTelemetry.Instrumentation.AspNetCore` + `OpenTelemetry.Instrumentation.EntityFrameworkCore` + `OpenTelemetry.Exporter.OpenTelemetryProtocol` | Distributed tracing + metrics | Web |
| `Microsoft.AspNetCore.Authentication.JwtBearer` (8.0.x) | JWT bearer authentication | Web |
| `Microsoft.AspNetCore.HealthChecks.NpgSql` (or `HealthChecks.NpgSql` from `AspNetCore.Diagnostics.HealthChecks`) | DB health probe | Web |
| `Polly` (8.x) | Retry, circuit breaker for HttpClients and DB | Infrastructure |
| `Microsoft.Extensions.Http.Polly` | `AddPolicyHandler` integration for HttpClient | Infrastructure / Web |
| `Microsoft.Extensions.Caching.StackExchangeRedis` | Distributed cache | Infrastructure |
| `Microsoft.Extensions.Options.ConfigurationExtensions` + `Microsoft.Extensions.Options.DataAnnotations` | Strongly typed options binding + validation | Web |
| `Asp.Versioning.Mvc.ApiExplorer` | API versioning with Swagger integration | Presentation + Web |
| `AspNetCoreRateLimit` *(or use the built-in `Microsoft.AspNetCore.RateLimiting` on .NET 7+)* | Public-endpoint rate limiting | Web |

### Background processing (pick one)

| Package | Use case |
|---------|----------|
| `Hangfire.AspNetCore` + `Hangfire.PostgreSql` | Shared-DB scheduler, dashboard, ad-hoc jobs |
| `Quartz.Extensions.Hosting` | Quartz cluster mode for multi-instance scheduling |
| Worker service (`dotnet new worker`) + MassTransit or pure consumer | Message-bus driven async work |

### Testing

| Package | Purpose |
|---------|---------|
| `Microsoft.AspNetCore.Mvc.Testing` | `WebApplicationFactory` for integration tests |
| `xunit` + `xunit.runner.visualstudio` | Test framework |
| `FluentAssertions` | Readable assertion style |
| `Testcontainers.PostgreSql` | Ephemeral PostgreSQL per test run |
| `Bogus` | Deterministic fake data |
| `NSubstitute` *or* `Moq` | Mocking |
| `Verify.Xunit` | Snapshot testing for query response shapes |

### Domain enrichment / DDD utilities

Most DDD primitives (AggregateRoot, ValueObject, smart enum) are 20-50 lines and worth writing in your `Domain/Primitives/` folder rather than pulling in a library. The exceptions:

| Package | Optional / opinion |
|---------|---------------------|
| `Ardalis.Result` *or* `OneOf` | Result/discriminated-union types if you choose to model failures as values rather than exceptions |
| `MediatR.Contracts` | Lightweight contracts-only MediatR (avoids pulling the full package into Application if you only need the interfaces) |

---

## 5. Recommended `.csproj` layouts (post-cleanup, post-upgrade)

### `Domain/Domain.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

### `Application/Application.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="FluentValidation" Version="11.9.x" />
    <PackageReference Include="MediatR" Version="11.x" />
    <!-- Dapper and Mapster are NOT here — those concerns live in Infrastructure / Presentation respectively -->
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Domain\Domain.csproj" />
  </ItemGroup>
</Project>
```

### `Infrastructure/Infrastructure.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="8.0.x" />
    <PackageReference Include="Dapper" Version="2.1.x" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" Version="8.0.x" />
    <PackageReference Include="Polly" Version="8.x" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Application\Application.csproj" /> <!-- Required to implement Application abstractions -->
    <ProjectReference Include="..\Domain\Domain.csproj" />
  </ItemGroup>
</Project>
```

> **Note:** introducing the `Application` reference into Infrastructure is a deliberate change. Today Infrastructure only references Domain, which means Application abstractions (like `ISqlConnectionFactory` or `IEmailSender`) cannot be implemented in Infrastructure. The standard Clean Architecture variant exposes those abstractions in Application and lets Infrastructure implement them — so Infrastructure must reference Application. Web is still the only place where all four are wired together.

### `Presentation/Presentation.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Debug|AnyCPU'">
    <DocumentationFile>Presentation.xml</DocumentationFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Mapster" Version="7.4.x" />
    <PackageReference Include="MediatR.Contracts" Version="2.x" />  <!-- only contracts, not the full MediatR -->
  </ItemGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Application\Application.csproj" />
  </ItemGroup>
</Project>
```

### `Web/Web.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UserSecretsId>540db5be-b2a8-4c4d-ace3-5761b60b3c97</UserSecretsId>
    <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>
    <DockerComposeProjectPath>..\docker-compose.dcproj</DockerComposeProjectPath>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MediatR" Version="11.x" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.0.x" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Tools" Version="8.0.x">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.VisualStudio.Azure.Containers.Tools.Targets" Version="1.*" />
    <PackageReference Include="Swashbuckle.AspNetCore" Version="6.6.x" />
    <PackageReference Include="Serilog.AspNetCore" Version="8.x" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.x" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.x" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.x" />
    <PackageReference Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" Version="1.x" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.x" />
    <PackageReference Include="AspNetCore.HealthChecks.NpgSql" Version="8.x" />
    <PackageReference Include="Asp.Versioning.Mvc.ApiExplorer" Version="8.x" />
    <!-- Add Hangfire / Polly / Redis cache here when you adopt them -->
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Domain\Domain.csproj" />
    <ProjectReference Include="..\Application\Application.csproj" />
    <ProjectReference Include="..\Infrastructure\Infrastructure.csproj" />
    <ProjectReference Include="..\Presentation\Presentation.csproj" />
  </ItemGroup>
</Project>
```

---

## 6. CVE / vulnerability posture

You can produce an authoritative list with:

```powershell
dotnet list package --vulnerable --include-transitive
```

Run this **before** and **after** the .NET 8 upgrade. Expect the current state to flag:

- `Microsoft.AspNetCore.Mvc.Core 2.2.5` and its 2.2.x transitive closure (multiple known issues over the 2.2 → 3.x → 5.x → 6.x → 8.x evolution).
- Older `Microsoft.Extensions.*` from the 2.2 era.
- `Npgsql.EntityFrameworkCore.PostgreSQL 6.0.6` — Npgsql has had several patch-level security fixes since 6.0.6.
- Potentially `Dapper 2.0.123` — depends on the rolling CVE list at audit time.

After upgrading to .NET 8 + the recommended package versions above, the same command should return a clean list.

Automate this in CI:
```yaml
- name: Check vulnerable packages
  run: dotnet list package --vulnerable --include-transitive --format json > vulns.json
- name: Fail on vulnerabilities
  run: |
    if [ "$(jq '.projects[].frameworks[].topLevelPackages | length' vulns.json | awk '{s+=$1} END {print s}')" != "0" ]; then
      echo "Vulnerable packages found"; cat vulns.json; exit 1
    fi
```

---

## 7. Technology stack quality assessment

| Component | Choice | Assessment |
|-----------|--------|------------|
| Language | C# | ✅ Industry standard for the use case |
| Runtime | .NET 6 → recommend .NET 8 | ⚠️ Currently EOL; upgrade in flight |
| Web framework | ASP.NET Core MVC | ✅ Well-suited; minimal-API would also work for endpoints with tiny footprints |
| ORM | EF Core | ✅ Right call for the write side |
| Query library | Dapper | ✅ Right call for the read side; just keep it in Infrastructure |
| Database | PostgreSQL | ✅ Excellent default for OLTP; mature, open, well-supported |
| Mediator | MediatR | ⚠️ Watch licensing trajectory; consider in-house alternative for the long term |
| Validation | FluentValidation | ✅ Best in class for .NET |
| Mapping | Mapster | 🟡 Good choice; needs explicit configs to avoid silent breakage |
| API documentation | Swashbuckle | ✅ Standard; consider `Microsoft.AspNetCore.OpenApi` on .NET 8+ if you want one less dependency |
| Container | Docker multi-stage | ✅ Right pattern; harden per File 04 §8 |
| Container orchestration | None yet | — Pick Kubernetes when you have ≥3 services or need autoscaling |
| Logging | Built-in `Microsoft.Extensions.Logging` | ❌ Needs upgrade to Serilog for structured logs |
| Observability | None | ❌ Add OpenTelemetry |
| Auth | None | ❌ Add JWT bearer |
| Background work | None | ❌ Pick Hangfire or worker service |
| Caching | None | ❌ Add `IDistributedCache` (Redis in production) |
| Message bus | None | ❌ Add when async integration events are needed |
| Tests | None | ❌ Add xUnit + Testcontainers immediately |
| CI/CD | None | ❌ Add GitHub Actions or equivalent |

**Bottom line:** the *choices that exist* are uniformly well-considered. The gap is in the *missing choices* — observability, auth, background processing, caching, tests, CI/CD. Filling those is the work of File 04's roadmap.

---

## 8. Package upgrade priority order

Do this in this order so each step is independent and shippable:

1. **Remove `Microsoft.AspNetCore.Mvc.Core 2.2.5`** from Presentation. Solo change; minimal risk. Should compile fine because the framework reference covers the symbols.
2. **Remove `FluentValidation.AspNetCore`** from Web. Solo change; only the deprecated MVC ModelState bridge is affected, which the codebase doesn't use.
3. **Remove `Dapper` and `Mapster`** from Application. After the read-side abstraction refactor (File 03 §5) — these two should land in the same PR.
4. **Bump every remaining package to its latest patch** within the same major version. Run `dotnet list package --outdated`; address the green ones first.
5. **Plan the .NET 8 upgrade** as its own dedicated change. All `<TargetFramework>` swaps in one PR; all package major-version bumps in another PR or rolled in together depending on team appetite.
6. **Add the recommended new packages** in §4 incrementally, one capability at a time, with tests.
