using FluentValidation;
using Worklane.Application.Imports.Dtos;

namespace Worklane.Application.Imports.Validators;

public class UpdateImportMappingRequestValidator : AbstractValidator<UpdateImportMappingRequest>
{
    public UpdateImportMappingRequestValidator()
    {
        RuleFor(x => x.FallbackColumnId)
            .NotNull()
            .WithErrorCode("not_nullable")
            .WithMessage("'{PropertyName}' must not be empty.");

        RuleFor(x => x.Assignments)
            .NotNull()
            .WithErrorCode("not_nullable")
            .WithMessage("'{PropertyName}' must not be empty.");

        RuleFor(x => x.ExtensionData)
            .Must(data => data is null || !data.Keys.Any(key => key.Equals("preset", StringComparison.OrdinalIgnoreCase)))
            .WithErrorCode("invalid")
            .WithMessage("Preset cannot be changed.")
            .OverridePropertyName("Preset");

        RuleForEach(x => x.Assignments)
            .ChildRules(assignment =>
            {
                assignment.RuleFor(x => x.Header)
                    .NotEmpty()
                    .WithErrorCode("not_empty");
                assignment.RuleFor(x => x.Target)
                    .NotEmpty()
                    .WithErrorCode("not_empty")
                    .Must(ImportTargets.IsKnown)
                    .WithErrorCode("invalid")
                    .WithMessage("'{PropertyName}' is not a known target.");
            })
            .When(x => x.Assignments is not null);

        RuleFor(x => x.Assignments)
            .Must(assignments => assignments!.Select(a => a.Header).Distinct(StringComparer.Ordinal).Count() == assignments!.Count)
            .When(x => x.Assignments is not null)
            .WithErrorCode("invalid")
            .WithMessage("Each header can be mapped once.")
            .OverridePropertyName(nameof(UpdateImportMappingRequest.Assignments));

        RuleFor(x => x.Assignments)
            .Must(HasExactlyOne(ImportTargets.Title))
            .When(x => x.Assignments is not null)
            .WithErrorCode("invalid")
            .WithMessage("Map exactly one column to title.")
            .OverridePropertyName(nameof(UpdateImportMappingRequest.Assignments));

        RuleFor(x => x.Assignments)
            .Must(HasExactlyOne(ImportTargets.ExternalId))
            .When(x => x.Assignments is not null)
            .WithErrorCode("invalid")
            .WithMessage("Map exactly one column to externalId.")
            .OverridePropertyName(nameof(UpdateImportMappingRequest.Assignments));

        foreach (var target in ImportTargets.Single)
        {
            if (target is ImportTargets.Title or ImportTargets.ExternalId)
                continue;
            var captured = target;
            RuleFor(x => x.Assignments)
                .Must(assignments => assignments!.Count(a => a.Target == captured) <= 1)
                .When(x => x.Assignments is not null)
                .WithErrorCode("invalid")
                .WithMessage($"Map {captured} at most once.")
                .OverridePropertyName(nameof(UpdateImportMappingRequest.Assignments));
        }
    }

    private static Func<List<ColumnAssignment>?, bool> HasExactlyOne(string target) =>
        assignments => assignments!.Count(a => a.Target == target) == 1;
}
