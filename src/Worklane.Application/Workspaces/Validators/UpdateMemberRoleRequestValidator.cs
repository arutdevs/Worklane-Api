using FluentValidation;
using Worklane.Application.Workspaces.Dtos;

namespace Worklane.Application.Workspaces.Validators;

public class UpdateMemberRoleRequestValidator : AbstractValidator<UpdateMemberRoleRequest>
{
    public UpdateMemberRoleRequestValidator()
    {
        RuleFor(x => x.Role).IsInEnum();
    }
}
