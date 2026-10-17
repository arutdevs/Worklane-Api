using FluentValidation;
using Worklane.Application.Board.Dtos;

namespace Worklane.Application.Board.Validators;

public class UpdateColumnValidator : AbstractValidator<UpdateColumnRequest>
{
    public UpdateColumnValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100).When(x => x.Name is not null);
        RuleFor(x => x.WipLimit.Value)
            .GreaterThan(0)
            .When(x => x.WipLimit.IsSpecified && x.WipLimit.Value.HasValue);
    }
}
