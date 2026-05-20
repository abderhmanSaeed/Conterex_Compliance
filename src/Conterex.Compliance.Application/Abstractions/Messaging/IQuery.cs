using MediatR;

namespace Conterex.Compliance.Application.Abstractions.Messaging;

public interface IQuery<out TResponse> : IRequest<TResponse>
{
}