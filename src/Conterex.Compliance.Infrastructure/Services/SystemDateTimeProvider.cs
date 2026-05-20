using System;
using Conterex.Compliance.Domain.Abstractions;

namespace Conterex.Compliance.Infrastructure.Services;

public sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}
