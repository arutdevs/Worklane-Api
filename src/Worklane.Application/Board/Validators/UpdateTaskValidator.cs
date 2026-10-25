using FluentValidation;
using Worklane.Application.Board.Dtos;

namespace Worklane.Application.Board.Validators;

public class UpdateTaskValidator : AbstractValidator<UpdateTaskRequest>
{
    public UpdateTaskValidator()
    {
        RuleFor(x => x.Title)
            .Must(value => !value.IsSpecified || value.Value is not null)
            .WithErrorCode("not_nullable")
            .WithMessage("This field can't be cleared.");
        RuleFor(x => x.Title.Value)
            .Must(static title => !string.IsNullOrWhiteSpace(title))
            .WithErrorCode("not_empty")
            .WithMessage("'{PropertyName}' must not be empty.")
            .MaximumLength(500)
            .When(x => x.Title.IsSpecified && x.Title.Value is not null)
            .OverridePropertyName(nameof(UpdateTaskRequest.Title));

        RuleFor(x => x.Priority)
            .Must(value => !value.IsSpecified || value.Value is not null)
            .WithErrorCode("not_nullable")
            .WithMessage("This field can't be cleared.");

        RuleFor(x => x.ColumnId)
            .Must(value => !value.IsSpecified || value.Value is not null)
            .WithErrorCode("not_nullable")
            .WithMessage("This field can't be cleared.");

        RuleFor(x => x.Description.Value)
            .MaximumLength(10_000)
            .When(x => x.Description.IsSpecified && x.Description.Value is not null)
            .OverridePropertyName(nameof(UpdateTaskRequest.Description));

        RuleFor(x => x.EstimateDays.Value)
            .GreaterThan(0).LessThanOrEqualTo(260)
            .When(x => x.EstimateDays.IsSpecified && x.EstimateDays.Value.HasValue)
            .OverridePropertyName(nameof(UpdateTaskRequest.EstimateDays));
    }
}
