using Clementine.Core;
using Xunit;

namespace Clementine.Tests;

public class ToolArgsTests
{
    [Theory]
    [InlineData("90", 90)]
    [InlineData("1:30", 90)]
    [InlineData("1:02:03", 3723)]
    [InlineData(" 0:05,5 ", 5.5)]
    public void Seconds(string text, double expected) => Assert.Equal(expected, ToolArgs.Seconds(text));

    [Fact]
    public void TimeRange_NeedsEndAfterStart()
    {
        Assert.Equal((30.0, 105.0), ToolArgs.TimeRange("0:30-1:45"));
        Assert.Equal((30.0, 105.0), ToolArgs.TimeRange("30 - 105"));
        Assert.Null(ToolArgs.TimeRange("1:45-0:30"));
        Assert.Null(ToolArgs.TimeRange("0:30"));
        Assert.Null(ToolArgs.TimeRange("a-b"));
    }

    [Theory]
    [InlineData("2 MB", 2L * 1024 * 1024)]
    [InlineData("1,5mb", 1572864L)]
    [InlineData("500 KB", 512000L)]
    [InlineData("800", 819200L)]
    public void Size(string text, long bytes) => Assert.Equal(bytes, ToolArgs.Size(text));

    [Theory]
    [InlineData("")]
    [InlineData("0 MB")]
    [InlineData("due mega")]
    public void Size_Invalid(string text) => Assert.Null(ToolArgs.Size(text));

    [Fact]
    public void Pages_KeepWrittenOrder_AndCheckBounds()
    {
        Assert.Equal([3, 1, 2], ToolArgs.Pages("3,1,2", 3));
        Assert.Equal([1, 2, 3, 5], ToolArgs.Pages("1-3, 5", 5));
        Assert.Equal([3, 2, 1], ToolArgs.Pages("3-1", 3)); // al contrario: pagine invertite
        Assert.Null(ToolArgs.Pages("4", 3));
        Assert.Null(ToolArgs.Pages("0", 3));
        Assert.Null(ToolArgs.Pages(",", 3));
    }
}
