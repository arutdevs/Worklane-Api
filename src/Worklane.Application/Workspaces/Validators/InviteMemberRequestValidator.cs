using FluentValidation;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;

namespace Worklane.Application.Workspaces.Validators;

public class InviteMemberRequestValidator : AbstractValidator<InviteMemberRequest>
{
    public InviteMemberRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Role).IsInEnum().NotEqual(WorkspaceRole.Owner)
            .WithMessage("Cannot invite a member directly as Owner.");
    }
}
