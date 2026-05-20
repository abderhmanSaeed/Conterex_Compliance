using Conterex.Compliance.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Conterex.Compliance.Infrastructure.Configurations;

internal sealed class WebinarConfiguration : IEntityTypeConfiguration<Webinar>
{
    public void Configure(EntityTypeBuilder<Webinar> builder)
    {
        builder.ToTable("Webinars");

        builder.HasKey(webinar => webinar.Id);

        builder.Property(webinar => webinar.Name)
            .HasMaxLength(Webinar.NameMaxLength)
            .IsRequired();

        builder.Property(webinar => webinar.ScheduledOn).IsRequired();

        builder.Property(webinar => webinar.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(webinar => webinar.CancellationReason)
            .HasMaxLength(Webinar.CancellationReasonMaxLength);

        // Domain events are an in-memory concern only; never persist them.
        builder.Ignore(webinar => webinar.DomainEvents);
    }
}
