using System.Globalization;
using FluentAssertions;
using Worklane.Infrastructure.Services;

namespace Worklane.IntegrationTests.Auth;

public class WorkspaceSlugTests
{
    [Theory]
    [InlineData("พี่น้อง", "พี่น้อง-workspace")]
    [InlineData("กิ๊บ", "กิ๊บ-workspace")]
    [InlineData("ทีม Alpha", "ทีม-alpha-workspace")]
    [InlineData("Team 01", "team-01-workspace")]
    [InlineData("Alice", "alice-workspace")]
    [InlineData("Test User", "test-user-workspace")]
    public void ForDisplayName_KeepsLettersDigitsAndMarks(string displayName, string expected)
    {
        WorkspaceSlug.ForDisplayName(displayName).Should().Be(expected);
    }

    [Fact]
    public void ForDisplayName_KeepsSpacingCombiningMarks()
    {
        const char mark = '\u093E';
        CharUnicodeInfo.GetUnicodeCategory(mark).Should().Be(UnicodeCategory.SpacingCombiningMark);
        WorkspaceSlug.ForDisplayName("A" + mark).Should().Be("a" + mark + "-workspace");
    }

    [Theory]
    [InlineData("พี่น้อง", "พี่น้อง")]
    [InlineData("ทีม Alpha", "ทีม-alpha")]
    public void ForWorkspaceName_KeepsVowelsAndToneMarks(string name, string expected)
    {
        WorkspaceSlug.ForWorkspaceName(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("ิ่้๊๋")]
    [InlineData("@@@")]
    [InlineData("---")]
    public void EmptyWhitespaceMarksOrSymbols_FallBackToASafeSlug(string input)
    {
        WorkspaceSlug.ForDisplayName(input).Should().Be(WorkspaceSlug.Fallback);
        WorkspaceSlug.ForWorkspaceName(input).Should().Be(WorkspaceSlug.Fallback);
        WorkspaceSlug.Normalize(input).Should().BeNull();
    }

    [Fact]
    public void NullName_FallsBackToASafeSlug()
    {
        WorkspaceSlug.ForDisplayName(null).Should().Be(WorkspaceSlug.Fallback);
        WorkspaceSlug.ForWorkspaceName(null).Should().Be(WorkspaceSlug.Fallback);
    }

    [Fact]
    public void Normalize_FullwidthName_IsWorklane()
    {
        WorkspaceSlug.Normalize("Ｗｏｒｋｌａｎｅ").Should().Be("worklane");
        WorkspaceSlug.ForWorkspaceName("Ｗｏｒｋｌａｎｅ").Should().Be("worklane");
        WorkspaceSlug.ForDisplayName("Ｗｏｒｋｌａｎｅ").Should().Be("worklane-workspace");
    }

    [Theory]
    [InlineData("  Worklane  ")]
    [InlineData("\tＷｏｒｋｌａｎｅ\n")]
    [InlineData("\u3000Ｗｏｒｋｌａｎｅ\u3000")]
    public void Normalize_TrimsThenCompatibilityComposes(string value)
    {
        WorkspaceSlug.Normalize(value).Should().Be("worklane");
    }

    [Fact]
    public void ForDisplayName_TrimsWhitespaceBeforeLowercasing()
    {
        WorkspaceSlug.ForDisplayName("  Alice  ").Should().Be("alice-workspace");
    }
}
