using System;
using System.Threading;
using System.Threading.Tasks;
using Conterex.Compliance.Application.Abstractions.Messaging;
using Conterex.Compliance.Domain.Abstractions;
using Conterex.Compliance.Domain.Entities;

namespace Conterex.Compliance.Application.Webinars.Commands.CreateWebinar;

internal sealed class CreateWebinarCommandHandler : ICommandHandler<CreateWebinarCommand, Guid>
{
    private readonly IWebinarRepository _webinarRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDateTimeProvider _dateTimeProvider;

    public CreateWebinarCommandHandler(
        IWebinarRepository webinarRepository,
        IUnitOfWork unitOfWork,
        IDateTimeProvider dateTimeProvider)
    {
        _webinarRepository = webinarRepository;
        _unitOfWork = unitOfWork;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<Guid> Handle(CreateWebinarCommand request, CancellationToken cancellationToken)
    {
        var webinar = Webinar.Create(request.Name, request.ScheduledOn, _dateTimeProvider);

        _webinarRepository.Insert(webinar);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return webinar.Id;
    }
}
