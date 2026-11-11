namespace Worklane.Application.Common.Options;

public class ImportUploadRateLimitSettings
{
    public const string SectionName = "RateLimit:ImportUpload";
    public const string PolicyName = "import-upload";

    public int PermitLimit { get; set; } = 10;
    public int WindowSeconds { get; set; } = 3600;
}

public class ImportPreviewRateLimitSettings
{
    public const string SectionName = "RateLimit:ImportPreview";
    public const string PolicyName = "import-preview";

    public int PermitLimit { get; set; } = 60;
    public int WindowSeconds { get; set; } = 3600;
}

public class ImportCommitRateLimitSettings
{
    public const string SectionName = "RateLimit:ImportCommit";
    public const string PolicyName = "import-commit";

    public int PermitLimit { get; set; } = 10;
    public int WindowSeconds { get; set; } = 3600;
}

public class ExportRateLimitSettings
{
    public const string SectionName = "RateLimit:Export";
    public const string PolicyName = "export";

    public int PermitLimit { get; set; } = 30;
    public int WindowSeconds { get; set; } = 3600;
}
