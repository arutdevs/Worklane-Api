using FluentValidation;
using Worklane.Application.Projects.Dtos;

namespace Worklane.Application.Projects.Validators;

public class CreateProjectRequestValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Key)
            .NotEmpty()
            .MaximumLength(10)
            .Matches("^[A-Z][A-Z0-9]*$")
            .WithMessage("Key must be uppercase letters/digits, starting with a letter (e.g. \"WL\").");
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}
