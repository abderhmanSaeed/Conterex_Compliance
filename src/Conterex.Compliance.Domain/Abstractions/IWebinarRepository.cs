using Conterex.Compliance.Domain.Entities;

namespace Conterex.Compliance.Domain.Abstractions;

public interface IWebinarRepository
{
    void Insert(Webinar webinar);
}