using FluentValidation;
using Worklane.Application.Workspaces.Dtos;

namespace Worklane.Application.Workspaces.Validators;

public class UpdateMemberRoleRequestValidator : AbstractValidator<UpdateMemberRoleRequest>
{
    public UpdateMemberRoleRequestValidator()
    {
        // Owner and Admin stay legal here. Whether this caller may assign them is decided
        // by WorkspaceMembershipPolicy from their membership in the route workspace.
        RuleFor(x => x.Role).IsInEnum();
    }
}
