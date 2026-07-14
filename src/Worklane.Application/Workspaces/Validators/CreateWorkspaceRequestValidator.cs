using FluentValidation;
using Worklane.Application.Workspaces.Dtos;

namespace Worklane.Application.Workspaces.Validators;

public class CreateWorkspaceRequestValidator : AbstractValidator<CreateWorkspaceRequest>
{
    public CreateWorkspaceRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}
