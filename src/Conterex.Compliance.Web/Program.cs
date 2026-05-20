using System;
using System.Linq;
using System.Threading.Tasks;
using Conterex.Compliance.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conterex.Compliance.Web;

public class Program
{
    /// <summary>
    /// Environment variable name that, when set to "true", causes the app to apply
    /// EF Core migrations during startup. Intended for LOCAL DEVELOPMENT ONLY.
    /// Production deployments should apply migrations as a separate pipeline step
    /// (see Architecture-Documentation/Foundation-Hardening/04_Migration_Strategy.md).
    /// </summary>
    private const string ApplyMigrationsEnvVar = "APPLY_MIGRATIONS_ON_STARTUP";

    public static async Task Main(string[] args)
    {
        var webHost = CreateHostBuilder(args).Build();

        if (ShouldApplyMigrations(args))
        {
            await ApplyMigrationsAsync(webHost.Services);
        }

        await webHost.RunAsync();
    }

    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder => webBuilder.UseStartup<Startup>());

    private static bool ShouldApplyMigrations(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--migrate", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var envValue = Environment.GetEnvironmentVariable(ApplyMigrationsEnvVar);
        return string.Equals(envValue, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ApplyMigrationsAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await dbContext.Database.MigrateAsync();
    }
}
