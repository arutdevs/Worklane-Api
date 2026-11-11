using FluentValidation;
using Worklane.Application.Imports.Dtos;
using Worklane.Domain.Imports;

namespace Worklane.Application.Imports.Validators;

public class ExportTasksRequestValidator : AbstractValidator<ExportTasksRequest>
{
    public ExportTasksRequestValidator()
    {
        RuleFor(x => x.Format)
            .NotEmpty()
            .WithErrorCode("not_empty")
            .Must(format => ImportWire.TryParseFormat(format, out _))
            .WithErrorCode("invalid")
            .WithMessage("'{PropertyName}' must be csv or json.");
    }
}
