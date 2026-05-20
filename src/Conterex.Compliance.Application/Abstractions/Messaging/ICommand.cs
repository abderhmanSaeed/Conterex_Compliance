using MediatR;

namespace Conterex.Compliance.Application.Abstractions.Messaging;

public interface ICommand<out TResponse> : IRequest<TResponse>
{
}