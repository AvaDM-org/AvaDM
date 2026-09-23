using AvaDM.UI.Services;
using Xunit;

namespace AvaDM.UI.Tests;

public sealed class BulkImportParserTests
{
    [Fact]
    public void Parse_KeepsOneLinkPerLine_AcrossLineEndings()
    {
        var links = BulkImportParser.Parse("https://a.test/1.zip\r\nhttp://b.test/2.zip\n\nhttps://c.test/3.zip");

        Assert.Equal(
            new[] { "https://a.test/1.zip", "http://b.test/2.zip", "https://c.test/3.zip" },
            links.Select(l => l.AbsoluteUri));
    }

    [Fact]
    public void Parse_SkipsCommentsGarbageAndNonHttpSchemes()
    {
        var links = BulkImportParser.Parse("# my list\nnot a link\nftp://x.test/f.bin\nfile:///etc/passwd\n  https://a.test/ok.zip  ");

        Assert.Equal("https://a.test/ok.zip", Assert.Single(links).AbsoluteUri);
    }

    [Fact]
    public void Parse_CollapsesDuplicates_KeepingFirstOccurrence()
    {
        var links = BulkImportParser.Parse("https://a.test/1.zip\nhttps://b.test/2.zip\nhttps://a.test/1.zip");

        Assert.Equal(2, links.Count);
        Assert.Equal("https://a.test/1.zip", links[0].AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n")]
    public void Parse_EmptyInput_YieldsNothing(string? text) => Assert.Empty(BulkImportParser.Parse(text));
}
