using FluentValidation;
using Worklane.Application.Board.Dtos;

namespace Worklane.Application.Board.Validators;

public class MoveTaskValidator : AbstractValidator<MoveTaskRequest>
{
    public MoveTaskValidator()
    {
        RuleFor(x => x.ToColumnId).NotEmpty();
        RuleFor(x => x.ToIndex).GreaterThanOrEqualTo(0);
    }
}
