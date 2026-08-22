using FluentValidation;
using Worklane.Application.Board.Dtos;

namespace Worklane.Application.Board.Validators;

public class CreateTaskValidator : AbstractValidator<CreateTaskRequest>
{
    public CreateTaskValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Description).MaximumLength(10_000).When(x => x.Description is not null);
        // One working year is the ceiling — anything larger is a typo, not a plan.
        RuleFor(x => x.EstimateDays)
            .GreaterThan(0).LessThanOrEqualTo(260)
            .When(x => x.EstimateDays.HasValue);
    }
}
