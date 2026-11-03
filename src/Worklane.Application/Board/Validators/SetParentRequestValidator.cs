using FluentValidation;
using Worklane.Application.Board.Dtos;

namespace Worklane.Application.Board.Validators;

public class SetParentRequestValidator : AbstractValidator<SetParentRequest>
{
    public SetParentRequestValidator()
    {
        RuleFor(x => x.ParentTaskId)
            .NotEmpty()
            .WithErrorCode("not_empty")
            .WithMessage("'{PropertyName}' must not be empty.");
    }
}
