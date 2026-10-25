using FluentAssertions;
using Worklane.Infrastructure.Services;

namespace Worklane.IntegrationTests.Auth;

public class WorkspaceLocaleTests
{
    [Theory]
    [InlineData(null, null, "en")]
    [InlineData("", "", "en")]
    [InlineData("   ", null, "en")]
    [InlineData("th", null, "th")]
    [InlineData("TH", null, "th")]
    [InlineData("th-TH", "en", "th")]
    [InlineData("en", "th-TH,th;q=0.9", "en")]
    [InlineData("en-US", "th", "en")]
    [InlineData("fr", "th-TH", "en")]
    [InlineData(null, "th-TH,th;q=0.9,en;q=0.8", "th")]
    [InlineData(null, "en-US,en;q=0.9,th;q=0.8", "en")]
    [InlineData(null, "fr-FR,th;q=0.9,en;q=0.8", "th")]
    [InlineData(null, "th;q=0,en;q=0.8", "en")]
    [InlineData(null, "fr", "en")]
    public void Resolve_PrefersTheBodyThenAcceptLanguage(string? preferred, string? accept, string expected)
    {
        WorkspaceLocale.Resolve(preferred, accept).Should().Be(expected);
    }

    [Fact]
    public void ThaiSeed_UsesThaiWorkspaceNameAndColumns()
    {
        WorkspaceLocale.WorkspaceName("สมชาย", "th").Should().Be("เวิร์กสเปซของ สมชาย");
        WorkspaceLocale.ColumnsFor("th").Select(column => column.Name)
            .Should().Equal("ต้องทำ", "กำลังทำ", "เสร็จแล้ว");
        WorkspaceLocale.ColumnsFor("th").Single(column => column.IsInProgress).Name.Should().Be("กำลังทำ");
        WorkspaceLocale.ColumnsFor("th").Single(column => column.IsDone).Name.Should().Be("เสร็จแล้ว");
    }

    [Fact]
    public void EnglishSeed_StaysTheDefault()
    {
        WorkspaceLocale.WorkspaceName("Alice", "en").Should().Be("Alice's Workspace");
        WorkspaceLocale.ColumnsFor(null).Select(column => column.Name)
            .Should().Equal("To Do", "In Progress", "Done");
        WorkspaceLocale.ColumnsFor("en").Should().Equal(WorkspaceLocale.EnglishColumns);
    }
}
