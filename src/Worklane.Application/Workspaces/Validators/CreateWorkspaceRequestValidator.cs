using FluentValidation;
using Worklane.Application.Workspaces.Dtos;

namespace Worklane.Application.Workspaces.Validators;

public class CreateWorkspaceRequestValidator : AbstractValidator<CreateWorkspaceRequest>
{
    public CreateWorkspaceRequestValidator()
    {
        RuleFor(x => x.Name)
            .Must(static name => !string.IsNullOrWhiteSpace(name))
            .WithErrorCode("not_empty")
            .WithMessage("'{PropertyName}' must not be empty.")
            .MaximumLength(200);
    }
}
