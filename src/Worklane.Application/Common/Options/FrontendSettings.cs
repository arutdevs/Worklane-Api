namespace Worklane.Application.Common.Options;

public class FrontendSettings
{
    public const string SectionName = "Frontend";

    /// <summary>
    /// Origin the Angular app is served from. Invitation links are this origin plus
    /// <c>/invitations/accept?token=</c>. There is no code default: Development may omit it
    /// (the link is then path-only) or set a loopback origin, and every other environment
    /// must set an absolute http(s) URL whose host is not a loopback address.
    /// </summary>
    public string BaseUrl { get; set; } = "";
}
