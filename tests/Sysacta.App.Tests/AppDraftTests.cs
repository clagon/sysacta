using Sysacta.App.ViewModels;

namespace Sysacta.App.Tests;

public class AppDraftTests
{
    public static TheoryData<string[]> Arguments => new(
        [],
        [""],
        ["", ""],
        ["--profile", ""],
        ["", "--profile"],
        ["a b", "", "c\"d", @"tail\"]);

    [Theory]
    [MemberData(nameof(Arguments))]
    public void Re_approval_shows_the_fixed_arguments_so_that_they_parse_back_unchanged(string[] args) =>
        Assert.Equal(args, AppDraft.ParseArguments(AppDraft.FormatArguments(args)));

    [Fact]
    public void Typed_text_is_one_argument_per_line_with_one_trailing_line_break_ignored()
    {
        Assert.Empty(AppDraft.ParseArguments(""));
        Assert.Equal(["a"], AppDraft.ParseArguments("a\r\n"));
        Assert.Equal(["a", ""], AppDraft.ParseArguments("a\n\n"));
        Assert.Equal(["a", "b"], AppDraft.ParseArguments("a\r\nb"));
    }
}
