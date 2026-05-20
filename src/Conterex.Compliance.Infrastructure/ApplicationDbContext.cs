using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Conterex.Compliance.Application.Events;
using Conterex.Compliance.Domain.Abstractions;
using Conterex.Compliance.Domain.Primitives;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Conterex.Compliance.Infrastructure;

public sealed class ApplicationDbContext : DbContext, IUnitOfWork
{
    private readonly IPublisher _publisher;

    public ApplicationDbContext(DbContextOptions options, IPublisher publisher) : base(options)
    {
        _publisher = publisher;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Collect events before saving so the aggregate's event list is cleared even on failure.
        // We publish AFTER base.SaveChangesAsync succeeds so handlers never see "event fired
        // but data not persisted" — a fundamental invariant for any future outbox / projection
        // work the platform layers on top.
        var domainEvents = ChangeTracker.Entries<AggregateRoot>()
            .Select(entry => entry.Entity)
            .SelectMany(aggregate =>
            {
                var events = aggregate.DomainEvents.ToList();
                aggregate.ClearDomainEvents();
                return events;
            })
            .ToList();

        var result = await base.SaveChangesAsync(cancellationToken);

        foreach (var domainEvent in domainEvents)
        {
            var notificationType = typeof(DomainEventNotification<>).MakeGenericType(domainEvent.GetType());
            var notification = (INotification)Activator.CreateInstance(notificationType, domainEvent)!;
            await _publisher.Publish(notification, cancellationToken);
        }

        return result;
    }
}
