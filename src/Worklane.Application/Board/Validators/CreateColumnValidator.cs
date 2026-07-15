using FluentValidation;
using Worklane.Application.Board.Dtos;

namespace Worklane.Application.Board.Validators;

public class CreateColumnValidator : AbstractValidator<CreateColumnRequest>
{
    public CreateColumnValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
    }
}
