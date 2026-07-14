using FluentValidation;
using Worklane.Application.Workspaces.Dtos;

namespace Worklane.Application.Workspaces.Validators;

public class AcceptInvitationRequestValidator : AbstractValidator<AcceptInvitationRequest>
{
    public AcceptInvitationRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
    }
}
