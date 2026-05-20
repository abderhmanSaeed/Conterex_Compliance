using System.Threading;
using System.Threading.Tasks;
using Conterex.Compliance.Application.Abstractions.Authentication;
using Conterex.Compliance.Application.Abstractions.Messaging;
using Conterex.Compliance.Application.Exceptions;

namespace Conterex.Compliance.Application.Authentication.Login;

internal sealed class LoginCommandHandler : ICommandHandler<LoginCommand, LoginResponse>
{
    private readonly IUserStore _userStore;
    private readonly IJwtTokenGenerator _tokenGenerator;

    public LoginCommandHandler(IUserStore userStore, IJwtTokenGenerator tokenGenerator)
    {
        _userStore = userStore;
        _tokenGenerator = tokenGenerator;
    }

    public async Task<LoginResponse> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var credentials = await _userStore.FindAsync(request.Email, request.Password, cancellationToken)
            ?? throw new InvalidCredentialsException();

        var token = _tokenGenerator.Generate(credentials);

        return new LoginResponse(token.Token, token.ExpiresAtUtc);
    }
}
