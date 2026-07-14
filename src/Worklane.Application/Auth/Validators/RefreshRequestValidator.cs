using FluentValidation;
using Worklane.Application.Auth.Dtos;

namespace Worklane.Application.Auth.Validators;

public class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}
