namespace Conterex.Compliance.Application.Abstractions.Authentication;

public interface IJwtTokenGenerator
{
    AccessToken Generate(UserCredentials credentials);
}
