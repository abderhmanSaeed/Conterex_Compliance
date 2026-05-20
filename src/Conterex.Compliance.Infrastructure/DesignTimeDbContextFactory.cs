using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Conterex.Compliance.Infrastructure;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> commands (migrations add, database update,
/// migrations bundle, etc.). Avoids the runtime host pipeline entirely so EF tooling
/// works without needing user-secrets, environment variables, or a real MediatR publisher.
///
/// The connection string here is a placeholder — it is NEVER used to issue real SQL.
/// EF only needs it to build the model graph for migration scaffolding.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    // Placeholder connection string used ONLY for EF model graph scaffolding.
    // EF tooling does not open a connection just to enumerate migrations, so these
    // values are never used to authenticate. Real connection strings come from
    // user-secrets / environment variables at runtime.
    private const string DesignTimeConnectionString =
        "Host=localhost;Port=5432;Database=ef_design_time_only;Username=ef_tool;Password=ef_tool_placeholder_not_a_real_secret";

    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(DesignTimeConnectionString)
            .Options;

        return new ApplicationDbContext(options, new NoOpPublisher());
    }

    private sealed class NoOpPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification =>
            Task.CompletedTask;
    }
}
