namespace Worklane.Application.Common.Options;

public class FrontendSettings
{
    public const string SectionName = "Frontend";

    /// <summary>Origin the Angular app is served from — used to build absolute links (e.g.
    /// an invitation accept link) that have to work from outside the browser session,
    /// such as when clicked from an email client.</summary>
    public string BaseUrl { get; set; } = "http://localhost:4200";
}
