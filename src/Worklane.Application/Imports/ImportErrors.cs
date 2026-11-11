using System.Globalization;

namespace Worklane.Application.Imports;

/// <summary>
/// Stable problem codes for import and export. HTTP details match docs/api-error-codes.md.
/// Row codes are returned on the row, not as the HTTP <c>code</c>.
/// </summary>
public static class ImportErrors
{
    public const string NoRows = "import_no_rows";
    public const string NoRowsDetail = "The file has no data rows.";

    public const string DelimiterUnsupported = "import_delimiter_unsupported";
    public const string DelimiterUnsupportedDetail = "Save the file as comma-separated CSV.";

    public const string Unreadable = "import_unreadable";
    public const string UnreadableDetail = "The file could not be read.";

    public const string RowLimit = "import_row_limit";
    public const string ColumnLimit = "import_column_limit";

    public const string BatchLimit = "import_batch_limit";
    public const string MappingRequired = "import_mapping_required";
    public const string MappingRequiredDetail = "Map the columns before continuing.";

    public const string HasErrors = "import_has_errors";
    public const string ColumnMissing = "import_column_missing";
    public const string ColumnMissingDetail = "The fallback column is no longer in this project.";

    public const string AlreadyCommitted = "import_already_committed";
    public const string AlreadyCommittedDetail = "This import was already committed.";

    public const string AlreadyUndone = "import_already_undone";
    public const string AlreadyUndoneDetail = "This import was already undone.";

    public const string NotCommitted = "import_not_committed";
    public const string NotCommittedDetail = "Commit the import before undoing it.";

    public const string Expired = "import_expired";
    public const string ExpiredDetail = "This import has expired.";

    public const string Timeout = "import_timeout";
    public const string TimeoutDetail = "The import took too long. Try again.";

    public const string ExternalIdTaken = "import_external_id_taken";
    public const string ExternalIdTakenDetail = "A card with this external id already exists for this project and source.";

    public const string NoColumnsDetail = "Project has no columns. Create a column first.";

    public const string ExternalIdMissing = "import_external_id_missing";
    public const string ExternalIdDuplicate = "import_external_id_duplicate";
    public const string TitleMissing = "import_title_missing";
    public const string TitleTooLong = "import_title_too_long";
    public const string DescriptionTooLong = "import_description_too_long";
    public const string DateInvalid = "import_date_invalid";
    public const string EstimateInvalid = "import_estimate_invalid";
    public const string FormulaUncached = "import_formula_uncached";
    public const string ParentAmbiguous = "import_parent_ambiguous";
    public const string ParentUnresolved = "import_parent_unresolved";
    public const string AssigneeSkipped = "import_assignee_skipped";
    public const string ColumnFallback = "import_column_fallback";
    public const string PriorityDefaulted = "import_priority_defaulted";
    public const string FieldIgnored = "import_field_ignored";
    public const string ExtraSheets = "import_extra_sheets";
    public const string ParentColumnAssumed = "import_parent_column_assumed";

    public const string NotFoundDetail = "Resource was not found.";

    public const int TitleMaxLength = 500;
    public const int DescriptionMaxLength = 10_000;

    public static string RowLimitDetail(int maxRows) =>
        string.Format(CultureInfo.InvariantCulture, "The file has more than {0} rows.", maxRows);

    public static string ColumnLimitDetail(int maxColumns) =>
        string.Format(CultureInfo.InvariantCulture, "The file has more than {0} columns.", maxColumns);

    public static string BatchLimitDetail(int limit) =>
        string.Format(CultureInfo.InvariantCulture, "This project already has {0} imports in progress.", limit);

    public static string HasErrorsDetail(int invalidCount) =>
        string.Format(CultureInfo.InvariantCulture, "The import has {0} row(s) to fix or skip.", invalidCount);
}
