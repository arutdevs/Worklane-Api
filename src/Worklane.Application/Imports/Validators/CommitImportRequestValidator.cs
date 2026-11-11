using FluentValidation;
using Worklane.Application.Imports.Dtos;

namespace Worklane.Application.Imports.Validators;

public class CommitImportRequestValidator : AbstractValidator<CommitImportRequest>
{
    public CommitImportRequestValidator()
    {
        RuleFor(x => x.SkipInvalidRows)
            .NotNull()
            .WithErrorCode("not_nullable")
            .WithMessage("'{PropertyName}' must not be empty.");
    }
}
