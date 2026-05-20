using Conterex.Compliance.Domain.Abstractions;
using Conterex.Compliance.Domain.Entities;
using FluentValidation;

namespace Conterex.Compliance.Application.Webinars.Commands.CreateWebinar;

public sealed class CreateWebinarCommandValidator : AbstractValidator<CreateWebinarCommand>
{
    public CreateWebinarCommandValidator(IDateTimeProvider dateTimeProvider)
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Webinar name is required.")
            .MaximumLength(Webinar.NameMaxLength)
                .WithMessage($"Webinar name cannot exceed {Webinar.NameMaxLength} characters.");

        RuleFor(x => x.ScheduledOn)
            .NotEmpty().WithMessage("Webinar schedule date is required.")
            .Must(scheduledOn => scheduledOn > dateTimeProvider.UtcNow)
                .WithMessage("Webinar must be scheduled in the future.");
    }
}
